using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>Parsed, ready-to-sign transaction from an aggregator swap route.</summary>
public sealed record AggregatorSwapTx(
    string To,
    string Data,
    BigInteger Value,
    BigInteger GasPrice,
    BigInteger EstimatedGas,
    decimal MinOutTokens,
    string OutSymbol,
    int OutDecimals);

/// <summary>Outcome of a real aggregator swap (build + sign + send).</summary>
public sealed record AggregatorSwapResult(
    string TxHash,
    bool Success,
    decimal MinOutTokens,
    string OutSymbol,
    bool ApproveWasRequired,
    string? ApproveTxHash,
    string Route);

/// <summary>
/// Executes a real EVM swap along the aggregator's best cross-DEX route (OpenOcean).
/// Unlike <c>DexAggregatorService</c> (preview only), this builds the swap transaction,
/// grants ERC-20 allowance to the aggregator router when the spend asset is a token,
/// signs with the caller's account-bound <see cref="Web3"/>, and broadcasts it.
///
/// Non-custodial: the account/private key live inside the caller's Web3 — this class
/// never sees or stores a key. URL building and response parsing are pure and unit-tested;
/// only the send path touches the chain.
/// </summary>
public sealed class AggregatorSwapExecutor
{
    public const string NativePlaceholder = "0xEeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeE";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>OpenOcean chain slug for a network name (null = aggregator not wired here).</summary>
    public static string? NetworkSlug(string networkName) => networkName?.Trim().ToLowerInvariant() switch
    {
        "ethereum" or "eth" => "eth",
        "bsc" => "bsc",
        "base" => "base",
        "arbitrum" => "arbitrum",
        "polygon" => "polygon",
        "avalanche" or "avax" => "avax",
        "optimism" => "optimism",
        _ => null,
    };

    /// <summary>Pure builder for the OpenOcean swap_quote endpoint (returns a signable tx).</summary>
    public static string BuildSwapUrl(
        string chainSlug, string inToken, string outToken,
        decimal amount, decimal slippagePercent, string account, decimal gasPriceGwei)
    {
        var inv = CultureInfo.InvariantCulture;
        return $"https://open-api.openocean.finance/v3/{chainSlug}/swap_quote" +
               $"?inTokenAddress={inToken}&outTokenAddress={outToken}" +
               $"&amount={amount.ToString("0.########", inv)}" +
               $"&slippage={slippagePercent.ToString("0.##", inv)}" +
               $"&gasPrice={gasPriceGwei.ToString("0.##", inv)}" +
               $"&account={account}";
    }

    /// <summary>Pure parser for an OpenOcean swap_quote response. Returns null when no route.</summary>
    public static AggregatorSwapTx? ParseSwapTx(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var to = Str(data, "to");
            var callData = Str(data, "data");
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(callData))
            {
                return null;
            }

            var value = BigIntProp(data, "value");
            var gasPrice = BigIntProp(data, "gasPrice");
            var estGas = BigIntProp(data, "estimatedGas");
            if (estGas <= 0) estGas = BigIntProp(data, "gas");

            var outDec = 18;
            var outSymbol = "";
            if (data.TryGetProperty("outToken", out var ot) && ot.ValueKind == JsonValueKind.Object)
            {
                if (ot.TryGetProperty("decimals", out var od) && od.ValueKind == JsonValueKind.Number) outDec = od.GetInt32();
                if (ot.TryGetProperty("symbol", out var os) && os.ValueKind == JsonValueKind.String) outSymbol = os.GetString() ?? "";
            }

            var minRaw = BigIntProp(data, "minOutAmount");
            if (minRaw <= 0) minRaw = BigIntProp(data, "outAmount");
            var factor = Pow10(outDec);
            var minOutTokens = factor > 0m ? (decimal)minRaw / factor : (decimal)minRaw;

