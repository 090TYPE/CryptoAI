using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// A proposed order plus the account context needed to judge it. Cash equity is
/// optional (0 = unknown) — the app has no single coherent equity figure, so the
/// heuristic falls back to the exposure limit when equity isn't supplied.
/// </summary>
public sealed record PreTradeRiskInput(
    string Symbol,
    string Side,                       // "buy" | "sell"
    decimal OrderUsd,
    decimal EquityUsd,                 // 0 = unknown
    decimal ExistingExposureUsd,       // sum of |notional| across open positions
    decimal SameSymbolExposureUsd,     // notional already held in this symbol
    decimal MaxExposureUsd,            // configured exposure cap (0 = unknown → skip)
    int Leverage,
    decimal DailyPnlUsd,               // today's realized P&L (negative = loss)
    decimal MaxDailyLossUsd);          // configured daily-loss cap (0 = unknown → skip)

/// <param name="Verdict">APPROVE | CAUTION | BLOCK.</param>
/// <param name="Score">0..100 holistic risk score (higher = riskier).</param>
public sealed record PreTradeRiskResult(
    string Verdict, int Score, string Rationale,
    IReadOnlyList<string> Reasons, string Source, bool IsFallback);

/// <summary>
/// Holistic pre-trade risk check. Unlike the hard-limit
/// <see cref="RiskManager.RiskManager"/> (a boolean gate), this scores a proposed
/// order against the whole book — size, projected exposure, concentration, leverage
/// and daily-loss proximity — and returns APPROVE / CAUTION / BLOCK with reasons.
///
/// The verdict and score are computed <b>deterministically</b> (you want a risk gate
/// to be predictable, not subject to model variance); when a Claude key is configured
/// it additionally enriches the human-readable rationale via the generic
/// <see cref="MarketInsightAiService"/>. Works fully offline without a key.
/// This is advisory — it never blocks the actual order path.
/// </summary>
public sealed class PreTradeRiskService
{
    private static readonly string[] Vocabulary = ["APPROVE", "CAUTION", "BLOCK"];

    private readonly MarketInsightAiService _ai = new();

    public bool UsesLiveModel => _ai.UsesLiveModel;

