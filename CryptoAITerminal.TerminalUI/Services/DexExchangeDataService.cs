using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>One market on a DEX exchange (live).</summary>
public sealed record DexExchangeMarket(
    string Symbol,
    decimal Price,
    decimal Change24hPct,
    decimal Volume24hUsd,
    decimal Funding8hPct,
    int MaxLeverage);

/// <summary>Aggregated live stats for a DEX exchange.</summary>
public sealed record DexExchangeLiveStats(
    string Key,
    decimal Vol24hUsd,
    decimal RepFunding8hPct,
    int MaxLeverage,
    IReadOnlyList<DexExchangeMarket> Markets,
    bool IsLive);

/// <summary>
/// Pulls live market data from the real public APIs of the listed DEX exchanges —
/// Hyperliquid (<c>/info metaAndAssetCtxs</c>) and dYdX v4 (<c>/perpetualMarkets</c>).
/// Parsing is split into pure, unit-tested methods; the network call degrades to null
/// (caller keeps demo values) when an endpoint is unreachable. Read-only, keyless.
/// </summary>
public sealed class DexExchangeDataService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<DexExchangeLiveStats?> FetchAsync(string key, CancellationToken ct = default)
    {
        try
        {
            switch (key?.ToUpperInvariant())
            {
                case "HYPERLIQUID":
                {
                    using var content = new StringContent("{\"type\":\"metaAndAssetCtxs\"}", Encoding.UTF8, "application/json");
                    using var resp = await Http.PostAsync("https://api.hyperliquid.xyz/info", content, ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    return ParseHyperliquid(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                }
                case "DYDX":
                {
                    using var resp = await Http.GetAsync("https://indexer.dydx.trade/v4/perpetualMarkets", ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    return ParseDydx(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                }
                case "UNISWAP":
                {
                    using var resp = await Http.GetAsync("https://api.geckoterminal.com/api/v2/networks/eth/dexes/uniswap_v3/pools?page=1", ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    return ParseGeckoTerminalPools(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "UNISWAP");
                }
                default:
                    return null; // GMX / Vertex: perp/CLOB feeds need bespoke integration
            }
        }
        catch
        {
            return null;
        }
    }

    // ── Pure parsers (unit-tested) ────────────────────────────────────────────

    public static DexExchangeLiveStats? ParseHyperliquid(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
            {
                return null;
            }

            var universe = root[0].GetProperty("universe");
            var ctxs = root[1];
            var count = Math.Min(universe.GetArrayLength(), ctxs.GetArrayLength());

            var markets = new List<DexExchangeMarket>(count);
            for (var i = 0; i < count; i++)
            {
                var u = universe[i];
                var c = ctxs[i];
                if (u.TryGetProperty("isDelisted", out var del) && del.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var name = u.GetProperty("name").GetString() ?? "";
                var maxLev = u.TryGetProperty("maxLeverage", out var ml) ? ml.GetInt32() : 0;
                var mark = Dec(c, "markPx");
                var prev = Dec(c, "prevDayPx");
                var vol = Dec(c, "dayNtlVlm");
                var funding = Dec(c, "funding"); // hourly
                var chg = prev > 0 ? (mark - prev) / prev * 100m : 0m;
                markets.Add(new DexExchangeMarket(name, mark, chg, vol, funding * 8m * 100m, maxLev));
            }

            return Aggregate("HYPERLIQUID", markets);
        }
        catch
        {
            return null;
        }
    }

    public static DexExchangeLiveStats? ParseDydx(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("markets", out var markets) || markets.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var list = new List<DexExchangeMarket>();
            foreach (var m in markets.EnumerateObject())
            {
                var v = m.Value;
                var ticker = v.TryGetProperty("ticker", out var t) ? t.GetString() ?? m.Name : m.Name;
                var symbol = ticker.Replace("-USD", "", StringComparison.OrdinalIgnoreCase);
                var price = DecStr(v, "oraclePrice");
                var chgAbs = DecStr(v, "priceChange24H");
                var prev = price - chgAbs;
                var chgPct = prev != 0 ? chgAbs / prev * 100m : 0m;
                var vol = DecStr(v, "volume24H");
                var funding = DecStr(v, "nextFundingRate"); // hourly
                var imf = DecStr(v, "initialMarginFraction");
                var maxLev = imf > 0 ? (int)Math.Round(1m / imf) : 0;
                list.Add(new DexExchangeMarket(symbol, price, chgPct, vol, funding * 8m * 100m, maxLev));
            }

            return Aggregate("DYDX", list);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse GeckoTerminal pool list (AMM SWAP venues, e.g. Uniswap V3).</summary>
    public static DexExchangeLiveStats? ParseGeckoTerminalPools(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var markets = new List<DexExchangeMarket>();
            foreach (var pool in data.EnumerateArray())
            {
                if (!pool.TryGetProperty("attributes", out var a))
                {
                    continue;
                }

                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var parts = name.Split(" / ", StringSplitOptions.RemoveEmptyEntries);
                var baseSym = parts.Length > 0 ? parts[0].Trim() : name;
                var quoteSym = parts.Length > 1 ? parts[1].Split(' ')[0] : "";
                var symbol = quoteSym.Length > 0 ? $"{baseSym}/{quoteSym}" : baseSym;

                var price = DecStr(a, "base_token_price_usd");
                var chg = NestedH24(a, "price_change_percentage");
                var vol = NestedH24(a, "volume_usd");
                markets.Add(new DexExchangeMarket(symbol, price, chg, vol, 0m, 0));
            }

            return Aggregate(key, markets);
        }
        catch
        {
            return null;
        }
    }

    private static decimal NestedH24(JsonElement a, string prop) =>
        a.TryGetProperty(prop, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty("h24", out var h) &&
        decimal.TryParse(h.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static DexExchangeLiveStats? Aggregate(string key, List<DexExchangeMarket> markets)
    {
        markets = markets.Where(m => m.Price > 0m).OrderByDescending(m => m.Volume24hUsd).ToList();
        if (markets.Count == 0)
        {
            return null;
        }

        var totalVol = markets.Sum(m => m.Volume24hUsd);
        var maxLev = markets.Max(m => m.MaxLeverage);
        var rep = markets.FirstOrDefault(m => m.Symbol.Equals("BTC", StringComparison.OrdinalIgnoreCase)) ?? markets[0];
        return new DexExchangeLiveStats(key, totalVol, rep.Funding8hPct, maxLev, markets.Take(14).ToList(), true);
    }

    public static string FormatUsdCompact(decimal v)
    {
        var ci = CultureInfo.InvariantCulture;
        return v switch
        {
            >= 1_000_000_000m => "$" + (v / 1_000_000_000m).ToString("0.##", ci) + "B",
            >= 1_000_000m => "$" + (v / 1_000_000m).ToString("0.##", ci) + "M",
            >= 1_000m => "$" + (v / 1_000m).ToString("0.##", ci) + "K",
            _ => "$" + v.ToString("0", ci),
        };
    }

    private static decimal Dec(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static decimal DecStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
}
