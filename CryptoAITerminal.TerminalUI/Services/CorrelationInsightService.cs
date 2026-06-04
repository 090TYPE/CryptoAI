using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Turns a raw <see cref="CorrelationMatrix"/> into a plain-language read of how
/// diversified the book is: a DIVERSIFIED / BALANCED / CONCENTRATED posture, the
/// tightest clusters, and a concrete suggestion. Portfolio-aware — if a set of held
/// symbols is supplied, correlations among them are weighted more heavily.
///
/// Reuses the generic <see cref="MarketInsightAiService"/> (no new provider) and, like
/// every AI feature, ships a deterministic offline heuristic so it works keyless.
/// </summary>
public sealed class CorrelationInsightService
{
    private static readonly string[] Vocabulary = ["DIVERSIFIED", "BALANCED", "CONCENTRATED"];

    private readonly MarketInsightAiService _ai = new();

    public bool UsesLiveModel => _ai.UsesLiveModel;

    public void ConfigureAi(string? apiKey, string? model = null)
    {
        if (apiKey is not null) _ai.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) _ai.Model = model;
    }

    public Task<InsightResult> AnalyzeAsync(
        CorrelationMatrix matrix,
        IReadOnlyCollection<string>? heldSymbols = null,
        CancellationToken ct = default)
    {
        const string role =
            "You are a portfolio diversification analyst. Read the correlation summary and explain, in one " +
            "or two sentences, how diversified the book is and where the concentration risk sits. " +
            "This is not financial advice.";

        return _ai.InterpretAsync(role, BuildLines(matrix, heldSymbols), Vocabulary,
            () => BuildOffline(matrix, heldSymbols), ct);
    }

    /// <summary>
    /// Deterministic diversification read: posture from the average absolute pairwise
    /// correlation, escalated to CONCENTRATED if held symbols are tightly coupled.
    /// </summary>
    public static InsightResult BuildOffline(CorrelationMatrix matrix, IReadOnlyCollection<string>? heldSymbols = null)
    {
        var offDiagonal = matrix.Cells
            .Where(c => !string.Equals(c.SymbolA, c.SymbolB, StringComparison.OrdinalIgnoreCase) && c.SampleOverlap > 0)
            .ToList();

        if (offDiagonal.Count == 0)
            return new InsightResult(
                "Not enough overlapping price history yet to judge diversification.",
                "BALANCED", [], "Heuristic (offline)", true);

        var avgAbs = offDiagonal.Average(c => Math.Abs(c.Coefficient));
        var highPairs = matrix.GetHighCorrelationPairs(0.7m);

        // Held-symbol coupling: tightest correlation among the held set.
        var held = heldSymbols is { Count: > 0 }
            ? new HashSet<string>(heldSymbols, StringComparer.OrdinalIgnoreCase)
            : null;
        var heldPairs = held is null
            ? new List<CorrelationCell>()
            : offDiagonal
                .Where(c => held.Contains(c.SymbolA) && held.Contains(c.SymbolB))
                .OrderByDescending(c => Math.Abs(c.Coefficient))
                .ToList();
        var heldTightlyCoupled = heldPairs.Any(c => Math.Abs(c.Coefficient) >= 0.8m);

        var signal = heldTightlyCoupled || avgAbs >= 0.6m ? "CONCENTRATED"
                   : avgAbs <= 0.3m ? "DIVERSIFIED"
                   : "BALANCED";

        // Top distinct clusters (dedupe A/B vs B/A).
        var seen = new HashSet<string>();
        var topPairs = highPairs
            .Where(c => seen.Add(string.Join("|", new[] { c.SymbolA, c.SymbolB }.OrderBy(s => s))))
            .Take(3)
            .ToList();

        var summary = signal switch
        {
            "CONCENTRATED" => heldTightlyCoupled
                ? $"Your holdings move together — {heldPairs[0].SymbolA}/{heldPairs[0].SymbolB} at r={heldPairs[0].Coefficient:+0.00;-0.00}. " +
                  "Real diversification is limited; a single move hits several positions at once."
                : $"Highly correlated book (avg |r| {avgAbs:0.00}) with {highPairs.Count} tight pair(s) — limited diversification.",
            "DIVERSIFIED"  => $"Well diversified: average pairwise correlation is low (|r| {avgAbs:0.00}) with few tight clusters.",
            _              => $"Moderately diversified (avg |r| {avgAbs:0.00}); some clusters worth watching."
        };

        var bullets = new List<string> { $"Average pairwise |correlation|: {avgAbs:0.00}" };
        if (topPairs.Count > 0)
            bullets.Add("Tightest: " + string.Join(", ", topPairs.Select(c => $"{c.SymbolA}/{c.SymbolB} {c.Coefficient:+0.00;-0.00}")));
        if (heldPairs.Count > 0)
            bullets.Add($"Among your holdings: {heldPairs[0].SymbolA}/{heldPairs[0].SymbolB} {heldPairs[0].Coefficient:+0.00;-0.00}");
        bullets.Add(signal == "CONCENTRATED"
            ? "Consider trimming one of each correlated pair or adding an uncorrelated asset."
            : signal == "DIVERSIFIED"
                ? "Diversification looks healthy — maintain the spread."
                : "Keep an eye on the tighter clusters as you add risk.");

        return new InsightResult(summary, signal, bullets.ToArray(), "Heuristic (offline)", true);
    }

    private static IReadOnlyList<string> BuildLines(CorrelationMatrix matrix, IReadOnlyCollection<string>? heldSymbols)
    {
        var offDiagonal = matrix.Cells
            .Where(c => !string.Equals(c.SymbolA, c.SymbolB, StringComparison.OrdinalIgnoreCase) && c.SampleOverlap > 0)
            .ToList();

        var lines = new List<string>
        {
            $"Tracked symbols: {string.Join(", ", matrix.Symbols)}",
            offDiagonal.Count > 0
                ? $"Average pairwise |correlation|: {offDiagonal.Average(c => Math.Abs(c.Coefficient)):0.00}"
                : "Average pairwise |correlation|: n/a (insufficient overlap)",
        };

        var seen = new HashSet<string>();
        foreach (var c in matrix.GetHighCorrelationPairs(0.6m).Take(8))
            if (seen.Add(string.Join("|", new[] { c.SymbolA, c.SymbolB }.OrderBy(s => s))))
                lines.Add($"Pair {c.SymbolA}/{c.SymbolB}: r={c.Coefficient:+0.00;-0.00}");

        if (heldSymbols is { Count: > 0 })
            lines.Add("Currently held: " + string.Join(", ", heldSymbols));

        return lines;
    }
}
