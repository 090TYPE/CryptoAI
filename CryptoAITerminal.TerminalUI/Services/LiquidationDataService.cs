using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class LiquidationDataService : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string? _apiKey = Environment.GetEnvironmentVariable("COINGLASS_API_KEY");
    private bool _disposed;

    public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);
    public string DataSourceLabel => HasApiKey ? "CoinGlass" : "Estimated (Leverage Model)";

    public async Task<IReadOnlyList<LiquidationLevel>> GetLevelsAsync(
        string symbol, CancellationToken ct = default)
    {
        try
        {
            return HasApiKey
                ? await FetchCoinGlassAsync(symbol, ct)
                : await EstimateFromBinanceAsync(symbol, ct);
        }
        catch { return []; }
    }

    // CoinGlass fetch
    private async Task<IReadOnlyList<LiquidationLevel>> FetchCoinGlassAsync(string symbol, CancellationToken ct)
    {
        var url = $"https://open-api.coinglass.com/public/v2/liquidation_chart?ex=Binance&pair={symbol}&interval=12";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("coinglassSecret", _apiKey);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return await EstimateFromBinanceAsync(symbol, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseCoinGlass(json);
    }

    private static IReadOnlyList<LiquidationLevel> ParseCoinGlass(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return [];
        if (!data.TryGetProperty("liqDataList", out var list)) return [];
        var result = new List<LiquidationLevel>();
        foreach (var item in list.EnumerateArray())
        {
            var price = item.TryGetProperty("price", out var p) ? p.GetDecimal() : 0m;
            var buyLiq = item.TryGetProperty("buyLiquidationSum", out var b) ? b.GetDecimal() : 0m;
            var sellLiq = item.TryGetProperty("sellLiquidationSum", out var s) ? s.GetDecimal() : 0m;
            if (price > 0) result.Add(new LiquidationLevel(price, buyLiq, sellLiq));
        }
        return result.OrderBy(l => l.Price).ToList();
    }

    // ── Binance estimation (leverage model, no OB) ───────────────────────────
    // Order book data is NOT used here because all BTC futures bids cluster within
    // 0.5% of current price, inflating the scale and making leverage peaks invisible.
    // Instead we use a pure theoretical model: each leverage tier creates a
    // liquidation cluster at distance (1/leverage) from current price.
    private async Task<IReadOnlyList<LiquidationLevel>> EstimateFromBinanceAsync(
        string symbol, CancellationToken ct)
    {
        var priceJson = await _http.GetStringAsync(
            $"https://fapi.binance.com/fapi/v1/ticker/price?symbol={symbol}", ct);
        using var doc = JsonDocument.Parse(priceJson);
        var currentPrice = decimal.Parse(
            doc.RootElement.GetProperty("price").GetString() ?? "0",
            CultureInfo.InvariantCulture);
        return currentPrice <= 0 ? [] : BuildLeverageModel(currentPrice);
    }

    /// <summary>
    /// Generates estimated liquidation levels from a leverage distribution model.
    /// Publicly accessible so callers can preview without an HTTP call.
    /// </summary>
    public static IReadOnlyList<LiquidationLevel> BuildLeverageModel(decimal price)
    {
        // (leverage, longWeight, shortWeight)
        // Weight = share of total OI typically at this leverage tier
        var tiers = new (decimal lev, decimal lw, decimal sw)[]
        {
            (500m, 0.018m, 0.018m),
            (200m, 0.030m, 0.030m),
            (125m, 0.042m, 0.042m),
            (100m, 0.058m, 0.058m),
            (75m,  0.038m, 0.038m),
            (50m,  0.088m, 0.088m),
            (40m,  0.048m, 0.048m),
            (33m,  0.052m, 0.052m),
            (25m,  0.095m, 0.095m),
            (20m,  0.125m, 0.125m),
            (15m,  0.068m, 0.068m),
            (10m,  0.175m, 0.175m),
            (7m,   0.048m, 0.048m),
            (5m,   0.070m, 0.070m),
            (3m,   0.045m, 0.045m),
        };

        // Scale total OI by asset price tier
        var totalOi = price >= 10_000m ? 3_500_000_000m   // BTC
                    : price >= 1_000m  ? 1_800_000_000m   // ETH
                    : price >= 100m    ?   600_000_000m   // BNB, SOL...
                    :                      250_000_000m;  // smaller alts

        var levels = new List<LiquidationLevel>(tiers.Length * 2);
        foreach (var (lev, lw, sw) in tiers)
        {
            var dist   = 1m / lev;
            var longP  = price * (1m - dist);
            var shortP = price * (1m + dist);
            if (longP  > 0) levels.Add(new LiquidationLevel(longP,  totalOi * lw, 0m));
            if (shortP > 0) levels.Add(new LiquidationLevel(shortP, 0m, totalOi * sw));
        }
        return levels.OrderBy(l => l.Price).ToList();
    }

    /// <summary>Lightweight single-price fetch — used by the 5-second live-update ticker.</summary>
    public async Task<decimal> FetchCurrentPriceAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(
                $"https://fapi.binance.com/fapi/v1/ticker/price?symbol={symbol}", ct);
            using var doc = JsonDocument.Parse(json);
            return decimal.Parse(
                doc.RootElement.GetProperty("price").GetString() ?? "0",
                CultureInfo.InvariantCulture);
        }
        catch { return 0m; }
    }

    public void Dispose() { if (!_disposed) { _disposed = true; _http.Dispose(); } }
}
