using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// One snapshot of everything the morning briefing reasons over. The host gathers
/// these from the panels it already has (positions, news pulse, sentiment, scanner)
/// so the service stays decoupled and the offline heuristic is unit-testable.
/// </summary>
/// <param name="NewsPulseScore">-100..+100 (bearish→bullish), from the news pulse.</param>
/// <param name="FearGreed">0..100 Fear &amp; Greed index; 50 = unknown/neutral.</param>
public sealed record BriefingInput(
    int OpenPositions,
    decimal UnrealizedPnlUsd,
    string? BestSymbol,
    decimal BestPnlUsd,
    string? WorstSymbol,
    decimal WorstPnlUsd,
    int NewsPulseScore,
    string NewsPulseLabel,
    int FearGreed,
    IReadOnlyList<string> TopPicks);

/// <summary>
/// Daily AI briefing: ties the existing AI/data panels together into one "morning
/// read" — a short narrative + a RISK_ON/NEUTRAL/RISK_OFF posture + a few bullets.
/// Reuses the generic <see cref="MarketInsightAiService"/> (no new provider) and, as
/// with every AI feature, ships a deterministic offline heuristic so it works keyless.
/// </summary>
public sealed class DailyBriefingService
{
    private static readonly string[] Vocabulary = ["RISK_ON", "NEUTRAL", "RISK_OFF"];

    private readonly MarketInsightAiService _ai = new();

    public bool UsesLiveModel => _ai.UsesLiveModel;

    /// <summary>Share the Claude key/model from the AI Bot tab (like every other AI service).</summary>
    public void ConfigureAi(string? apiKey, string? model = null)
    {
        if (apiKey is not null) _ai.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) _ai.Model = model;
    }

    public Task<InsightResult> BuildAsync(BriefingInput input, CancellationToken ct = default)
    {
        const string role =
            "You are a portfolio briefing assistant for a crypto trader. Give a concise, balanced " +
            "morning briefing: where the book stands, the market backdrop, and what to watch today. " +
            "Be specific and avoid hype. This is not financial advice.";

        return _ai.InterpretAsync(role, BuildLines(input), Vocabulary, () => BuildOffline(input), ct);
    }

    /// <summary>Pre-formatted, privacy-safe fact lines handed to the model.</summary>
    private static IReadOnlyList<string> BuildLines(BriefingInput i)
    {
        var lines = new List<string>
        {
            $"Open positions: {i.OpenPositions}",
            $"Unrealized P&L: {Usd(i.UnrealizedPnlUsd)}",
        };
        if (!string.IsNullOrWhiteSpace(i.BestSymbol))
            lines.Add($"Best position: {i.BestSymbol} {Usd(i.BestPnlUsd)}");
        if (!string.IsNullOrWhiteSpace(i.WorstSymbol))
            lines.Add($"Worst position: {i.WorstSymbol} {Usd(i.WorstPnlUsd)}");
        lines.Add($"News pulse: {i.NewsPulseLabel} (score {i.NewsPulseScore} on -100..100)");
        lines.Add($"Fear & Greed index: {i.FearGreed} / 100");
        if (i.TopPicks.Count > 0)
            lines.Add("Scanner top picks: " + string.Join("; ", i.TopPicks.Take(5)));
        return lines;
    }

    /// <summary>
    /// Deterministic offline briefing. Posture blends the news pulse, the Fear &amp; Greed
    /// index and the book's P&L direction; the narrative and bullets are built from the
    /// same numbers so the card always has a useful read without an API key.
    /// </summary>
    public static InsightResult BuildOffline(BriefingInput i)
    {
        // Blend three signals into a -100..+100 risk posture score.
        var pnlBias = i.UnrealizedPnlUsd > 0 ? 10m : i.UnrealizedPnlUsd < 0 ? -10m : 0m;
        var fgBias = (i.FearGreed - 50) * 0.8m;          // greed → risk-on
        var score = (i.NewsPulseScore * 0.4m) + fgBias + pnlBias;

        var signal = score > 15m ? "RISK_ON"
                   : score < -15m ? "RISK_OFF"
                   : "NEUTRAL";

        var posture = signal switch
        {
            "RISK_ON"  => "a risk-on backdrop",
            "RISK_OFF" => "a risk-off backdrop",
            _          => "a mixed, range-bound backdrop"
        };

        var bookLine = i.OpenPositions == 0
            ? "You're flat — no open positions"
            : $"You hold {i.OpenPositions} position(s) with {Usd(i.UnrealizedPnlUsd)} unrealized";

        var fg = i.FearGreed >= 75 ? "extreme greed"
               : i.FearGreed >= 55 ? "greed"
               : i.FearGreed <= 25 ? "extreme fear"
               : i.FearGreed <= 45 ? "fear"
               : "neutral";

        var summary =
            $"{bookLine}. News reads {i.NewsPulseLabel.ToLowerInvariant()} and sentiment sits at {fg} " +
            $"(F&G {i.FearGreed}) — {posture}. " +
            (signal == "RISK_ON" ? "Conditions favor selective longs; keep sizing disciplined."
             : signal == "RISK_OFF" ? "Lean defensive: tighten stops and favor cash or hedges."
             : "No clear edge — be patient and trade only high-conviction setups.");

        var bullets = new List<string>
        {
            i.OpenPositions == 0
                ? "Book: flat"
                : $"Book: {i.OpenPositions} open, uPnL {Usd(i.UnrealizedPnlUsd)}" +
                  (string.IsNullOrWhiteSpace(i.BestSymbol) ? "" : $" (best {i.BestSymbol} {Usd(i.BestPnlUsd)}, worst {i.WorstSymbol} {Usd(i.WorstPnlUsd)})"),
            $"Market: news {i.NewsPulseLabel} (score {i.NewsPulseScore}), F&G {i.FearGreed}",
        };
        if (i.TopPicks.Count > 0)
            bullets.Add("Watchlist: " + string.Join(", ", i.TopPicks.Take(3)));

        return new InsightResult(summary, signal, bullets.ToArray(), "Heuristic (offline)", true);
    }

    private static string Usd(decimal v) => "$" + v.ToString("N2", CultureInfo.InvariantCulture);
}
