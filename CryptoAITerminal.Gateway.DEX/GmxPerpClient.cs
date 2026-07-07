using System.Globalization;
using System.Text.Json;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>A GMX v2 GM market (perp market backed by index + long/short collateral tokens).</summary>
public sealed record GmxMarket(
    string MarketToken,
    string IndexToken,
    string LongToken,
    string ShortToken);

/// <summary>A GMX oracle ticker: min/max price for a token (GMX quotes a spread).</summary>
public sealed record GmxTicker(string TokenAddress, decimal MinPrice, decimal MaxPrice)
{
    public decimal MidPrice => (MinPrice + MaxPrice) / 2m;
}

/// <summary>
/// Real GMX v2 reads via the public keyless infra API (Arbitrum): the market list and oracle
/// tickers needed to build an order (you must know the GM market address + acceptable price).
/// Parsing is pure and unit-tested; only the fetch methods touch the network.
///
/// Order placement lives in <see cref="GmxOrderClient"/> and is gated — GMX v2 contract addresses
/// and the createOrder struct layout change across upgrades, so they must be verified against the
/// live deployment before enabling real orders.
/// </summary>
public sealed class GmxPerpClient
{
    private const string ArbitrumApi = "https://arbitrum-api.gmxinfra.io";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<IReadOnlyList<GmxMarket>> GetMarketsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"{ArbitrumApi}/markets", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<GmxMarket>();
            return ParseMarkets(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            return Array.Empty<GmxMarket>();
        }
    }

    public async Task<IReadOnlyList<GmxTicker>> GetTickersAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"{ArbitrumApi}/prices/tickers", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<GmxTicker>();
            return ParseTickers(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            return Array.Empty<GmxTicker>();
        }
    }

    /// <summary>Pure parser for GMX <c>/markets</c>.</summary>
    public static IReadOnlyList<GmxMarket> ParseMarkets(string json)
    {
        var markets = new List<GmxMarket>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("markets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return markets;
            }

            foreach (var m in arr.EnumerateArray())
            {
                var market = Str(m, "marketToken");
                if (string.IsNullOrWhiteSpace(market)) continue;
                markets.Add(new GmxMarket(
                    market!, Str(m, "indexToken") ?? "", Str(m, "longToken") ?? "", Str(m, "shortToken") ?? ""));
            }
        }
        catch
        {
            // malformed → whatever parsed
        }

        return markets;
    }

    /// <summary>Pure parser for GMX <c>/prices/tickers</c> (prices are 30-decimals-minus-token-decimals scaled).</summary>
    public static IReadOnlyList<GmxTicker> ParseTickers(string json)
    {
        var tickers = new List<GmxTicker>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return tickers;
            }

            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var token = Str(t, "tokenAddress");
                if (string.IsNullOrWhiteSpace(token)) continue;
                tickers.Add(new GmxTicker(token!, DecStr(t, "minPrice"), DecStr(t, "maxPrice")));
            }
        }
        catch
        {
            // malformed → whatever parsed
        }

        return tickers;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal DecStr(JsonElement obj, string name)
    {
        var s = Str(obj, name);
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }
}
