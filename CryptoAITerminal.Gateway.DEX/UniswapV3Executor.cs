using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace CryptoAITerminal.Gateway.DEX;

// ── Uniswap V3 ABI Function Messages ─────────────────────────────────────────

[Function("quoteExactInputSingle", "uint256")]
public class V3QuoteExactInputSingleFunction : FunctionMessage
{
    [Parameter("address", "tokenIn",  1)] public string TokenIn  { get; set; } = string.Empty;
    [Parameter("address", "tokenOut", 2)] public string TokenOut { get; set; } = string.Empty;
    [Parameter("uint24",  "fee",      3)] public uint   Fee      { get; set; }
    [Parameter("uint256", "amountIn", 4)] public BigInteger AmountIn { get; set; }
    [Parameter("uint160", "sqrtPriceLimitX96", 5)] public BigInteger SqrtPriceLimitX96 { get; set; }
}

// QuoterV2 quoteExactInputSingle returns (amountOut, sqrtPriceX96After, initializedTicksCrossed, gasEstimate)
[Function("quoteExactInputSingle")]
public class V3QuoterV2ExactInputSingleFunction : FunctionMessage
{
    [Parameter("tuple", "params", 1)]
    public V3QuoteParams Params { get; set; } = new();
}

public class V3QuoteParams
{
    [Parameter("address", "tokenIn",  1)] public string TokenIn  { get; set; } = string.Empty;
    [Parameter("address", "tokenOut", 2)] public string TokenOut { get; set; } = string.Empty;
    [Parameter("uint256", "amountIn", 3)] public BigInteger AmountIn { get; set; }
    [Parameter("uint24",  "fee",      4)] public uint Fee { get; set; }
    [Parameter("uint160", "sqrtPriceLimitX96", 5)] public BigInteger SqrtPriceLimitX96 { get; set; }
}

[Function("exactInputSingle", "uint256")]
public class V3ExactInputSingleFunction : FunctionMessage
{
    [Parameter("tuple", "params", 1)]
    public V3ExactInputSingleParams Params { get; set; } = new();
}

public class V3ExactInputSingleParams
{
    [Parameter("address", "tokenIn",           1)] public string    TokenIn          { get; set; } = string.Empty;
    [Parameter("address", "tokenOut",          2)] public string    TokenOut         { get; set; } = string.Empty;
    [Parameter("uint24",  "fee",               3)] public uint      Fee              { get; set; }
    [Parameter("address", "recipient",         4)] public string    Recipient        { get; set; } = string.Empty;
    [Parameter("uint256", "deadline",          5)] public BigInteger Deadline        { get; set; }
    [Parameter("uint256", "amountIn",          6)] public BigInteger AmountIn        { get; set; }
    [Parameter("uint256", "amountOutMinimum",  7)] public BigInteger AmountOutMinimum { get; set; }
    [Parameter("uint160", "sqrtPriceLimitX96", 8)] public BigInteger SqrtPriceLimitX96 { get; set; }
}

[Function("exactOutputSingle", "uint256")]
public class V3ExactOutputSingleFunction : FunctionMessage
{
    [Parameter("tuple", "params", 1)]
    public V3ExactOutputSingleParams Params { get; set; } = new();
}

public class V3ExactOutputSingleParams
{
    [Parameter("address", "tokenIn",          1)] public string    TokenIn           { get; set; } = string.Empty;
    [Parameter("address", "tokenOut",         2)] public string    TokenOut          { get; set; } = string.Empty;
    [Parameter("uint24",  "fee",              3)] public uint      Fee               { get; set; }
    [Parameter("address", "recipient",        4)] public string    Recipient         { get; set; } = string.Empty;
    [Parameter("uint256", "deadline",         5)] public BigInteger Deadline         { get; set; }
    [Parameter("uint256", "amountOut",        6)] public BigInteger AmountOut        { get; set; }
    [Parameter("uint256", "amountInMaximum",  7)] public BigInteger AmountInMaximum  { get; set; }
    [Parameter("uint160", "sqrtPriceLimitX96",8)] public BigInteger SqrtPriceLimitX96 { get; set; }
}

// ── Known fee tiers ────────────────────────────────────────────────────────────
public static class UniswapV3FeeTier
{
    public const uint Lowest  = 100;   // 0.01% — stablecoins
    public const uint Low     = 500;   // 0.05% — pegged pairs
    public const uint Medium  = 3000;  // 0.30% — most pairs
    public const uint High    = 10000; // 1.00% — exotic/new tokens
}

// ── Network definitions ────────────────────────────────────────────────────────

public sealed record UniswapV3NetworkConfig(
    string NetworkName,
    string SwapRouter02Address,
    string QuoterV2Address,
    string WrappedNativeAddress,
    uint[] PreferredFeeTiers);

