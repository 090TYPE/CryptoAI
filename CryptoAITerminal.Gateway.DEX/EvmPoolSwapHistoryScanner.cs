using System.Numerics;
using System.Text.Json.Nodes;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Reconstructs full price history for a Uniswap V2-compatible pool directly from
/// on-chain Swap events via eth_getLogs. Works for any pool age — new tokens included.
///
/// Swap(address indexed sender,
///      uint256 amount0In, uint256 amount1In,
///      uint256 amount0Out, uint256 amount1Out,
///      address indexed to)
/// keccak256 = 0xd78ad95fa46c994b6551d0da85fc275fe613ce37657fb8d5e3d130840159d822
/// </summary>
public sealed class EvmPoolSwapHistoryScanner
{
    private const string SwapEventTopic = "0xd78ad95fa46c994b6551d0da85fc275fe613ce37657fb8d5e3d130840159d822";
    private const int BlockChunkSize = 2000;
    private const int MaxChunks = 50; // scan at most 100 000 blocks back

    private readonly HttpClient _http;
    private readonly string _rpcUrl;

    public EvmPoolSwapHistoryScanner(string rpcUrl)
    {
        _rpcUrl = rpcUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    /// <summary>
    /// Scans the pool from (currentBlock - maxBlockLookback) to currentBlock,
    /// buckets swaps into OHLCV candles of the given size, and returns ordered list.
    /// token0Decimals / token1Decimals needed to express price in USD-like units.
    /// isToken0Base = true means we report price of token0 denominated in token1.
    /// </summary>
    public async Task<IReadOnlyList<DexOhlcvPoint>> ScanAsync(
        string pairAddress,
        bool isToken0Base,
        int token0Decimals,
        int token1Decimals,
        decimal token1PriceUsd,
        TimeSpan bucketSize,
        int maxBlockLookback = 50_000,
        CancellationToken ct = default)
    {
        var latestBlock = await GetLatestBlockAsync(ct);
        var fromBlock = BigInteger.Max(BigInteger.Zero, latestBlock - maxBlockLookback);

        // Fetch block timestamps for the range endpoints (to map block→time)
        var fromTimestamp = await GetBlockTimestampAsync(fromBlock, ct);

        var rawSwaps = await FetchSwapEventsAsync(pairAddress, fromBlock, latestBlock, ct);
        if (rawSwaps.Count == 0) return Array.Empty<DexOhlcvPoint>();

        // Enrich with approximate timestamps (linear interpolation between fromBlock and now)
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var totalBlocks = (double)(latestBlock - fromBlock);

        var priced = new List<(DateTime time, decimal price, decimal volumeToken1)>(rawSwaps.Count);
        foreach (var swap in rawSwaps)
        {
            if (swap.BlockNumber < fromBlock) continue;

            var blockFraction = totalBlocks > 0
                ? (double)(swap.BlockNumber - fromBlock) / totalBlocks
                : 1.0;
            var approxTs = fromTimestamp + (long)((nowTs - fromTimestamp) * blockFraction);
            var time = DateTimeOffset.FromUnixTimeSeconds(approxTs).UtcDateTime;

            var price = CalculatePriceUsd(swap, isToken0Base, token0Decimals, token1Decimals, token1PriceUsd);
            var vol = CalculateVolumeToken1(swap, isToken0Base, token1Decimals);
            if (price > 0)
            {
                priced.Add((time, price, vol));
            }
        }

        if (priced.Count == 0) return Array.Empty<DexOhlcvPoint>();

        return BucketizeToCandles(priced, bucketSize);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<IReadOnlyList<RawSwap>> FetchSwapEventsAsync(
        string pairAddress, BigInteger fromBlock, BigInteger toBlock, CancellationToken ct)
    {
        var result = new List<RawSwap>();
        var cursor = fromBlock;
        var chunks = 0;

        while (cursor <= toBlock && chunks < MaxChunks)
        {
            var chunkTo = BigInteger.Min(cursor + BlockChunkSize - 1, toBlock);
            var fromHex = "0x" + cursor.ToString("X");
            var toHex   = "0x" + chunkTo.ToString("X");

            try
            {
                var logs = await EthGetLogsAsync(pairAddress, SwapEventTopic, fromHex, toHex, ct);
                foreach (var log in logs)
                {
                    if (log is null) continue;
                    var blockHex = log["blockNumber"]?.GetValue<string>() ?? "0x0";
                    var data     = log["data"]?.GetValue<string>() ?? string.Empty;

                    if (data.Length < 2 + 4 * 64) continue;
                    var d = data[2..];

                    var swap = new RawSwap(
                        HexToBigInt(blockHex),
                        HexToBigInt(d[..64]),       // amount0In
                        HexToBigInt(d[64..128]),     // amount1In
                        HexToBigInt(d[128..192]),    // amount0Out
                        HexToBigInt(d[192..256]));   // amount1Out

                    result.Add(swap);
                }
            }
            catch
            {
                // RPC may rate-limit; skip chunk and continue
            }

            cursor = chunkTo + 1;
            chunks++;
        }

        return result;
    }

    private async Task<JsonArray> EthGetLogsAsync(
        string address, string topic0, string fromBlock, string toBlock, CancellationToken ct)
    {
        var payload = $$"""
            {"jsonrpc":"2.0","method":"eth_getLogs","params":[{
              "address":"{{address}}",
              "fromBlock":"{{fromBlock}}",
              "toBlock":"{{toBlock}}",
              "topics":["{{topic0}}"]
            }],"id":1}
            """;
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_rpcUrl, content, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonNode.Parse(body);
        return doc?["result"]?.AsArray() ?? new JsonArray();
    }

    private async Task<BigInteger> GetLatestBlockAsync(CancellationToken ct)
    {
        var payload = """{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}""";
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_rpcUrl, content, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var hex = JsonNode.Parse(body)?["result"]?.GetValue<string>() ?? "0x0";
        return HexToBigInt(hex);
    }

    private async Task<long> GetBlockTimestampAsync(BigInteger blockNumber, CancellationToken ct)
    {
        var hexBlock = "0x" + blockNumber.ToString("X");
        var payload  = $$"""{"jsonrpc":"2.0","method":"eth_getBlockByNumber","params":["{{hexBlock}}",false],"id":1}""";
        try
        {
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_rpcUrl, content, ct);
            response.EnsureSuccessStatusCode();
            var body      = await response.Content.ReadAsStringAsync(ct);
            var tsHex     = JsonNode.Parse(body)?["result"]?["timestamp"]?.GetValue<string>() ?? "0x0";
            return (long)HexToBigInt(tsHex);
        }
        catch
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
        }
    }