    public void ConfigureAi(string? apiKey, string? model = null)
    {
        if (apiKey is not null) _ai.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) _ai.Model = model;
    }

    /// <summary>
    /// Deterministic risk gate. Pure and unit-tested — the verdict/score never depend
    /// on the model. Returns the offline source label.
    /// </summary>
    public static PreTradeRiskResult Evaluate(PreTradeRiskInput i)
    {
        var reasons = new List<string>();
        decimal score = 0m;

        var projectedExposure = i.ExistingExposureUsd + Math.Max(0m, i.OrderUsd);

        // 1) Order size relative to the account (equity if known, else the exposure cap).
        var sizeBase = i.EquityUsd > 0 ? i.EquityUsd : i.MaxExposureUsd;
        if (sizeBase > 0 && i.OrderUsd > 0)
        {
            var sizePct = i.OrderUsd / sizeBase;
            score += Math.Min(40m, sizePct * 100m);
            if (sizePct >= 0.25m)
                reasons.Add($"Order is {sizePct:P0} of {(i.EquityUsd > 0 ? "equity" : "the exposure cap")} — sizeable.");
        }

        // 2) Projected total exposure vs the configured cap.
        bool overCap = false;
        if (i.MaxExposureUsd > 0)
        {
            var ratio = projectedExposure / i.MaxExposureUsd;
            score += Math.Clamp((ratio - 0.5m) * 60m, 0m, 40m);
            if (ratio > 1m)
            {
                overCap = true;
                reasons.Add($"Projected exposure {Usd(projectedExposure)} EXCEEDS the {Usd(i.MaxExposureUsd)} cap.");
            }
            else if (ratio >= 0.8m)
                reasons.Add($"Projected exposure {Usd(projectedExposure)} is {ratio:P0} of the cap.");
        }

        // 3) Concentration — share of the book that would sit in this one symbol.
        var sameSymbolAfter = i.SameSymbolExposureUsd + Math.Max(0m, i.OrderUsd);
        if (projectedExposure > 0)
        {
            var concentration = sameSymbolAfter / projectedExposure;
            if (concentration >= 0.5m)
            {
                score += Math.Min(20m, (concentration - 0.5m) * 60m);
                reasons.Add($"{i.Symbol} would be {concentration:P0} of total exposure — concentrated.");
            }
        }

        // 4) Leverage.
        if (i.Leverage > 1)
        {
            score += Math.Min(15m, (i.Leverage - 1) * 3m);
            if (i.Leverage >= 10) reasons.Add($"{i.Leverage}x leverage materially raises liquidation risk.");
        }

        // 5) Daily-loss proximity.
        bool lossBreached = false;
        if (i.MaxDailyLossUsd > 0 && i.DailyPnlUsd < 0)
        {
            var lossRatio = -i.DailyPnlUsd / i.MaxDailyLossUsd;
            score += Math.Clamp(lossRatio * 20m, 0m, 20m);
            if (lossRatio >= 1m)
            {
                lossBreached = true;
                reasons.Add($"Daily loss {Usd(-i.DailyPnlUsd)} has hit the {Usd(i.MaxDailyLossUsd)} limit.");
            }
            else if (lossRatio >= 0.7m)
                reasons.Add($"Daily loss is {lossRatio:P0} of the limit — little room left.");
        }

        var s = (int)Math.Clamp(Math.Round(score), 0m, 100m);

        // Hard blocks override the score; otherwise band by score.
        var verdict = overCap || lossBreached ? "BLOCK"
                    : s >= 70 ? "BLOCK"
                    : s >= 40 ? "CAUTION"
                    : "APPROVE";

        if (reasons.Count == 0)
            reasons.Add("Within size, exposure and loss limits — no flags.");

        var rationale = verdict switch
        {
            "BLOCK"   => $"High risk (score {s}/100). This trade breaches or strains your limits — reconsider or downsize.",
            "CAUTION" => $"Elevated risk (score {s}/100). Acceptable but tighten sizing/stops and watch the flags below.",
            _         => $"Low risk (score {s}/100). Sizing and exposure look reasonable."
        };

        return new PreTradeRiskResult(verdict, s, rationale, reasons, "Heuristic (offline)", true);
    }

    /// <summary>
    /// Deterministic verdict/score, optionally with a Claude-written rationale + extra
    /// reasons when a key is configured. Falls back to the offline result on any error.
    /// </summary>
    public async Task<PreTradeRiskResult> EvaluateAsync(PreTradeRiskInput input, CancellationToken ct = default)
    {
        var baseResult = Evaluate(input);
        if (!UsesLiveModel) return baseResult;

        const string role =
            "You are a pre-trade risk officer for a crypto trader. A deterministic engine has already " +
            "scored the trade; explain the risk crisply in one or two sentences and list the key concerns. " +
            "Do not contradict the engine's verdict. This is not financial advice.";

        var insight = await _ai.InterpretAsync(role, BuildLines(input, baseResult), Vocabulary,
            () => new InsightResult(baseResult.Rationale, baseResult.Verdict, baseResult.Reasons.ToArray(), baseResult.Source, true),
            ct).ConfigureAwait(false);

        if (insight.IsFallback) return baseResult;

        // Keep the deterministic verdict + score; use the model's narrative & any extra reasons.
        var reasons = baseResult.Reasons.ToList();
        foreach (var b in insight.Bullets)
            if (!reasons.Contains(b, StringComparer.OrdinalIgnoreCase)) reasons.Add(b);

        return new PreTradeRiskResult(
            baseResult.Verdict, baseResult.Score,
            string.IsNullOrWhiteSpace(insight.Summary) ? baseResult.Rationale : insight.Summary,
            reasons, insight.Source, false);
    }

    private static IReadOnlyList<string> BuildLines(PreTradeRiskInput i, PreTradeRiskResult r)
    {
        var lines = new List<string>
        {
            $"Proposed: {i.Side.ToUpperInvariant()} {Usd(i.OrderUsd)} of {i.Symbol}" + (i.Leverage > 1 ? $" at {i.Leverage}x" : ""),
            $"Engine verdict: {r.Verdict} (risk score {r.Score}/100)",
            $"Existing exposure: {Usd(i.ExistingExposureUsd)}" + (i.MaxExposureUsd > 0 ? $" of {Usd(i.MaxExposureUsd)} cap" : ""),
            $"Already in {i.Symbol}: {Usd(i.SameSymbolExposureUsd)}",
            $"Today's P&L: {Usd(i.DailyPnlUsd)}" + (i.MaxDailyLossUsd > 0 ? $" (loss cap {Usd(i.MaxDailyLossUsd)})" : ""),
        };
        if (i.EquityUsd > 0) lines.Add($"Account equity: {Usd(i.EquityUsd)}");
        lines.AddRange(r.Reasons.Select(x => "Flag: " + x));
        return lines;
    }

    private static string Usd(decimal v) => "$" + v.ToString("N2", CultureInfo.InvariantCulture);
}
