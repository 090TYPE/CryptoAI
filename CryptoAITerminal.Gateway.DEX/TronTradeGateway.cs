using System.Globalization;
using System.Numerics;
using System.Reactive.Subjects;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

public sealed class TronTradeGateway : IDexTradeGateway
{
    public const string DefaultUsdtContractAddress = "TXLAQ63Xg1NAzckPwKHvzw7CSEmLMEqcdj";
    public const string DefaultUsdcContractAddress = "TEkxiTehnzSmSe2XqrBj4w32RUN966rdz8";
    private const string SunSwapRouterAddress = "TKzxdSv2FZKQrEqkKVgp5DcwEXBEKMg2Ax";
    private const string WrappedTrxContractAddress = "TNUC9Qb1rRpS5CbWLmNMxXBjyFoydXjWFR";
    private const decimal SunPerTrx = 1_000_000m;

    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly TronWalletClient _walletClient;
    private readonly string _walletAddress;
    private readonly string _privateKey;

    public TronTradeGateway(string privateKey, string? rpcUrl = null)
    {
        _privateKey = privateKey;
        _walletAddress = TronAddressCodec.DeriveAddress(privateKey);
        _walletClient = new TronWalletClient(rpcUrl: rpcUrl);
    }

    public string NetworkName => "Tron";
    public string NativeSymbol => "TRX";
    public string SupportedDexesLabel => "sunswap";
    public IReadOnlyList<DexQuoteAssetOption> SupportedQuoteAssets => DexQuoteAssetCatalog.GetOptions(NetworkName);
    public string WalletAddress => _walletAddress;
    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    public Task ConnectAsync() => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (string.IsNullOrWhiteSpace(order.Symbol))
        {
            throw new ArgumentException("Tron DEX orders expect the token contract address in Symbol.", nameof(order));
        }

