using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed record DexTrendingToken(
    string  TokenAddress,
    string  Symbol,
    string  Name,
    string  Chain,
    string  DexId,
    string  PairAddress,
    decimal PriceUsd,
    double  PriceChangeM5,
    double  PriceChangeM15,   // approximated: (M5 + H1) / 2
    double  PriceChangeH1,
    double  PriceChangeH6,
    double  PriceChangeH24,
    decimal VolumeM5,
    decimal VolumeM15,        // approximated: H1 / 4
    decimal VolumeH1,
    decimal VolumeH24,
    decimal LiquidityUsd,
    decimal MarketCap,
    long    PairCreatedAtMs,
    string  DexScreenerUrl
);

public sealed class DexTrendingService : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DexTrendingService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        _http.BaseAddress = new Uri("https://api.dexscreener.com/");
    }

    public async Task<IReadOnlyList<DexTrendingToken>> FetchTrendingAsync(CancellationToken ct = default)
    {
        // Step 1: get boosted/trending token addresses
        var boostAddresses = await FetchBoostAddressesAsync(ct).ConfigureAwait(false);

        List<string> addresses;
        bool usedFallback = false;

        if (boostAddresses.Count >= 5)
        {
            addresses = boostAddresses.Take(20).ToList();
        }
        else
        {
            // fallback: search trending
            usedFallback = true;
            addresses = [];
        }

        List<DexTrendingToken> tokens;

        if (!usedFallback && addresses.Count > 0)
        {
            // Fetch pair details for each address in parallel (batches of 5 to avoid rate limit)
            tokens = await FetchPairsForAddressesAsync(addresses, ct).ConfigureAwait(false);
        }
        else
        {
            tokens = [];
        }

        // If we have fewer than 5 results, use the search fallback
        if (tokens.Count < 5)
        {
            var fallback = await FetchSearchFallbackAsync(ct).ConfigureAwait(false);
            // Merge: add fallback tokens not already present
            var seen = tokens.Select(t => t.TokenAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var ft in fallback)
            {
                if (seen.Add(ft.TokenAddress))
                    tokens.Add(ft);
            }
        }

        // Deduplicate by address, keep highest liquidity
        var result = tokens
            .GroupBy(t => t.TokenAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.LiquidityUsd).First())
            .OrderByDescending(t => t.VolumeH1)
            .ToList();

        return result;
    }

    private async Task<List<string>> FetchBoostAddressesAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync("token-boosts/top/v1", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var addresses = new List<string>();
            var arr = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : default(JsonElement.ArrayEnumerator);

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.TryGetProperty("tokenAddress", out var addrProp))
                    {
                        var addr = addrProp.GetString();
                        if (!string.IsNullOrWhiteSpace(addr))
                            addresses.Add(addr);
                    }
                }
            }
            return addresses;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<DexTrendingToken>> FetchPairsForAddressesAsync(
        List<string> addresses, CancellationToken ct)
    {
        // Fetch in parallel, max 5 concurrent
        var semaphore = new SemaphoreSlim(5, 5);
        var tasks = addresses.Select(async addr =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await FetchBestPairForAddressAsync(addr, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(t => t is not null).Select(t => t!).ToList();
    }

    private async Task<DexTrendingToken?> FetchBestPairForAddressAsync(
        string tokenAddress, CancellationToken ct)
    {
        try
        {
            var json = await _http
                .GetStringAsync($"latest/dex/tokens/{Uri.EscapeDataString(tokenAddress)}", ct)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pairs", out var pairsEl))
                return null;
            if (pairsEl.ValueKind != JsonValueKind.Array)
                return null;

            // Pick pair with highest liquidity.usd
            JsonElement? bestPair = null;
            decimal bestLiq = -1m;

            foreach (var pair in pairsEl.EnumerateArray())
            {
                var liq = GetDecimal(pair, "liquidity", "usd");
                if (liq > bestLiq)
                {
                    bestLiq = liq;
                    bestPair = pair;
                }
            }

            if (bestPair is null) return null;
            return MapPair(bestPair.Value, tokenAddress);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<DexTrendingToken>> FetchSearchFallbackAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http
                .GetStringAsync("latest/dex/search?q=trending", ct)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("pairs", out var pairsEl))
                return [];
            if (pairsEl.ValueKind != JsonValueKind.Array)
                return [];

            // Group by base token address, pick best pair per token
            var pairList = new List<(string addr, JsonElement pair, decimal liq)>();
            foreach (var pair in pairsEl.EnumerateArray())
            {
                var addr = GetNestedString(pair, "baseToken", "address");
                if (string.IsNullOrWhiteSpace(addr)) continue;
                var liq = GetDecimal(pair, "liquidity", "usd");
                pairList.Add((addr, pair.Clone(), liq));
            }

            return pairList
                .GroupBy(x => x.addr, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.liq).First())
                .Select(x => MapPair(x.pair, x.addr))
                .Where(t => t is not null)
                .Select(t => t!)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static DexTrendingToken? MapPair(JsonElement pair, string fallbackAddress)
    {
        var tokenAddress = GetNestedString(pair, "baseToken", "address");
        if (string.IsNullOrWhiteSpace(tokenAddress))
            tokenAddress = fallbackAddress;
        if (string.IsNullOrWhiteSpace(tokenAddress))
            return null;

        var symbol    = GetNestedString(pair, "baseToken", "symbol") ?? "";
        var name      = GetNestedString(pair, "baseToken", "name")   ?? "";
        var chain     = GetString(pair, "chainId")      ?? "";
        var dexId     = GetString(pair, "dexId")        ?? "";
        var pairAddr  = GetString(pair, "pairAddress")  ?? "";
        var url       = GetString(pair, "url")          ?? $"https://dexscreener.com/{chain}/{pairAddr}";

        var priceUsd  = ParseDecimalString(GetString(pair, "priceUsd"));

        var pcM5  = GetNestedDouble(pair, "priceChange", "m5");
        var pcH1  = GetNestedDouble(pair, "priceChange", "h1");
        var pcH6  = GetNestedDouble(pair, "priceChange", "h6");
        var pcH24 = GetNestedDouble(pair, "priceChange", "h24");
        // m15 is not a native DexScreener field — approximate as midpoint of m5 and h1
        var pcM15 = (pcM5 + pcH1) / 2.0;

        var volM5  = GetNestedDecimal(pair, "volume", "m5");
        var volH1  = GetNestedDecimal(pair, "volume", "h1");
        var volH24 = GetNestedDecimal(pair, "volume", "h24");
        // m15 volume ≈ first quarter of the hourly volume
        var volM15 = volH1 / 4m;

        var liqUsd  = GetDecimal(pair, "liquidity", "usd");
        var mcap    = GetDirectDecimal(pair, "marketCap");
        if (mcap == 0m) mcap = GetDirectDecimal(pair, "fdv");

        long pairCreatedAt = 0;
        if (pair.TryGetProperty("pairCreatedAt", out var pca))
        {
            if (pca.ValueKind == JsonValueKind.Number)
                pairCreatedAt = pca.GetInt64();
        }

        return new DexTrendingToken(
            TokenAddress:    tokenAddress,
            Symbol:          symbol,
            Name:            name,
            Chain:           chain,
            DexId:           dexId,
            PairAddress:     pairAddr,
            PriceUsd:        priceUsd,
            PriceChangeM5:   pcM5,
            PriceChangeM15:  pcM15,
            PriceChangeH1:   pcH1,
            PriceChangeH6:   pcH6,
            PriceChangeH24:  pcH24,
            VolumeM5:        volM5,
            VolumeM15:       volM15,
            VolumeH1:        volH1,
            VolumeH24:       volH24,
            LiquidityUsd:    liqUsd,
            MarketCap:       mcap,
            PairCreatedAtMs: pairCreatedAt,
            DexScreenerUrl:  url
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string key)
    {
        return el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string? GetNestedString(JsonElement el, string key1, string key2)
    {
        if (!el.TryGetProperty(key1, out var inner)) return null;
        return GetString(inner, key2);
    }

    private static decimal GetDecimal(JsonElement el, string key1, string key2)
    {
        if (!el.TryGetProperty(key1, out var inner)) return 0m;
        if (!inner.TryGetProperty(key2, out var val)) return 0m;
        return val.ValueKind == JsonValueKind.Number ? val.GetDecimal() : 0m;
    }

    private static decimal GetDirectDecimal(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var val)) return 0m;
        return val.ValueKind == JsonValueKind.Number ? val.GetDecimal() : 0m;
    }

    private static double GetNestedDouble(JsonElement el, string key1, string key2)
    {
        if (!el.TryGetProperty(key1, out var inner)) return 0.0;
        if (!inner.TryGetProperty(key2, out var val)) return 0.0;
        return val.ValueKind == JsonValueKind.Number ? val.GetDouble() : 0.0;
    }

    private static decimal GetNestedDecimal(JsonElement el, string key1, string key2)
    {
        if (!el.TryGetProperty(key1, out var inner)) return 0m;
        if (!inner.TryGetProperty(key2, out var val)) return 0m;
        return val.ValueKind == JsonValueKind.Number ? val.GetDecimal() : 0m;
    }

    private static decimal ParseDecimalString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    public void Dispose()
    {
        if (!_disposed) { _disposed = true; _http.Dispose(); }
    }
}