public static class UniswapV3Networks
{
    public static readonly IReadOnlyDictionary<string, UniswapV3NetworkConfig> Configs =
        new Dictionary<string, UniswapV3NetworkConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ethereum"] = new(
                "Ethereum",
                "0x68b3465833fb72A70ecDF485E0e4C7bD8665Fc45", // SwapRouter02
                "0x61fFE014bA17989E743c5F6cB21bF9697530B21e", // QuoterV2
                "0xC02aaA39b223FE8D0A0E5C4F27eAD9083C756Cc2", // WETH
                [UniswapV3FeeTier.Medium, UniswapV3FeeTier.High, UniswapV3FeeTier.Low]),

            ["Base"] = new(
                "Base",
                "0x2626664c2603336E57B271c5C0b26F421741e481",
                "0x3d4e44Eb1374240CE5F1B871ab261CD16335B76a",
                "0x4200000000000000000000000000000000000006",
                [UniswapV3FeeTier.Medium, UniswapV3FeeTier.High, UniswapV3FeeTier.Low]),

            ["Polygon"] = new(
                "Polygon",
                "0x68b3465833fb72A70ecDF485E0e4C7bD8665Fc45",
                "0x61fFE014bA17989E743c5F6cB21bF9697530B21e",
                "0x0d500B1d8E8eF31E21C99d1Db9A6444d3ADf1270",
                [UniswapV3FeeTier.Medium, UniswapV3FeeTier.High]),

            ["Arbitrum"] = new(
                "Arbitrum",
                "0x68b3465833fb72A70ecDF485E0e4C7bD8665Fc45",
                "0x61fFE014bA17989E743c5F6cB21bF9697530B21e",
                "0x82aF49447D8a07e3bd95BD0d56f35241523fBab1",
                [UniswapV3FeeTier.Low, UniswapV3FeeTier.Medium, UniswapV3FeeTier.High]),
        };

    public static bool IsSupported(string networkName) => Configs.ContainsKey(networkName);
}

// ── Executor ──────────────────────────────────────────────────────────────────

/// <summary>
/// Executes swaps on Uniswap V3 / forks using SwapRouter02 (exactInputSingle).
/// Tries each fee tier from the config in order, picking the best quote.
/// Use this for ETH, Base, Polygon, Arbitrum where V3 has deeper liquidity
/// than V2 for most established tokens.
/// </summary>
public sealed class UniswapV3Executor
{
    private readonly Web3 _web3;
    private readonly Account _account;
    private readonly UniswapV3NetworkConfig _config;

    public string NetworkName => _config.NetworkName;

    public static bool SupportsNetwork(string networkName) =>
        UniswapV3Networks.IsSupported(networkName);

    public static UniswapV3Executor Create(string networkName, string privateKey, string rpcUrl)
    {
        if (!UniswapV3Networks.Configs.TryGetValue(networkName, out var cfg))
            throw new NotSupportedException($"Uniswap V3 not configured for network '{networkName}'.");

        return new UniswapV3Executor(privateKey, rpcUrl, cfg);
    }

    private UniswapV3Executor(string privateKey, string rpcUrl, UniswapV3NetworkConfig config)
    {
        _account = new Account(privateKey);
        _web3 = new Web3(_account, rpcUrl);
        _config = config;
    }

    /// <summary>
    /// Returns best quote (amountOut in token units) for buying tokenOut with nativeAmountEth.
    /// Tries all configured fee tiers and returns the best.
    /// </summary>
    public async Task<V3QuoteResult> GetBestBuyQuoteAsync(
        string tokenOutAddress,
        decimal nativeAmountEth,
        CancellationToken ct = default)
    {
        var amountInWei = Web3.Convert.ToWei(nativeAmountEth);
        var best = V3QuoteResult.NotFound;

        foreach (var fee in _config.PreferredFeeTiers)
        {
            try
            {
                var quoted = await QuoteExactInputAsync(
                    _config.WrappedNativeAddress, tokenOutAddress, fee, amountInWei, ct);

                if (quoted > best.AmountOut)
                {
                    best = new V3QuoteResult(quoted, fee, true);
                }
            }
            catch
            {
                // Pool may not exist for this fee tier — try next
            }
        }

        return best;
    }