        if (order.Quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(order.Quantity), "Tron DEX order quantity must be greater than zero.");
        }

        var transactionHash = order.Side switch
        {
            CryptoAITerminal.Core.Enums.OrderSide.Buy => await BuyTokenAsync(order.Symbol, order.Quantity, 5m, null, null),
            CryptoAITerminal.Core.Enums.OrderSide.Sell => await SellTokenAsync(order.Symbol, order.Quantity, 5m, null, null),
            _ => throw new NotSupportedException($"Unsupported Tron DEX order side '{order.Side}'.")
        };

        order.Id = transactionHash;
        order.ClientOrderId = transactionHash;
        order.ExchangeType = "sunswap";
        order.TimeInForce = "IOC";
        order.FilledQuantity = order.Quantity;
        order.Status = CryptoAITerminal.Core.Enums.OrderStatus.Filled;
        return order;
    }

    public Task CancelOrderAsync(string orderId) => Task.CompletedTask;

    public async Task<decimal> GetBalanceAsync(string asset)
    {
        if (asset.Equals("TRX", StringComparison.OrdinalIgnoreCase))
        {
            return await _walletClient.GetNativeBalanceAsync(_walletAddress);
        }

        var quoteAsset = ResolveQuoteAsset(asset, requireExactSymbol: false);
        if (quoteAsset is not null && !quoteAsset.IsNative && !string.IsNullOrWhiteSpace(quoteAsset.ContractAddress))
        {
            return await _walletClient.GetTrc20BalanceAsync(_walletAddress, quoteAsset.ContractAddress);
        }

        return 0m;
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        await Task.CompletedTask;
        return new OrderBook { Symbol = symbol, Bids = new(), Asks = new() };
    }

    public bool SupportsDex(string? dexId) =>
        string.IsNullOrWhiteSpace(dexId) || dexId.Equals("sunswap", StringComparison.OrdinalIgnoreCase);

    public async Task<DexBuyExecutionResult> ExecuteConfirmedBuyAsync(DexBuyExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateDex(request.DexId);
        if (string.IsNullOrWhiteSpace(request.TokenAddress))
        {
            throw new ArgumentException("Token address is required for Tron buys.", nameof(request));
        }

        if (request.NativeAmountToSpend <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request.NativeAmountToSpend), "Spend amount must be greater than zero.");
        }

        var tokenDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
        var tokenBalanceBefore = await GetTokenBalanceAsync(request.TokenAddress);
        var nativeBalanceBefore = await GetBalanceAsync(NativeSymbol);
        var quoteAsset = ResolveQuoteAsset(request.SpendAssetSymbol, requireExactSymbol: false);
        var boundedSlippage = Math.Clamp(request.SlippagePercent, 0m, 99m);

        if (quoteAsset is not null && !quoteAsset.IsNative)
        {
            var quoteDecimals = await GetTokenDecimalsAsync(quoteAsset.ContractAddress!);
            var quoteBalanceBefore = await _walletClient.GetTrc20BalanceAsync(_walletAddress, quoteAsset.ContractAddress!);
            var quoteAmountRaw = ScaleUp(request.NativeAmountToSpend, quoteDecimals);
            var allowance = await EnsureAllowanceAsync(quoteAsset.ContractAddress!, quoteAmountRaw);
            var path = BuildPath(quoteAsset, request.TokenAddress);
            var expectedTokenRaw = await QueryAmountsOutRawAsync(quoteAmountRaw, path);
            var minimumTokenRaw = ApplySlippage(expectedTokenRaw, boundedSlippage);
            var swapParameters = TronWalletClient.EncodeSwapExactTokensParameters(
                quoteAmountRaw,
                minimumTokenRaw,
                path,
                _walletAddress,
                BuildDeadlineUnix());
            var invocation = await _walletClient.ExecuteSmartContractAsync(
                _privateKey,
                _walletAddress,
                SunSwapRouterAddress,
                "swapExactTokensForTokensSupportingFeeOnTransferTokens(uint256,uint256,address[],address,uint256)",
                swapParameters);

            var tokenBalanceAfter = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBefore);
            var quoteBalanceAfter = await WaitForTokenBalanceChangeAsync(quoteAsset.ContractAddress!, quoteBalanceBefore);
            var nativeBalanceAfter = await GetBalanceAsync(NativeSymbol);
            var expectedTokenAmount = ScaleDown(expectedTokenRaw, tokenDecimals);
            var minimumTokenAmount = ScaleDown(minimumTokenRaw, tokenDecimals);
            var actualTokenAmount = Math.Max(0m, tokenBalanceAfter - tokenBalanceBefore);
            var spendAssetAmount = Math.Max(0m, quoteBalanceBefore - quoteBalanceAfter);
            var balanceVerified = actualTokenAmount > 0m;
            var hasUnexpectedOutput = balanceVerified && minimumTokenAmount > 0m && actualTokenAmount < minimumTokenAmount;
            var suspectedPartialFill = balanceVerified && expectedTokenAmount > 0m && actualTokenAmount < (expectedTokenAmount * 0.98m);

            return new DexBuyExecutionResult(
                invocation.TransactionId,
                invocation.Confirmed && balanceVerified && !hasUnexpectedOutput,
                balanceVerified,
                hasUnexpectedOutput,
                suspectedPartialFill,
                tokenDecimals,
                spendAssetAmount,
                nativeBalanceBefore,
                nativeBalanceAfter,
                expectedTokenAmount,
                minimumTokenAmount,
                actualTokenAmount,
                tokenBalanceBefore,
                tokenBalanceAfter,
                $"Tron buy {invocation.TransactionId[..Math.Min(10, invocation.TransactionId.Length)]} on sunswap: spent {spendAssetAmount:0.########} {quoteAsset.Symbol}, received {actualTokenAmount:0.########} tokens.",
                "sunswap",
                invocation.FeeSun / SunPerTrx + allowance.FeeNative,
                invocation.EnergyUsed,
                null,
                ReceiptParsed: invocation.Confirmed,
                DecimalsVerified: tokenDecimals > 0 && quoteDecimals > 0,
                SlippageProtected: minimumTokenAmount > 0m,
                BalanceSynchronized: balanceVerified,
                SpendAssetSymbol: quoteAsset.Symbol,
                SpendAssetAmount: spendAssetAmount,
                UsedQuoteAsset: true);
        }

        var trxAmountSun = ToSun(request.NativeAmountToSpend);
        var pathToToken = BuildPath(null, request.TokenAddress);
        var expectedTokenOutRaw = await QueryAmountsOutRawAsync(trxAmountSun, pathToToken);
        var minimumTokenOutRaw = ApplySlippage(expectedTokenOutRaw, boundedSlippage);
        var parameters = TronWalletClient.EncodeSwapExactNativeForTokensParameters(
            minimumTokenOutRaw,
            pathToToken,
            _walletAddress,
            BuildDeadlineUnix());
        var swapInvocation = await _walletClient.ExecuteSmartContractAsync(
            _privateKey,
            _walletAddress,
            SunSwapRouterAddress,
            "swapExactETHForTokensSupportingFeeOnTransferTokens(uint256,address[],address,uint256)",
            parameters,
            callValueSun: (long)trxAmountSun);

        var tokenBalanceAfterNative = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBefore);
        var nativeBalanceAfterNative = await GetBalanceAsync(NativeSymbol);
        var expectedTokenAmountNative = ScaleDown(expectedTokenOutRaw, tokenDecimals);
        var minimumTokenAmountNative = ScaleDown(minimumTokenOutRaw, tokenDecimals);
        var actualTokenAmountNative = Math.Max(0m, tokenBalanceAfterNative - tokenBalanceBefore);
        var nativeAmountSpent = Math.Max(0m, nativeBalanceBefore - nativeBalanceAfterNative);
        var balanceVerifiedNative = actualTokenAmountNative > 0m;
        var hasUnexpectedOutputNative = balanceVerifiedNative && minimumTokenAmountNative > 0m && actualTokenAmountNative < minimumTokenAmountNative;
        var suspectedPartialFillNative = balanceVerifiedNative && expectedTokenAmountNative > 0m && actualTokenAmountNative < (expectedTokenAmountNative * 0.98m);

        return new DexBuyExecutionResult(
            swapInvocation.TransactionId,
            swapInvocation.Confirmed && balanceVerifiedNative && !hasUnexpectedOutputNative,
            balanceVerifiedNative,
            hasUnexpectedOutputNative,
            suspectedPartialFillNative,
            tokenDecimals,
            nativeAmountSpent,
            nativeBalanceBefore,
            nativeBalanceAfterNative,
            expectedTokenAmountNative,
            minimumTokenAmountNative,
            actualTokenAmountNative,
            tokenBalanceBefore,
            tokenBalanceAfterNative,
            $"Tron buy {swapInvocation.TransactionId[..Math.Min(10, swapInvocation.TransactionId.Length)]} on sunswap: spent {nativeAmountSpent:0.########} TRX, received {actualTokenAmountNative:0.########} tokens.",
            "sunswap",
            swapInvocation.FeeSun / SunPerTrx,
            swapInvocation.EnergyUsed,
            null,
            ReceiptParsed: swapInvocation.Confirmed,
            DecimalsVerified: tokenDecimals > 0,
            SlippageProtected: minimumTokenAmountNative > 0m,
            BalanceSynchronized: balanceVerifiedNative,
            SpendAssetSymbol: NativeSymbol,
            SpendAssetAmount: nativeAmountSpent,
            UsedQuoteAsset: false);
    }

    public async Task<DexSellExecutionResult> ExecuteConfirmedSellAsync(DexSellExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateDex(request.DexId);
        if (string.IsNullOrWhiteSpace(request.TokenAddress))
        {
            throw new ArgumentException("Token address is required for Tron sells.", nameof(request));
        }

        if (request.TokenAmountToSell <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TokenAmountToSell), "Token amount must be greater than zero.");
        }

        var tokenDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
        var tokenBalanceBefore = await GetTokenBalanceAsync(request.TokenAddress);
        var nativeBalanceBefore = await GetBalanceAsync(NativeSymbol);
        var amountInRaw = ScaleUp(request.TokenAmountToSell, tokenDecimals);
        var allowanceResult = await EnsureAllowanceAsync(request.TokenAddress, amountInRaw);
        var boundedSlippage = Math.Clamp(request.SlippagePercent, 0m, 99m);
        var receiveAsset = ResolveQuoteAsset(request.ReceiveAssetSymbol, requireExactSymbol: false);

        if (receiveAsset is not null && !receiveAsset.IsNative)
        {
            var quoteDecimals = await GetTokenDecimalsAsync(receiveAsset.ContractAddress!);
            var quoteBalanceBefore = await _walletClient.GetTrc20BalanceAsync(_walletAddress, receiveAsset.ContractAddress!);
            var path = BuildPath(null, request.TokenAddress, reverse: true, quoteAssetOverride: receiveAsset);
            var expectedQuoteOutRaw = await QueryAmountsOutRawAsync(amountInRaw, path);
            var minimumQuoteOutRaw = ApplySlippage(expectedQuoteOutRaw, boundedSlippage);
            var swapParameters = TronWalletClient.EncodeSwapExactTokensParameters(
                amountInRaw,
                minimumQuoteOutRaw,
                path,
                _walletAddress,
                BuildDeadlineUnix());
            var invocation = await _walletClient.ExecuteSmartContractAsync(
                _privateKey,
                _walletAddress,
                SunSwapRouterAddress,
                "swapExactTokensForTokensSupportingFeeOnTransferTokens(uint256,uint256,address[],address,uint256)",
                swapParameters);

            var tokenBalanceAfter = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBefore);
            var quoteBalanceAfter = await WaitForTokenBalanceChangeAsync(receiveAsset.ContractAddress!, quoteBalanceBefore);
            var nativeBalanceAfter = await GetBalanceAsync(NativeSymbol);
            var actualTokenAmountSold = Math.Max(0m, tokenBalanceBefore - tokenBalanceAfter);
            var actualQuoteReceived = Math.Max(0m, quoteBalanceAfter - quoteBalanceBefore);
            var expectedQuoteAmount = ScaleDown(expectedQuoteOutRaw, quoteDecimals);
            var minimumQuoteAmount = ScaleDown(minimumQuoteOutRaw, quoteDecimals);
            var balanceVerified = actualTokenAmountSold > 0m || actualQuoteReceived > 0m;

            return new DexSellExecutionResult(
                invocation.TransactionId,
                invocation.Confirmed && balanceVerified,
                balanceVerified,
                tokenDecimals,
                request.TokenAmountToSell,
                actualTokenAmountSold,
                actualQuoteReceived,
                minimumQuoteAmount,
                actualQuoteReceived,
                nativeBalanceBefore,
                nativeBalanceAfter,
                tokenBalanceBefore,
                tokenBalanceAfter,
                $"Confirmed Tron sell on sunswap. Sold {actualTokenAmountSold:0.########} tokens for {actualQuoteReceived:0.########} {receiveAsset.Symbol}.",
                "sunswap",
                invocation.FeeSun / SunPerTrx + allowanceResult.FeeNative,
                invocation.EnergyUsed,
                null,
                ReceiptParsed: invocation.Confirmed,
                DecimalsVerified: tokenDecimals > 0 && quoteDecimals > 0,
                SlippageProtected: minimumQuoteAmount > 0m,
                BalanceSynchronized: balanceVerified,
                ApproveWasRequired: allowanceResult.WasRequired,
                ApproveTransactionHash: allowanceResult.TransactionHash,
                ApproveFeeNative: allowanceResult.FeeNative,
                ReceiveAssetSymbol: receiveAsset.Symbol,
                ActualReceiveAmount: actualQuoteReceived,
                ExpectedReceiveAmount: expectedQuoteAmount,
                MinimumReceiveAmount: minimumQuoteAmount,
                UsedQuoteAsset: true);
        }

        var pathToNative = BuildPath(null, request.TokenAddress, reverse: true);
        var expectedNativeOutRaw = await QueryAmountsOutRawAsync(amountInRaw, pathToNative);
        var minimumNativeOutRaw = ApplySlippage(expectedNativeOutRaw, boundedSlippage);
        var nativeAmountBeforeSwap = nativeBalanceBefore;
        var swapParametersNative = TronWalletClient.EncodeSwapExactTokensParameters(
            amountInRaw,
            minimumNativeOutRaw,
            pathToNative,
            _walletAddress,
            BuildDeadlineUnix());
        var nativeInvocation = await _walletClient.ExecuteSmartContractAsync(
            _privateKey,
            _walletAddress,
            SunSwapRouterAddress,
            "swapExactTokensForETHSupportingFeeOnTransferTokens(uint256,uint256,address[],address,uint256)",
            swapParametersNative);

        var tokenBalanceAfterNative = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBefore);
        var nativeBalanceAfterNative = await GetBalanceAsync(NativeSymbol);
        var actualTokenAmountSoldNative = Math.Max(0m, tokenBalanceBefore - tokenBalanceAfterNative);
        var actualNativeReceived = Math.Max(0m, nativeBalanceAfterNative - nativeAmountBeforeSwap);
        var expectedNativeAmount = FromSun(expectedNativeOutRaw);
        var minimumNativeAmount = FromSun(minimumNativeOutRaw);
        var balanceVerifiedNative = actualTokenAmountSoldNative > 0m || actualNativeReceived > 0m;

        return new DexSellExecutionResult(
            nativeInvocation.TransactionId,
            nativeInvocation.Confirmed && balanceVerifiedNative,
            balanceVerifiedNative,
            tokenDecimals,
            request.TokenAmountToSell,
            actualTokenAmountSoldNative,
            expectedNativeAmount,
            minimumNativeAmount,
            actualNativeReceived,
            nativeBalanceBefore,
            nativeBalanceAfterNative,
            tokenBalanceBefore,
            tokenBalanceAfterNative,
            $"Confirmed Tron sell on sunswap. Sold {actualTokenAmountSoldNative:0.########} tokens for {actualNativeReceived:0.########} TRX.",
            "sunswap",
            nativeInvocation.FeeSun / SunPerTrx + allowanceResult.FeeNative,
            nativeInvocation.EnergyUsed,
            null,
            ReceiptParsed: nativeInvocation.Confirmed,
            DecimalsVerified: tokenDecimals > 0,
            SlippageProtected: minimumNativeAmount > 0m,
            BalanceSynchronized: balanceVerifiedNative,
            ApproveWasRequired: allowanceResult.WasRequired,
            ApproveTransactionHash: allowanceResult.TransactionHash,
            ApproveFeeNative: allowanceResult.FeeNative,
            ReceiveAssetSymbol: NativeSymbol,
            ActualReceiveAmount: actualNativeReceived,
            ExpectedReceiveAmount: expectedNativeAmount,
            MinimumReceiveAmount: minimumNativeAmount,
            UsedQuoteAsset: false);
    }

    public async Task<decimal> GetTokenPriceInNativeAsync(string tokenAddress, decimal nativeAmount, string? dexId = null)
    {
        ValidateDex(dexId);
        if (nativeAmount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeAmount), "TRX amount must be greater than zero.");
        }

        var tokenDecimals = await GetTokenDecimalsAsync(tokenAddress);
        var amountOutRaw = await QueryAmountsOutRawAsync(ToSun(nativeAmount), BuildPath(null, tokenAddress));
        return ScaleDown(amountOutRaw, tokenDecimals);
    }

    public async Task<string> BuyTokenAsync(string tokenAddress, decimal nativeAmountToSpend, decimal slippagePercent = 5, string? dexId = null, string? spendAssetSymbol = null)
    {
        var result = await ExecuteConfirmedBuyAsync(new DexBuyExecutionRequest(tokenAddress, nativeAmountToSpend, slippagePercent, dexId, spendAssetSymbol));
        return result.TransactionHash;
    }

    public async Task<string> SellTokenAsync(string tokenAddress, decimal tokenAmountToSell, decimal slippagePercent = 5, string? dexId = null, string? receiveAssetSymbol = null)
    {
        var result = await ExecuteConfirmedSellAsync(new DexSellExecutionRequest(tokenAddress, tokenAmountToSell, slippagePercent, dexId, receiveAssetSymbol));
        return result.TransactionHash;
    }

    public Task<int> GetTokenDecimalsAsync(string tokenAddress) =>
        _walletClient.GetTrc20DecimalsAsync(tokenAddress, _walletAddress);

    public Task<decimal> GetTokenBalanceAsync(string tokenAddress) =>
        _walletClient.GetTrc20BalanceAsync(_walletAddress, tokenAddress);

    public async Task<DexSellabilityProbeResult> ProbeSellabilityAsync(DexSellabilityProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateDex(request.DexId);
        if (string.IsNullOrWhiteSpace(request.TokenAddress))
        {
            throw new ArgumentException("Token address is required for Tron sellability probes.", nameof(request));
        }

        try
        {
            var quoteAsset = ResolveQuoteAsset(request.QuoteAssetSymbol, requireExactSymbol: false);
            var boundedSlippage = Math.Clamp(request.SlippagePercent, 0m, 99m);
            var ownedBalance = await GetTokenBalanceAsync(request.TokenAddress);
            var requestedTokenAmount = Math.Max(0m, request.TokenAmountToSell ?? 0m);

            if (requestedTokenAmount > 0m || ownedBalance > 0m)
            {
                var amountToCheck = requestedTokenAmount > 0m ? requestedTokenAmount : ownedBalance;
                if (ownedBalance < amountToCheck)
                {
                    return new DexSellabilityProbeResult(
                        false,
                        false,
                        true,
                        amountToCheck,
                        0m,
                        null,
                        $"Tron sell simulation blocked: wallet holds {ownedBalance:0.########} tokens, below requested {amountToCheck:0.########}.",
                        quoteAsset?.Symbol ?? NativeSymbol);
                }

                var tokenDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
                var amountInRaw = ScaleUp(amountToCheck, tokenDecimals);
                var path = BuildPath(null, request.TokenAddress, reverse: true, quoteAssetOverride: quoteAsset);
                var expectedOutRaw = await QueryAmountsOutRawAsync(amountInRaw, path);
                var expectedOut = quoteAsset is not null && !quoteAsset.IsNative
                    ? ScaleDown(expectedOutRaw, await GetTokenDecimalsAsync(quoteAsset.ContractAddress!))
                    : FromSun(expectedOutRaw);
                var receiveSymbol = quoteAsset?.Symbol ?? NativeSymbol;
                return new DexSellabilityProbeResult(
                    expectedOut > 0m,
                    true,
                    true,
                    amountToCheck,
                    expectedOut,
                    null,
                    $"SunSwap quote indicates {amountToCheck:0.########} tokens can be sold for about {expectedOut:0.########} {receiveSymbol} with {boundedSlippage:0.##}% slippage protection.",
                    receiveSymbol);
            }

            var buyAmount = Math.Max(0m, quoteAsset is not null && !quoteAsset.IsNative
                ? request.QuoteAmountToProbe ?? 0m
                : request.NativeAmountToProbe ?? 0m);
            if (buyAmount <= 0m)
            {
                return new DexSellabilityProbeResult(
                    false,
                    false,
                    false,
                    0m,
                    0m,
                    null,
                    "Provide a token amount to sell or a probe amount to estimate a round trip.",
                    quoteAsset?.Symbol ?? NativeSymbol);
            }

            var inputRaw = quoteAsset is not null && !quoteAsset.IsNative
                ? ScaleUp(buyAmount, await GetTokenDecimalsAsync(quoteAsset.ContractAddress!))
                : ToSun(buyAmount);
            var buyPath = BuildPath(quoteAsset, request.TokenAddress);
            var tokenOutRaw = await QueryAmountsOutRawAsync(inputRaw, buyPath);
            var tokenDecimalsForProbe = await GetTokenDecimalsAsync(request.TokenAddress);
            var tokenAmountChecked = ScaleDown(tokenOutRaw, tokenDecimalsForProbe);
            var sellPath = BuildPath(null, request.TokenAddress, reverse: true, quoteAssetOverride: quoteAsset);
            var expectedExitRaw = await QueryAmountsOutRawAsync(tokenOutRaw, sellPath);
            var expectedExit = quoteAsset is not null && !quoteAsset.IsNative
                ? ScaleDown(expectedExitRaw, await GetTokenDecimalsAsync(quoteAsset.ContractAddress!))
                : FromSun(expectedExitRaw);
            decimal? roundTripLossPercent = buyAmount <= 0m ? null : ((buyAmount - expectedExit) / buyAmount) * 100m;
            var quoteSymbol = quoteAsset?.Symbol ?? NativeSymbol;

            return new DexSellabilityProbeResult(
                expectedExit > 0m,
                true,
                false,
                tokenAmountChecked,
                expectedExit,
                roundTripLossPercent,
                $"Round-trip SunSwap quote: {buyAmount:0.########} {quoteSymbol} -> {tokenAmountChecked:0.########} tokens -> {expectedExit:0.########} {quoteSymbol}.",
                quoteSymbol);
        }
        catch (Exception ex)
        {
            return new DexSellabilityProbeResult(
                false,
                true,
                false,
                0m,
                0m,
                null,
                $"Tron sellability probe failed: {ex.Message}",
                request.QuoteAssetSymbol);
        }
    }

    public async Task<TronExecutionDiagnostics> RunExecutionDiagnosticsAsync()
    {
        var steps = new List<TronDiagnosticStep>();

        steps.Add(new TronDiagnosticStep(
            "Wallet Address",
            TronAddressCodec.IsValidAddress(_walletAddress),
            TronAddressCodec.IsValidAddress(_walletAddress)
                ? $"Armed Tron address {_walletAddress} is valid."
                : "The derived Tron wallet address is invalid."));
        if (!steps[^1].Success)
        {
            return BuildDiagnosticsResult(steps);
        }

        try
        {
            var nativeBalance = await GetBalanceAsync(NativeSymbol);
            steps.Add(new TronDiagnosticStep(
                "RPC Balance",
                true,
                $"RPC responded with {nativeBalance:0.######} TRX."));
        }
        catch (Exception ex)
        {
            steps.Add(new TronDiagnosticStep(
                "RPC Balance",
                false,
                $"Failed to read native balance: {ex.Message}"));
            return BuildDiagnosticsResult(steps);
        }

        try
        {
            var usdtDecimals = await GetTokenDecimalsAsync(DefaultUsdtContractAddress);
            var usdtBalance = await GetTokenBalanceAsync(DefaultUsdtContractAddress);
            steps.Add(new TronDiagnosticStep(
                "TRC20 Access",
                usdtDecimals > 0,
                $"USDT decimals {usdtDecimals}, tracked balance {usdtBalance:0.######}."));
        }
        catch (Exception ex)
        {
            steps.Add(new TronDiagnosticStep(
                "TRC20 Access",
                false,
                $"Failed to read USDT metadata or balance: {ex.Message}"));
            return BuildDiagnosticsResult(steps);
        }

        try
        {
            var quote = await ProbeSellabilityAsync(new DexSellabilityProbeRequest(
                DefaultUsdtContractAddress,
                SlippagePercent: 5m,
                DexId: "sunswap",
                NativeAmountToProbe: 1m,
                QuoteAssetSymbol: "TRX"));
            steps.Add(new TronDiagnosticStep(
                "SunSwap Quote",
                quote.Passed,
                quote.Narrative));
            if (!quote.Passed)
            {
                return BuildDiagnosticsResult(steps);
            }
        }
        catch (Exception ex)
        {
            steps.Add(new TronDiagnosticStep(
                "SunSwap Quote",
                false,
                $"SunSwap quote failed: {ex.Message}"));
            return BuildDiagnosticsResult(steps);
        }

        try
        {
            var previewAmountSun = ToSun(0.1m);
            var path = BuildPath(null, DefaultUsdtContractAddress);
            var minOutRaw = ApplySlippage(await QueryAmountsOutRawAsync(previewAmountSun, path), 5m);
            var preview = await _walletClient.PreviewSmartContractAsync(
                _walletAddress,
                SunSwapRouterAddress,
                "swapExactETHForTokensSupportingFeeOnTransferTokens(uint256,address[],address,uint256)",
                TronWalletClient.EncodeSwapExactNativeForTokensParameters(minOutRaw, path, _walletAddress, BuildDeadlineUnix()),
                callValueSun: (long)previewAmountSun);

            steps.Add(new TronDiagnosticStep(
                "Unsigned Swap Preview",
                preview.Success,
                preview.Success
                    ? $"Unsigned SunSwap transaction builds successfully{(string.IsNullOrWhiteSpace(preview.TransactionId) ? string.Empty : $" (tx preview {preview.TransactionId[..Math.Min(10, preview.TransactionId.Length)]})")}."
                    : $"Unsigned SunSwap preview failed: {preview.Narrative}"));
        }
        catch (Exception ex)
        {
            steps.Add(new TronDiagnosticStep(
                "Unsigned Swap Preview",
                false,
                $"Unsigned SunSwap preview crashed: {ex.Message}"));
        }

        return BuildDiagnosticsResult(steps);
    }

    public async Task<TronTransferResult> SendTrc20Async(
        string contractAddress,
        string recipientAddress,
        decimal amount,
        decimal feeLimitTrx = 30m,
        CancellationToken cancellationToken = default) =>
        await _walletClient.SendTrc20Async(
            _privateKey,
            _walletAddress,
            contractAddress,
            recipientAddress,
            amount,
            feeLimitTrx,
            cancellationToken);

    private void ValidateDex(string? dexId)
    {
        if (!SupportsDex(dexId))
        {
            throw new NotSupportedException($"Tron DEX '{dexId}' is not wired in this build. Supported: {SupportedDexesLabel}.");
        }
    }

    private DexQuoteAssetOption? ResolveQuoteAsset(string? symbol, bool requireExactSymbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized is "TRX" or "NATIVE")
        {
            return SupportedQuoteAssets.First(static asset => asset.IsNative);
        }

        var quoteAsset = SupportedQuoteAssets.FirstOrDefault(asset => asset.Symbol.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (quoteAsset is null && requireExactSymbol)
        {
            throw new NotSupportedException($"Tron execution does not support quote asset '{symbol}'. Supported: {string.Join(", ", SupportedQuoteAssets.Select(static asset => asset.Symbol))}.");
        }

        return quoteAsset;
    }

    private string[] BuildPath(DexQuoteAssetOption? quoteAsset, string tokenAddress, bool reverse = false, DexQuoteAssetOption? quoteAssetOverride = null)
    {
        var effectiveQuote = quoteAssetOverride ?? quoteAsset;
        var fromAsset = effectiveQuote is null || effectiveQuote.IsNative
            ? WrappedTrxContractAddress
            : effectiveQuote.ContractAddress!;

        return reverse
            ? [tokenAddress, fromAsset]
            : [fromAsset, tokenAddress];
    }

    private async Task<BigInteger> QueryAmountsOutRawAsync(BigInteger amountIn, string[] path)
    {
        var results = await _walletClient.TriggerConstantContractArrayAsync(
            _walletAddress,
            SunSwapRouterAddress,
            "getAmountsOut(uint256,address[])",
            TronWalletClient.EncodeUint256AndAddressArrayParameters(amountIn, path));

        return results.Count > 0 ? results[^1] : BigInteger.Zero;
    }

    private async Task<TronApproveExecutionResult> EnsureAllowanceAsync(string tokenAddress, BigInteger amount)
    {
        var allowanceRaw = await _walletClient.TriggerConstantContractAsync(
            _walletAddress,
            tokenAddress,
            "allowance(address,address)",
            TronWalletClient.EncodeTwoAddressParameters(_walletAddress, SunSwapRouterAddress));

        if (allowanceRaw >= amount)
        {
            return TronApproveExecutionResult.NotRequired;
        }

        var approveInvocation = await _walletClient.ExecuteSmartContractAsync(
            _privateKey,
            _walletAddress,
            tokenAddress,
            "approve(address,uint256)",
            TronWalletClient.EncodeAddressAndUint256Parameters(SunSwapRouterAddress, amount));

        return new TronApproveExecutionResult(
            true,
            approveInvocation.TransactionId,
            approveInvocation.FeeSun / SunPerTrx);
    }

    private async Task<decimal> WaitForTokenBalanceChangeAsync(string tokenAddress, decimal balanceBefore)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var balanceAfter = await GetTokenBalanceAsync(tokenAddress);
            if (balanceAfter != balanceBefore)
            {
                return balanceAfter;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return await GetTokenBalanceAsync(tokenAddress);
    }

    private static BigInteger ApplySlippage(BigInteger expectedAmount, decimal slippagePercent)
    {
        var boundedSlippage = Math.Clamp(slippagePercent, 0m, 99m);
        var factor = (100m - boundedSlippage) / 100m;
        return (BigInteger)((decimal)expectedAmount * factor);
    }

    private static BigInteger BuildDeadlineUnix() =>
        new(DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds());

    private static TronExecutionDiagnostics BuildDiagnosticsResult(IReadOnlyList<TronDiagnosticStep> steps)
    {
        var success = steps.All(static step => step.Success);
        var summary = success
            ? "Tron self-test passed: wallet, RPC, TRC20 access, SunSwap quote, and unsigned swap preview are ready."
            : $"Tron self-test found {steps.Count(static step => !step.Success)} failing stage(s).";
        return new TronExecutionDiagnostics(success, summary, steps);
    }

    private static BigInteger ToSun(decimal trxAmount) =>
        ScaleUp(trxAmount, 6);

    private static decimal FromSun(BigInteger sunAmount) =>
        ScaleDown(sunAmount, 6);

    private static decimal ScaleDown(BigInteger value, int decimals)
    {
        if (decimals <= 0)
        {
            return (decimal)value;
        }

        var divisor = BigInteger.Pow(10, decimals);
        var integerPart = BigInteger.DivRem(value, divisor, out var remainder);
        var normalized = $"{integerPart.ToString(CultureInfo.InvariantCulture)}.{remainder.ToString().PadLeft(decimals, '0')}";
        return decimal.Parse(normalized, CultureInfo.InvariantCulture);
    }

    private static BigInteger ScaleUp(decimal value, int decimals)
    {
        var normalized = value.ToString(CultureInfo.InvariantCulture);
        if (!normalized.Contains('.'))
        {
            normalized += ".0";
        }

        var parts = normalized.Split('.', 2);
        var fractional = parts[1].PadRight(decimals, '0');
        if (fractional.Length > decimals)
        {
            fractional = fractional[..decimals];
        }

        var combined = parts[0] + fractional;
        return BigInteger.Parse(string.IsNullOrWhiteSpace(combined) ? "0" : combined, CultureInfo.InvariantCulture);
    }

    private sealed record TronApproveExecutionResult(
        bool WasRequired,
        string? TransactionHash,
        decimal FeeNative)
    {
        public static TronApproveExecutionResult NotRequired => new(false, null, 0m);
    }
}