            return new AggregatorSwapTx(to!, callData!, value, gasPrice, estGas, minOutTokens, outSymbol, outDec);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches the best-route swap tx, ensures allowance for token spends, signs and broadcasts.
    /// <paramref name="web3"/> must be account-bound (constructed with the trading account).
    /// </summary>
    public async Task<AggregatorSwapResult> ExecuteAsync(
        Web3 web3, string owner, string chainSlug,
        string inToken, int inDecimals, decimal amount,
        string outToken, decimal slippagePercent, decimal gasPriceGwei,
        CancellationToken ct = default)
    {
        var url = BuildSwapUrl(chainSlug, inToken, outToken, amount, slippagePercent, owner, gasPriceGwei);
        var json = await Http.GetStringAsync(url, ct);
        var tx = ParseSwapTx(json)
                 ?? throw new InvalidOperationException("Aggregator returned no executable swap route for this pair/amount.");

        var isNativeIn = inToken.Equals(NativePlaceholder, StringComparison.OrdinalIgnoreCase);
        var approveRequired = false;
        string? approveTxHash = null;

        if (!isNativeIn)
        {
            var amountInRaw = ToRaw(amount, inDecimals);
            var allowance = await GetAllowanceAsync(web3, inToken, owner, tx.To);
            if (allowance < amountInRaw)
            {
                approveRequired = true;
                approveTxHash = await ApproveAsync(web3, inToken, tx.To, amountInRaw, ct);
            }
        }

        var gasLimit = tx.EstimatedGas > 0 ? tx.EstimatedGas * 12 / 10 : new BigInteger(800_000);
        var txInput = new TransactionInput
        {
            From = owner,
            To = tx.To,
            Data = tx.Data,
            Value = new HexBigInteger(tx.Value),
            Gas = new HexBigInteger(gasLimit),
        };
        if (tx.GasPrice > 0)
        {
            txInput.GasPrice = new HexBigInteger(tx.GasPrice);
        }

        var hash = await web3.Eth.TransactionManager.SendTransactionAsync(txInput);
        var receipt = await WaitForReceiptAsync(web3, hash, ct);
        var success = receipt?.Status?.Value == 1;

        return new AggregatorSwapResult(
            hash, success, tx.MinOutTokens, tx.OutSymbol, approveRequired, approveTxHash, "OpenOcean best-route");
    }

    private static async Task<BigInteger> GetAllowanceAsync(Web3 web3, string token, string owner, string spender)
    {
        var handler = web3.Eth.GetContractQueryHandler<AllowanceFunction>();
        return await handler.QueryAsync<BigInteger>(token, new AllowanceFunction { Owner = owner, Spender = spender });
    }

    private static async Task<string> ApproveAsync(Web3 web3, string token, string spender, BigInteger amount, CancellationToken ct)
    {
        var handler = web3.Eth.GetContractTransactionHandler<ApproveFunction>();
        var receipt = await handler.SendRequestAndWaitForReceiptAsync(
            token, new ApproveFunction { Spender = spender, Amount = amount }, ct);
        return receipt.TransactionHash;
    }

    private static async Task<TransactionReceipt?> WaitForReceiptAsync(Web3 web3, string hash, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(hash);
            if (receipt is not null)
            {
                return receipt;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        }

        return null;
    }

    private static BigInteger ToRaw(decimal amount, int decimals)
    {
        var factor = Pow10(decimals);
        return new BigInteger(amount * factor);
    }

    private static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n && i < 28; i++) r *= 10m;
        return r;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static BigInteger BigIntProp(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return BigInteger.Zero;
        }

        var raw = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return BigInteger.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : BigInteger.Zero;
    }
}

/// <summary>Optional capability: a gateway that can execute swaps along an aggregator best-route.</summary>
public interface ISupportsAggregatorSwap
{
    bool SupportsAggregatorSwap { get; }

    Task<AggregatorSwapResult> ExecuteAggregatorBuyAsync(
        string tokenAddress, decimal spendAmount, decimal slippagePercent,
        string? spendAssetSymbol, decimal gasPriceGwei, CancellationToken ct = default);
}
