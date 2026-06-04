using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Options-market snapshot for one asset plus the trader's directional view.</summary>
/// <param name="AtmIv">Annualised ATM implied volatility, percent.</param>
/// <param name="Skew25Delta">25Δ put IV − call IV; positive = puts bid = downside fear.</param>
/// <param name="Direction">"bullish" | "neutral" | "bearish".</param>
public sealed record OptionsStrategyInput(
    string Asset,
    decimal AtmIv,
    decimal Skew25Delta,
    decimal PutCallRatio,
    decimal IndexPrice,
    string Direction);

public sealed record OptionsStrategyResult(
    string Strategy, string Direction, string IvRegime,
    string Rationale, IReadOnlyList<string> Considerations, string Source, bool IsFallback);

/// <summary>
/// Suggests an options strategy from the Deribit IV / skew / put-call snapshot plus a
/// directional view. The strategy is picked <b>deterministically</b> from an IV-regime ×
/// direction matrix (buy premium when vol is cheap, sell it when rich); Claude only
/// enriches the rationale when a key is configured. Reuses the generic
/// <see cref="MarketInsightAiService"/> — no new provider — and works fully offline.
/// Educational, not financial advice.
/// </summary>
public sealed class OptionsStrategyService
{
    // Vol stance vocabulary handed to the model for the signal field.
    private static readonly string[] Vocabulary = ["BUY_PREMIUM", "NEUTRAL", "SELL_PREMIUM"];

    private readonly MarketInsightAiService _ai = new();

    public bool UsesLiveModel => _ai.UsesLiveModel;

    public void ConfigureAi(string? apiKey, string? model = null)
    {
        if (apiKey is not null) _ai.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) _ai.Model = model;
    }

    /// <summary>Deterministic strategy pick. Pure and unit-tested.</summary>
    public static OptionsStrategyResult Recommend(OptionsStrategyInput i)
    {
        var dir = Normalize(i.Direction);
        var regime = i.AtmIv >= 70m ? "High" : i.AtmIv <= 45m ? "Low" : "Moderate";

        // IV regime sets the premium stance; direction picks the structure.
        var (strategy, stance) = (regime, dir) switch
        {
            ("High", "bullish") => ("Bull put spread", "sell premium"),
            ("High", "bearish") => ("Bear call spread", "sell premium"),
            ("High", _)         => ("Iron condor (short premium)", "sell premium"),

            ("Low", "bullish")  => ("Long call / bull call spread", "buy premium"),
            ("Low", "bearish")  => ("Long put / bear put spread", "buy premium"),
            ("Low", _)          => ("Long straddle", "buy premium"),

            (_, "bullish")      => ("Bull call spread", "balanced"),
            (_, "bearish")      => ("Bear put spread", "balanced"),
            _                   => ("Iron condor", "balanced"),
        };

        var considerations = new List<string>
        {
            $"ATM IV {i.AtmIv:F0}% → {regime.ToLowerInvariant()} volatility ({stance})."
        };

        // Skew (25Δ): which wing is bid.
        if (i.Skew25Delta > 2m)
            considerations.Add($"25Δ skew {i.Skew25Delta:+0.0}% — puts are bid; downside protection is pricey, selling put premium is richer.");
        else if (i.Skew25Delta < -2m)
            considerations.Add($"25Δ skew {i.Skew25Delta:+0.0}% — calls are bid; upside calls are rich.");
        else
            considerations.Add("25Δ skew is roughly flat — no strong wing bias.");

        // Put/Call positioning.
        if (i.PutCallRatio >= 1m)
            considerations.Add($"P/C ratio {i.PutCallRatio:0.00} — put-heavy positioning (cautious crowd).");
        else if (i.PutCallRatio is > 0m and < 0.7m)
            considerations.Add($"P/C ratio {i.PutCallRatio:0.00} — call-heavy positioning (optimistic crowd).");

        considerations.Add(stance == "buy premium"
            ? "Long premium: max loss is the debit paid — define your time horizon, theta works against you."
            : stance == "sell premium"
                ? "Short premium: cap the risk with spreads, never sell naked; size for the max loss."
                : "Use defined-risk spreads and size for the worst case.");

        var rationale =
            $"With {regime.ToLowerInvariant()} IV ({i.AtmIv:F0}%) and a {dir} view on {i.Asset}, a {strategy} fits — " +
            (stance == "buy premium" ? "volatility is cheap, so buying optionality is favoured."
             : stance == "sell premium" ? "volatility is rich, so collecting premium is favoured."
             : "express the directional view with a defined-risk spread.") +
            " Educational only — not financial advice.";

        return new OptionsStrategyResult(strategy, dir, regime, rationale, considerations, "Heuristic (offline)", true);
    }

    /// <summary>Deterministic pick, with a Claude-written rationale + extra notes when keyed.</summary>
    public async Task<OptionsStrategyResult> RecommendAsync(OptionsStrategyInput input, CancellationToken ct = default)
    {
        var baseResult = Recommend(input);
        if (!UsesLiveModel) return baseResult;

        const string role =
            "You are an options strategist. A deterministic engine has already chosen the structure; explain " +
            "in one or two sentences why it fits the volatility regime and the view, and add any key caveats. " +
            "Do not change the chosen strategy. Educational, not financial advice.";

        var insight = await _ai.InterpretAsync(role, BuildLines(input, baseResult), Vocabulary,
            () => new InsightResult(baseResult.Rationale, StanceSignal(baseResult), baseResult.Considerations.ToArray(), baseResult.Source, true),
            ct).ConfigureAwait(false);

        if (insight.IsFallback) return baseResult;

        var considerations = baseResult.Considerations.ToList();
        foreach (var b in insight.Bullets)
            if (!considerations.Contains(b, StringComparer.OrdinalIgnoreCase)) considerations.Add(b);

        return new OptionsStrategyResult(
            baseResult.Strategy, baseResult.Direction, baseResult.IvRegime,
            string.IsNullOrWhiteSpace(insight.Summary) ? baseResult.Rationale : insight.Summary,
            considerations, insight.Source, false);
    }

    private static string StanceSignal(OptionsStrategyResult r) => r.IvRegime switch
    {
        "High" => "SELL_PREMIUM",
        "Low"  => "BUY_PREMIUM",
        _      => "NEUTRAL"
    };

    private static IReadOnlyList<string> BuildLines(OptionsStrategyInput i, OptionsStrategyResult r)
    {
        var lines = new List<string>
        {
            $"Asset: {i.Asset} (index {Usd(i.IndexPrice)})",
            $"Directional view: {r.Direction}",
            $"ATM IV: {i.AtmIv:F1}% ({r.IvRegime} regime)",
            $"25Δ skew: {i.Skew25Delta:+0.0;-0.0;0}% (put IV − call IV)",
            $"Put/Call ratio: {i.PutCallRatio:0.00}",
            $"Engine pick: {r.Strategy}",
        };
        lines.AddRange(r.Considerations.Select(c => "Note: " + c));
        return lines;
    }

    private static string Normalize(string? d)
    {
        d = (d ?? "").Trim().ToLowerInvariant();
        return d is "bullish" or "bearish" or "neutral" ? d : "neutral";
    }

    private static string Usd(decimal v) => "$" + v.ToString("N0", CultureInfo.InvariantCulture);
}
