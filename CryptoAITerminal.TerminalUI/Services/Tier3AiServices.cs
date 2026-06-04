using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

// ════════════════════════════════════════════════════════════════════════════
//  Tier 3 AI services — DEX trending (#9), dynamic TP/SL (#10),
//  StatArb pair scoring (#11), execution scheduling (#12).
//  Same pattern as the rest: Claude when keyed, deterministic offline otherwise.
// ════════════════════════════════════════════════════════════════════════════

internal static class AiKeys
{
    public static string Default() =>
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        ?? Environment.GetEnvironmentVariable("CRYPTOAI_CLAUDE_KEY")
        ?? string.Empty;
}

/// <summary>#9 — rank trending DEX tokens by momentum vs rug risk.</summary>
public sealed class DexTrendingAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }
    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }
    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public Task<DexTrendingResult> RankAsync(IReadOnlyList<DexTokenInfo> tokens, int topN = 5, CancellationToken ct = default)
    {
        var rows = (tokens ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Symbol))
            .Select(t => new DexTrendRow(t.Symbol, t.TokenAddress, t.PriceUsd, t.PriceChange5m, t.PriceChange1h,
                t.PriceChange24h, t.LiquidityUsd, t.Volume24h, t.MarketCap))
            .ToList();
        return RankRowsAsync(rows, topN, ct);
    }

    /// <summary>Rank pre-built rows (used by the DEX trending panel, which already has rich token data).</summary>
    public async Task<DexTrendingResult> RankRowsAsync(IReadOnlyList<DexTrendRow> rows, int topN = 5, CancellationToken ct = default)
    {
        if (rows is null || rows.Count == 0) return new DexTrendingResult([], "Heuristic (offline)", true);

        if (UsesLiveModel)
        {
            try
            {
                var provider = new DexTrendingAiProvider(ApiKey, Model);
                var ranked = await provider.RankAsync(rows, topN, ct).ConfigureAwait(false);
                if (ranked is not null && ranked.Tokens.Count > 0) return ranked;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* offline */ }
        }
        return BuildOffline(rows, topN);
    }

    private static DexTrendingResult BuildOffline(IReadOnlyList<DexTrendRow> rows, int topN)
    {
        var picks = rows.Select(r =>
        {
            var momentum = Math.Min(40m, (Math.Max(0m, r.Change1h) + Math.Max(0m, r.Change5m) * 2m));
            var liquidity = r.LiquidityUsd > 0 ? Math.Min(25m, (decimal)Math.Log10((double)r.LiquidityUsd + 1) * 4m) : 0m;
            var churn = r.LiquidityUsd > 0 ? r.Volume24h / r.LiquidityUsd : 0m; // volume/liquidity
            var churnScore = Math.Min(20m, churn * 4m);
            var thin = r.LiquidityUsd < 10_000m ? -30m : 0m;     // heavy penalty for rug-thin pools
            var fading = r.Change5m < -3m && r.Change1h > 5m ? -15m : 0m; // parabolic then dumping
            var score = (int)Math.Clamp(momentum + liquidity + churnScore + thin + fading, 0m, 100m);

            string signal =
                thin < 0m ? "AVOID"
              : (r.Change5m < -3m && r.Change1h > 5m) ? "FADING"
              : (r.Change1h > 15m && r.LiquidityUsd >= 25_000m) ? "MOMENTUM"
              : (r.Change5m > 2m && r.Change24h < 10m) ? "EARLY"
              : "AVOID";

            var reason = $"{signal}: 1h {r.Change1h:+0.0;-0.0}%, 5m {r.Change5m:+0.0;-0.0}%, liq ${Compact(r.LiquidityUsd)}, vol/liq {churn:0.0}x.";
            return new DexTrendPick(r.Symbol, r.Address, score, signal, reason);
        })
        .OrderByDescending(p => p.Score)
        .Take(Math.Clamp(topN, 1, 10))
        .ToList();
        return new DexTrendingResult(picks, "Heuristic (offline)", true);
    }

    private static string Compact(decimal v) =>
        v >= 1_000_000m ? $"{v/1_000_000m:0.#}M" : v >= 1_000m ? $"{v/1_000m:0.#}K" : $"{v:0}";
}

/// <summary>#10 — volatility-adaptive TP/SL.</summary>
public sealed class DynamicTpSlAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }
    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }
    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<TpSlSuggestion> SuggestAsync(TpSlContext ctx, CancellationToken ct = default)
    {
        if (UsesLiveModel)
        {
            try
            {
                var provider = new DynamicTpSlAiProvider(ApiKey, Model);
                var s = await provider.SuggestAsync(ctx, ct).ConfigureAwait(false);
                if (s is not null) return s;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* offline */ }
        }
        return BuildOffline(ctx);
    }

    private static TpSlSuggestion BuildOffline(TpSlContext ctx)
    {
        // Volatility proxy: prefer ATR%, fall back to 24h range, floor at 1%.
        var vol = ctx.AtrPct > 0m ? ctx.AtrPct : ctx.Range24hPct / 4m;
        vol = Math.Clamp(vol, 1m, 20m);

        var sl = Math.Round(Math.Clamp(vol * 1.2m, 0.5m, 25m), 2);   // stop ~1.2x volatility
        var tp = Math.Round(Math.Clamp(sl * 1.6m, 0.8m, 50m), 2);    // reward:risk ~1.6
        var trailing = vol >= 5m;                                    // trail in choppy markets

        var rationale = $"Vol ≈ {vol:0.0}% → SL {sl}% (1.2× vol), TP {tp}% (1.6 R:R){(trailing ? ", trailing" : "")}.";
        return new TpSlSuggestion(tp, sl, trailing, rationale, "Heuristic (offline)", true);
    }
}

