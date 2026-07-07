using System.Numerics;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>Outcome of a GMX v2 order submission.</summary>
public sealed record GmxOrderResult(string TxHash, bool Success, bool ApproveWasRequired, string? ApproveTxHash);

/// <summary>
/// Submits GMX v2 perp orders on Arbitrum by building the ExchangeRouter multicall
/// (sendWnt + sendTokens + createOrder), signing and sending it through the account-bound Web3
/// (non-custodial — the key stays here).
///
/// GATED: real order placement requires <c>enableLiveOrders:true</c> (default OFF). GMX v2
/// addresses and the createOrder struct layout are version-sensitive (see <see cref="GmxArbitrum"/>);
/// verify them and validate on a funded account before enabling. Amounts are passed in raw
/// on-chain precision (sizeDeltaUsd = 1e30 USD, collateral = token smallest units, price in GMX
/// precision) so the client never guesses scaling.
/// </summary>
public sealed class GmxOrderClient
{
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";

    private readonly Web3 _web3;
    private readonly Account _account;
    private readonly bool _enableLiveOrders;

    public GmxOrderClient(string privateKey, string rpcUrl, bool enableLiveOrders = false)
    {
        _account = new Account(privateKey);
        _web3 = new Web3(_account, rpcUrl);
        _enableLiveOrders = enableLiveOrders;
    }

    /// <summary>
    /// Opens/increases a position. <paramref name="sizeDeltaUsd"/> is 1e30-scaled USD,
    /// <paramref name="collateralAmountRaw"/> is USDC in 6-decimals, <paramref name="acceptablePrice"/>
    /// is GMX price precision, <paramref name="executionFeeWei"/> pays the keeper.
    /// </summary>
    public async Task<GmxOrderResult> PlaceMarketIncreaseAsync(
        string market, bool isLong, BigInteger sizeDeltaUsd, BigInteger collateralAmountRaw,
        BigInteger acceptablePrice, BigInteger executionFeeWei, CancellationToken ct = default)
    {
        if (!_enableLiveOrders)
        {
            throw new NotSupportedException(
                "GMX live order placement is disabled in this build. The ExchangeRouter multicall builder " +
                "is ready, but verify the GMX v2 addresses + createOrder struct layout against the current " +
                "deployment and validate on a funded account before enabling (enableLiveOrders:true).");
        }

        var approve = await EnsureUsdcAllowanceAsync(collateralAmountRaw, ct);

        var multicall = new GmxMulticallFunction
        {
            Data = BuildIncreaseCalldata(market, isLong, sizeDeltaUsd, collateralAmountRaw, acceptablePrice, executionFeeWei),
            AmountToSend = new HexBigInteger(executionFeeWei),
        };

        var handler = _web3.Eth.GetContractTransactionHandler<GmxMulticallFunction>();
        var receipt = await handler.SendRequestAndWaitForReceiptAsync(GmxArbitrum.ExchangeRouter, multicall, ct);

        return new GmxOrderResult(
            receipt.TransactionHash, receipt.Status?.Value == 1, approve.Required, approve.TxHash);
    }

    /// <summary>Builds the three-call multicall payload. Pure given inputs — encodes via the ABI.</summary>
    public List<byte[]> BuildIncreaseCalldata(
        string market, bool isLong, BigInteger sizeDeltaUsd, BigInteger collateralAmountRaw,
        BigInteger acceptablePrice, BigInteger executionFeeWei)
    {
        var sendWnt = new GmxSendWntFunction { Receiver = GmxArbitrum.OrderVault, Amount = executionFeeWei };
        var sendTokens = new GmxSendTokensFunction { Token = GmxArbitrum.Usdc, Receiver = GmxArbitrum.OrderVault, Amount = collateralAmountRaw };
        var createOrder = new GmxCreateOrderFunction
        {
            Params = new GmxCreateOrderParams
            {
                Addresses = new GmxCreateOrderAddresses
                {
                    Receiver = _account.Address,
                    CancellationReceiver = ZeroAddress,
                    CallbackContract = ZeroAddress,
                    UiFeeReceiver = ZeroAddress,
                    Market = market,
                    InitialCollateralToken = GmxArbitrum.Usdc,
                    SwapPath = new List<string>(),
                },
                Numbers = new GmxCreateOrderNumbers
                {
                    SizeDeltaUsd = sizeDeltaUsd,
                    InitialCollateralDeltaAmount = collateralAmountRaw,
                    TriggerPrice = 0,
                    AcceptablePrice = acceptablePrice,
                    ExecutionFee = executionFeeWei,
                    CallbackGasLimit = 0,
                    MinOutputAmount = 0,
                    ValidFromTime = 0,
                },
                OrderType = (byte)GmxOrderType.MarketIncrease,
                DecreasePositionSwapType = 0,
                IsLong = isLong,
                ShouldUnwrapNativeToken = false,
                AutoCancel = false,
                ReferralCode = new byte[32],
            },
        };

        return new List<byte[]>
        {
            sendWnt.GetCallData(),
            sendTokens.GetCallData(),
            createOrder.GetCallData(),
        };
    }

    private async Task<(bool Required, string? TxHash)> EnsureUsdcAllowanceAsync(BigInteger amount, CancellationToken ct)
    {
        var allowanceHandler = _web3.Eth.GetContractQueryHandler<AllowanceFunction>();
        var allowance = await allowanceHandler.QueryAsync<BigInteger>(
            GmxArbitrum.Usdc, new AllowanceFunction { Owner = _account.Address, Spender = GmxArbitrum.Router });
        if (allowance >= amount)
        {
            return (false, null);
        }

        var approveHandler = _web3.Eth.GetContractTransactionHandler<ApproveFunction>();
        var receipt = await approveHandler.SendRequestAndWaitForReceiptAsync(
            GmxArbitrum.Usdc, new ApproveFunction { Spender = GmxArbitrum.Router, Amount = amount }, ct);
        return (true, receipt.TransactionHash);
    }
}
