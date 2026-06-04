using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Suggests starting parameters for Grid / DCA bots from current market context.
/// Claude when a key is set, otherwise volatility-based offline heuristics so the
/// "AI suggest" button always works.
/// </summary>
public sealed class BotParameterAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public const string Grid = "Grid";
    public const string Dca  = "DCA";

    /// <summary>Grid keys: LowerPrice, UpperPrice, GridCount. DCA keys: AmountUsd, IntervalHours, DipBuyPercent.</summary>
    public async Task<BotParamSuggestion> SuggestAsync(
        string botType,
        decimal price,
        decimal high24h,
        decimal low24h,
        decimal changePct24h,
        CancellationToken ct = default)
    {
        var keys = string.Equals(botType, Grid, StringComparison.OrdinalIgnoreCase)
            ? new[] { "LowerPrice", "UpperPrice", "GridCount" }
            : new[] { "AmountUsd", "IntervalHours", "DipBuyPercent" };

        if (UsesLiveModel && price > 0)
        {
            try
            {
                var ctx = new List<string>
                {
                    $"Price: {price:0.######}",
                    $"24h high: {high24h:0.######}  24h low: {low24h:0.######}",
                    $"24h change: {changePct24h:+0.0;-0.0}%",
                    $"24h range: {Range(high24h, low24h):0.0}%",
                };
                var provider = new BotParameterAiProvider(ApiKey, Model);
                var s = await provider.SuggestAsync(botType, ctx, keys, ct).ConfigureAwait(false);
                if (s is not null && s.Params.Count == keys.Length) return s;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        return BuildOffline(botType, price, high24h, low24h, changePct24h);
    }

    private static BotParamSuggestion BuildOffline(string botType, decimal price, decimal high24h, decimal low24h, decimal chg)
    {
        // Volatility proxy: 24h range as a fraction of price (fallback to |24h change|).
        var rangePct = Range(high24h, low24h);
        if (rangePct <= 0m) rangePct = Math.Max(2m, Math.Abs(chg));

        if (string.Equals(botType, Grid, StringComparison.OrdinalIgnoreCase))
        {
            // Bound the grid around price by ~the 24h range (min ±3%), grid count scales with volatility.
            var halfBand = Math.Max(0.03m, rangePct / 100m / 1.5m); // fraction
            var lower = price > 0 ? Math.Round(price * (1m - halfBand), 6) : low24h;
            var upper = price > 0 ? Math.Round(price * (1m + halfBand), 6) : high24h;
            var grids = (int)Math.Clamp(Math.Round(rangePct / 1.2m), 8m, 40m);
            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["LowerPrice"] = lower, ["UpperPrice"] = upper, ["GridCount"] = grids
            };
            var rationale = $"24h range ≈ {rangePct:0.0}% → grid band ±{halfBand:0.0%} around {price:0.##}, {grids} levels.";
            return new BotParamSuggestion(map, rationale, "Heuristic (offline)", true);
        }

        // DCA: calmer markets → longer interval; choppier → bigger dip-buy threshold.
        var interval = (decimal)Math.Clamp(24 - (double)rangePct, 4, 24);   // 4–24h
        var dip = Math.Clamp(Math.Round(rangePct / 2m, 1), 1.5m, 10m);
        var dcaMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["AmountUsd"] = 50m, ["IntervalHours"] = Math.Round(interval), ["DipBuyPercent"] = dip
        };
        var dcaRationale = $"24h range ≈ {rangePct:0.0}% → DCA every {Math.Round(interval)}h, extra buys on −{dip}% dips.";
        return new BotParamSuggestion(dcaMap, dcaRationale, "Heuristic (offline)", true);
    }

    private static decimal Range(decimal high, decimal low) =>
        low > 0 && high >= low ? (high - low) / low * 100m : 0m;
}