/// <summary>#11 — score a StatArb pair for tradeability.</summary>
public sealed class StatArbPairAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }
    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }
    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<StatArbPairVerdict> EvaluateAsync(StatArbPairStats s, CancellationToken ct = default)
    {
        if (UsesLiveModel)
        {
            try
            {
                var provider = new StatArbPairAiProvider(ApiKey, Model);
                var v = await provider.EvaluateAsync(s, ct).ConfigureAwait(false);
                if (v is not null) return v;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* offline */ }
        }
        return BuildOffline(s);
    }

    private static StatArbPairVerdict BuildOffline(StatArbPairStats s)
    {
        var goodCorr = Math.Abs(s.Correlation) >= 0.7m;
        var goodHalfLife = s.HalfLifeBars is > 0 and <= 100;
        var stretched = Math.Abs(s.CurrentZScore) >= s.EntryZ && s.EntryZ > 0m;

        string signal;
        bool tradeable = goodCorr && goodHalfLife;
        if (!tradeable) signal = "AVOID";
        else if (!stretched) signal = "WAIT";
        else signal = s.CurrentZScore <= -s.EntryZ ? "LONG_A_SHORT_B" : "SHORT_A_LONG_B";

        // Score: correlation + mean-reversion quality + stretch.
        var corrScore = Math.Min(40m, Math.Abs(s.Correlation) * 40m);
        var hlScore = goodHalfLife ? Math.Max(0m, 30m - s.HalfLifeBars / 4m) : 0m;
        var stretchScore = s.EntryZ > 0m ? Math.Min(30m, Math.Abs(s.CurrentZScore) / s.EntryZ * 20m) : 0m;
        var score = (int)Math.Clamp(corrScore + hlScore + stretchScore, 0m, 100m);

        var reason = signal switch
        {
            "AVOID" => $"Weak fit: corr {s.Correlation:0.00}, half-life {s.HalfLifeBars} bars.",
            "WAIT"  => $"Cointegrated (corr {s.Correlation:0.00}) but z {s.CurrentZScore:0.00} inside ±{s.EntryZ:0.0}.",
            _       => $"z {s.CurrentZScore:0.00} beyond ±{s.EntryZ:0.0} with corr {s.Correlation:0.00} — mean-reversion entry."
        };
        return new StatArbPairVerdict(tradeable, score, signal, reason, "Heuristic (offline)", true);
    }
}

/// <summary>#12 — slice a large order to limit market impact.</summary>
public sealed class ExecutionScheduleAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }
    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }
    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<ExecutionPlan> PlanAsync(OrderExecutionContext ctx, CancellationToken ct = default)
    {
        if (UsesLiveModel && ctx.TotalUsd > 0)
        {
            try
            {
                var provider = new ExecutionScheduleAiProvider(ApiKey, Model);
                var p = await provider.PlanAsync(ctx, ct).ConfigureAwait(false);
                if (p is not null) return p;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* offline */ }
        }
        return BuildOffline(ctx);
    }

    private static ExecutionPlan BuildOffline(OrderExecutionContext ctx)
    {
        if (ctx.TotalUsd <= 0)
            return new ExecutionPlan(1, 30, 0m, "Empty order.", "Heuristic (offline)", true);

        // Keep each child to a small fraction of top-of-book depth (fallback: 5% of 24h volume).
        var childCap = ctx.BookDepthUsd > 0 ? ctx.BookDepthUsd * 0.5m
                     : ctx.Volume24hUsd > 0 ? ctx.Volume24hUsd * 0.0005m
                     : ctx.TotalUsd / 5m;
        if (childCap <= 0) childCap = ctx.TotalUsd / 5m;

        var slices = (int)Math.Clamp(Math.Ceiling(ctx.TotalUsd / childCap), 1m, 200m);

        // Urgency shapes the cadence.
        var interval = ctx.Urgency.Trim().ToLowerInvariant() switch
        {
            "high"   => 10,
            "low"    => 120,
            _        => 45, // medium
        };

        var sliceUsd = Math.Round(ctx.TotalUsd / slices, 2);
        var rationale = $"{slices} slices of ~${sliceUsd:0} every {interval}s (child ≤ ${childCap:0} to limit impact, {ctx.Urgency} urgency).";
        return new ExecutionPlan(slices, interval, sliceUsd, rationale, "Heuristic (offline)", true);
    }
}
