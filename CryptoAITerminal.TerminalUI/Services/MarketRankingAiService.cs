using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Ranks the strongest scanner opportunities. Uses Claude when a key is configured,
/// otherwise a deterministic offline heuristic — so the scanner always shows an
/// "AI picks" panel, including in the demo flow without keys.
/// </summary>
public sealed class MarketRankingAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<MarketRankingResult> RankAsync(
        IReadOnlyList<ScanResult> results,
        int topN = 5,
        CancellationToken ct = default)
    {
        var rows = (results ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Symbol))
            .Select(r => new MarketScanRow(
                r.Symbol, r.Exchange, r.LastPrice, r.ChangePct24h,
                r.Volume24hUsd, r.Rsi14, r.ActivityScore, r.IsHot))
            .ToList();

        if (rows.Count == 0)
            return new MarketRankingResult([], "Heuristic (offline)", true);

        if (UsesLiveModel)
        {
            try
            {
                var provider = new MarketRankingAiProvider(ApiKey, Model);
                var ranked = await provider.RankAsync(rows, topN, ct).ConfigureAwait(false);
                if (ranked is not null && ranked.Opportunities.Count > 0) return ranked;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        return BuildOffline(rows, topN);
    }

    /// <summary>
    /// Deterministic scoring: momentum magnitude + RSI extremity + a liquidity/activity
    /// bonus. Bias = LONG for oversold-and-rising or strong up-move, SHORT for the mirror.
    /// </summary>
    private static MarketRankingResult BuildOffline(IReadOnlyList<MarketScanRow> rows, int topN)
    {
        var scored = rows.Select(r =>
        {
            var momentum = Math.Min(40m, Math.Abs(r.ChangePct24h) * 2m);          // up to 40
            var rsiEdge  = r.Rsi14 <= 0 ? 0m : Math.Max(0m, 30m - r.Rsi14)        // oversold
                         + Math.Max(0m, r.Rsi14 - 70m);                            // overbought
            rsiEdge = Math.Min(30m, rsiEdge);                                      // up to 30
            var liquidity = r.Volume24hUsd > 0 ? Math.Min(20m, (decimal)Math.Log10((double)r.Volume24hUsd + 1) * 3m) : 0m;
            var activity  = Math.Min(10m, r.ActivityScore / 10m) + (r.IsHot ? 5m : 0m);
            var score = (int)Math.Clamp(momentum + rsiEdge + liquidity + activity, 0m, 100m);

            string bias =
                (r.Rsi14 is > 0 and < 35 && r.ChangePct24h > -2m) || r.ChangePct24h >= 5m ? "LONG"
              : (r.Rsi14 > 68 && r.ChangePct24h < 2m) || r.ChangePct24h <= -5m ? "SHORT"
              : "WATCH";

            var reason = BuildReason(r, bias);
            return new RankedOpportunity(r.Symbol, score, bias, reason);
        })
        .OrderByDescending(o => o.Score)
        .Take(Math.Clamp(topN, 1, 10))
        .ToList();

        return new MarketRankingResult(scored, "Heuristic (offline)", true);
    }

    private static string BuildReason(MarketScanRow r, string bias)
    {
        var bits = new List<string>
        {
            $"24h {r.ChangePct24h:+0.0;-0.0}%",
            $"RSI {r.Rsi14:0}"
        };
        if (r.Rsi14 is > 0 and < 30) bits.Add("oversold");
        else if (r.Rsi14 > 70) bits.Add("overbought");
        if (r.IsHot) bits.Add("hot activity");
        if (r.Volume24hUsd >= 1_000_000m) bits.Add($"liq ${r.Volume24hUsd/1_000_000m:0.#}M");
        return $"{bias}: " + string.Join(", ", bits) + ".";
    }
}