    /// <summary>
    /// Buys tokenOut with all native ETH/MATIC/etc in the tx value.
    /// Returns the transaction hash.
    /// </summary>
    public async Task<V3SwapReceipt> BuyWithNativeAsync(
        string tokenOutAddress,
        decimal nativeAmountEth,
        decimal slippagePercent = 3m,
        CancellationToken ct = default)
    {
        var amountInWei = Web3.Convert.ToWei(nativeAmountEth);

        var quote = await GetBestBuyQuoteAsync(tokenOutAddress, nativeAmountEth, ct);
        if (!quote.Found)
            throw new InvalidOperationException($"No V3 pool found for token {tokenOutAddress} on {_config.NetworkName}");

        var slippageFactor = 1m - slippagePercent / 100m;
        var amountOutMin = new BigInteger((double)((decimal)quote.AmountOut * slippageFactor));

        var deadline = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;

        var swapFunction = new V3ExactInputSingleFunction
        {
            Params = new V3ExactInputSingleParams
            {
                TokenIn           = _config.WrappedNativeAddress,
                TokenOut          = tokenOutAddress,
                Fee               = quote.FeeTier,
                Recipient         = _account.Address,
                Deadline          = new BigInteger(deadline),
                AmountIn          = amountInWei,
                AmountOutMinimum  = amountOutMin,
                SqrtPriceLimitX96 = BigInteger.Zero
            },
            AmountToSend = new HexBigInteger(amountInWei)
        };

        var handler = _web3.Eth.GetContractTransactionHandler<V3ExactInputSingleFunction>();
        var receipt = await handler.SendRequestAndWaitForReceiptAsync(
            _config.SwapRouter02Address, swapFunction, ct);

        return new V3SwapReceipt(
            receipt.TransactionHash,
            receipt.Status?.Value == 1,
            nativeAmountEth,
            Web3.Convert.FromWei(amountOutMin), // minimum, actual decoded from logs separately
            quote.FeeTier,
            (long)(receipt.GasUsed?.Value ?? 0));
    }

    /// <summary>
    /// Sells tokenIn for native ETH/MATIC.
    /// Assumes approval has been granted by the caller.
    /// </summary>
    public async Task<V3SwapReceipt> SellForNativeAsync(
        string tokenInAddress,
        decimal tokenAmount,
        int tokenDecimals,
        decimal slippagePercent = 3m,
        CancellationToken ct = default)
    {
        var amountInWei = new BigInteger(tokenAmount * (decimal)Math.Pow(10, tokenDecimals));
        var fee = await FindBestFeeTierForSellAsync(tokenInAddress, amountInWei, ct);

        // Get a rough quote to compute amountOutMin
        BigInteger amountOutMin = BigInteger.Zero;
        try
        {
            var quoted = await QuoteExactInputAsync(tokenInAddress, _config.WrappedNativeAddress, fee, amountInWei, ct);
            var slippageFactor = 1m - slippagePercent / 100m;
            amountOutMin = new BigInteger((double)((decimal)quoted * slippageFactor));
        }
        catch { /* proceed with 0 min — risky but allows execution */ }

        var deadline = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;

        var swapFunction = new V3ExactInputSingleFunction
        {
            Params = new V3ExactInputSingleParams
            {
                TokenIn           = tokenInAddress,
                TokenOut          = _config.WrappedNativeAddress,
                Fee               = fee,
                Recipient         = _account.Address,
                Deadline          = new BigInteger(deadline),
                AmountIn          = amountInWei,
                AmountOutMinimum  = amountOutMin,
                SqrtPriceLimitX96 = BigInteger.Zero
            }
        };

        var handler = _web3.Eth.GetContractTransactionHandler<V3ExactInputSingleFunction>();
        var receipt = await handler.SendRequestAndWaitForReceiptAsync(
            _config.SwapRouter02Address, swapFunction, ct);

        return new V3SwapReceipt(
            receipt.TransactionHash,
            receipt.Status?.Value == 1,
            tokenAmount,
            Web3.Convert.FromWei(amountOutMin),
            fee,
            (long)(receipt.GasUsed?.Value ?? 0));
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task<BigInteger> QuoteExactInputAsync(
        string tokenIn, string tokenOut, uint fee, BigInteger amountIn, CancellationToken ct)
    {
        // QuoterV2 is a view function — call without account
        var quoterWeb3 = new Web3(_web3.Client);
        var quoteHandler = quoterWeb3.Eth.GetContractQueryHandler<V3QuoteExactInputSingleFunction>();
        var quoteFunction = new V3QuoteExactInputSingleFunction
        {
            TokenIn  = tokenIn,
            TokenOut = tokenOut,
            Fee      = fee,
            AmountIn = amountIn,
            SqrtPriceLimitX96 = BigInteger.Zero
        };
        return await quoteHandler.QueryAsync<BigInteger>(_config.QuoterV2Address, quoteFunction);
    }

    private async Task<uint> FindBestFeeTierForSellAsync(
        string tokenIn, BigInteger amountIn, CancellationToken ct)
    {
        BigInteger bestOut = BigInteger.Zero;
        var bestFee = _config.PreferredFeeTiers[0];

        foreach (var fee in _config.PreferredFeeTiers)
        {
            try
            {
                var quoted = await QuoteExactInputAsync(tokenIn, _config.WrappedNativeAddress, fee, amountIn, ct);
                if (quoted > bestOut)
                {
                    bestOut = quoted;
                    bestFee = fee;
                }
            }
            catch { }
        }

        return bestFee;
    }
}

public sealed record V3QuoteResult(BigInteger AmountOut, uint FeeTier, bool Found)
{
    public static readonly V3QuoteResult NotFound = new(BigInteger.Zero, 0, false);
}

public sealed record V3SwapReceipt(
    string TxHash,
    bool Success,
    decimal AmountIn,
    decimal AmountOutMinimum,
    uint FeeTierUsed,
    long GasUsed);