    private static decimal CalculatePriceUsd(
        RawSwap swap, bool isToken0Base,
        int dec0, int dec1, decimal token1Usd)
    {
        // token0 priced in token1 terms, then converted to USD
        decimal raw;
        if (isToken0Base)
        {
            // Buying token0: spent token1 (amount1In), received token0 (amount0Out)
            if (swap.Amount1In > 0 && swap.Amount0Out > 0)
            {
                var t1 = (decimal)swap.Amount1In  / (decimal)Math.Pow(10, dec1);
                var t0 = (decimal)swap.Amount0Out / (decimal)Math.Pow(10, dec0);
                raw = t0 > 0 ? t1 / t0 : 0;
            }
            // Selling token0: spent token0 (amount0In), received token1 (amount1Out)
            else if (swap.Amount0In > 0 && swap.Amount1Out > 0)
            {
                var t0 = (decimal)swap.Amount0In  / (decimal)Math.Pow(10, dec0);
                var t1 = (decimal)swap.Amount1Out / (decimal)Math.Pow(10, dec1);
                raw = t0 > 0 ? t1 / t0 : 0;
            }
            else return 0;
        }
        else
        {
            // token1 is base
            if (swap.Amount0In > 0 && swap.Amount1Out > 0)
            {
                var t0 = (decimal)swap.Amount0In  / (decimal)Math.Pow(10, dec0);
                var t1 = (decimal)swap.Amount1Out / (decimal)Math.Pow(10, dec1);
                raw = t1 > 0 ? t0 / t1 : 0;
            }
            else if (swap.Amount1In > 0 && swap.Amount0Out > 0)
            {
                var t1 = (decimal)swap.Amount1In  / (decimal)Math.Pow(10, dec1);
                var t0 = (decimal)swap.Amount0Out / (decimal)Math.Pow(10, dec0);
                raw = t1 > 0 ? t0 / t1 : 0;
            }
            else return 0;
        }

        return raw * token1Usd;
    }

    private static decimal CalculateVolumeToken1(RawSwap swap, bool isToken0Base, int dec1)
    {
        if (isToken0Base)
        {
            var volRaw = BigInteger.Max(swap.Amount1In, swap.Amount1Out);
            return (decimal)volRaw / (decimal)Math.Pow(10, dec1);
        }
        else
        {
            var volRaw = BigInteger.Max(swap.Amount0In, swap.Amount0Out);
            return (decimal)volRaw / (decimal)Math.Pow(10, dec1);
        }
    }

    private static IReadOnlyList<DexOhlcvPoint> BucketizeToCandles(
        IReadOnlyList<(DateTime time, decimal price, decimal vol)> trades,
        TimeSpan bucketSize)
    {
        if (trades.Count == 0) return Array.Empty<DexOhlcvPoint>();

        var ordered = trades.OrderBy(t => t.time).ToList();
        var epoch   = ordered[0].time;
        var candles = new List<DexOhlcvPoint>();

        decimal open = 0, high = 0, low = decimal.MaxValue, close = 0, vol = 0;
        var bucketStart = FloorToBucket(epoch, bucketSize);

        foreach (var (time, price, v) in ordered)
        {
            var bucket = FloorToBucket(time, bucketSize);
            if (bucket > bucketStart && open > 0)
            {
                candles.Add(new DexOhlcvPoint
                {
                    Timestamp = bucketStart,
                    Open  = open,
                    High  = high,
                    Low   = low,
                    Close = close,
                    Volume = vol
                });
                bucketStart = bucket;
                open = high = price;
                low  = price;
                vol  = v;
            }
            else
            {
                if (open == 0) { open = price; high = price; low = price; }
                high = Math.Max(high, price);
                low  = Math.Min(low, price);
                vol += v;
            }
            close = price;
        }

        if (open > 0)
        {
            candles.Add(new DexOhlcvPoint
            {
                Timestamp = bucketStart,
                Open  = open,
                High  = high,
                Low   = low,
                Close = close,
                Volume = vol
            });
        }

        return candles;
    }

    private static DateTime FloorToBucket(DateTime dt, TimeSpan bucket)
    {
        var ticks = dt.Ticks / bucket.Ticks * bucket.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static BigInteger HexToBigInt(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex is "0x0" or "0x") return BigInteger.Zero;
        var clean = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return string.IsNullOrEmpty(clean)
            ? BigInteger.Zero
            : BigInteger.Parse("0" + clean, System.Globalization.NumberStyles.HexNumber);
    }

    private sealed record RawSwap(
        BigInteger BlockNumber,
        BigInteger Amount0In,
        BigInteger Amount1In,
        BigInteger Amount0Out,
        BigInteger Amount1Out);
}

/// <summary>
/// Per-network RPC endpoints for on-chain history scanning.
/// </summary>
public static class EvmHistoryScannerNetworks
{
    public static readonly IReadOnlyDictionary<string, EvmScannerNetworkConfig> Configs =
        new Dictionary<string, EvmScannerNetworkConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"] = new("https://ethereum-rpc.publicnode.com",    12,  50_000),
            ["eth"]      = new("https://ethereum-rpc.publicnode.com",    12,  50_000),
            ["bsc"]      = new("https://bsc-dataseed.binance.org/",       3, 100_000),
            ["bnb"]      = new("https://bsc-dataseed.binance.org/",       3, 100_000),
            ["base"]     = new("https://mainnet.base.org",                2, 100_000),
            ["polygon"]  = new("https://polygon-rpc.com",                 2, 100_000),
            ["arbitrum"] = new("https://arb1.arbitrum.io/rpc",            1, 200_000),
        };

    public static bool TryGet(string chainId, out EvmScannerNetworkConfig config) =>
        Configs.TryGetValue(chainId.Trim().ToLowerInvariant(), out config!);
}

public sealed record EvmScannerNetworkConfig(
    string RpcUrl,
    int AvgBlockTimeSeconds,
    int DefaultMaxBlockLookback);
