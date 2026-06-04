using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Coaches the trader from their closed-trade journal. Computes aggregate stats
/// locally (raw trades never leave the machine) and asks Claude to interpret them;
/// falls back to a deterministic offline review built from the same aggregates.
/// </summary>
public sealed class TradeJournalCoachAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<JournalReview> ReviewAsync(IReadOnlyList<TradeRecord> trades, CancellationToken ct = default)
    {
        var closed = (trades ?? []).Where(t => t.ClosedAtUtc > t.OpenedAtUtc).ToList();
        if (closed.Count < 3)
            return new JournalReview(
                "Not enough closed trades yet for a meaningful review (need at least 3).",
                [], [], ["Keep journaling — patterns emerge after ~20 trades."], "Heuristic (offline)", true);

        var agg = Aggregate(closed);

        if (UsesLiveModel)
        {
            try
            {
                var provider = new TradeJournalCoachAiProvider(ApiKey, Model);
                var review = await provider.ReviewAsync(agg.Lines, ct).ConfigureAwait(false);
                if (review is not null) return review;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        return BuildOffline(agg);
    }

    // ── Aggregation ────────────────────────────────────────────────────────────

    private sealed record Agg(
        IReadOnlyList<JournalStatLine> Lines,
        int Total, decimal WinRate, decimal ProfitFactor,
        decimal AvgWinPct, decimal AvgLossPct,
        decimal LongWinRate, decimal ShortWinRate, int LongCount, int ShortCount,
        decimal AvgHoldWinMin, decimal AvgHoldLossMin,
        int MaxLossStreak, string? WorstBucketLabel, decimal WorstBucketPnl);

    private static Agg Aggregate(List<TradeRecord> t)
    {
        var wins = t.Where(x => x.IsWin).ToList();
        var losses = t.Where(x => !x.IsWin).ToList();
        var winRate = 100m * wins.Count / t.Count;
        var grossWin = wins.Sum(x => x.PnlUsd);
        var grossLoss = Math.Abs(losses.Sum(x => x.PnlUsd));
        var pf = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? 99m : 0m);
        var avgWinPct = wins.Count > 0 ? wins.Average(x => x.PnlPercent) : 0m;
        var avgLossPct = losses.Count > 0 ? losses.Average(x => x.PnlPercent) : 0m;

        var longs = t.Where(x => x.Direction == TradeDirection.Long).ToList();
        var shorts = t.Where(x => x.Direction == TradeDirection.Short).ToList();
        var longWr = longs.Count > 0 ? 100m * longs.Count(x => x.IsWin) / longs.Count : 0m;
        var shortWr = shorts.Count > 0 ? 100m * shorts.Count(x => x.IsWin) / shorts.Count : 0m;

        decimal HoldMin(IEnumerable<TradeRecord> xs)
        {
            var arr = xs.ToList();
            return arr.Count == 0 ? 0m : (decimal)arr.Average(x => (x.ClosedAtUtc - x.OpenedAtUtc).TotalMinutes);
        }

        // Longest losing streak (chronological).
        var chrono = t.OrderBy(x => x.ClosedAtUtc).ToList();
        int streak = 0, maxStreak = 0;
        foreach (var x in chrono) { streak = x.IsWin ? 0 : streak + 1; maxStreak = Math.Max(maxStreak, streak); }

        // Worst bucket by source/bot.
        var worst = t.GroupBy(x => x.BotName ?? x.SourceLabel)
            .Select(g => (Label: g.Key, Pnl: g.Sum(x => x.PnlUsd)))
            .OrderBy(g => g.Pnl).FirstOrDefault();

        var lines = new List<JournalStatLine>
        {
            new("Total closed trades", t.Count.ToString()),
            new("Win rate", $"{winRate:0.0}%"),
            new("Profit factor", $"{pf:0.00}"),
            new("Avg win", $"{avgWinPct:+0.0}%"),
            new("Avg loss", $"{avgLossPct:0.0}%"),
            new("Net P&L USD", $"{t.Sum(x => x.PnlUsd):+0.00;-0.00}"),
            new("Long trades", $"{longs.Count} ({longWr:0}% win)"),
            new("Short trades", $"{shorts.Count} ({shortWr:0}% win)"),
            new("Avg hold winners", $"{HoldMin(wins):0} min"),
            new("Avg hold losers", $"{HoldMin(losses):0} min"),
            new("Longest losing streak", maxStreak.ToString()),
        };
        if (!string.IsNullOrWhiteSpace(worst.Label))
            lines.Add(new("Worst source/bot", $"{worst.Label} ({worst.Pnl:+0.00;-0.00} USD)"));

        return new Agg(lines, t.Count, winRate, pf, avgWinPct, avgLossPct,
            longWr, shortWr, longs.Count, shorts.Count, HoldMin(wins), HoldMin(losses),
            maxStreak, worst.Label, worst.Pnl);
    }

    private static JournalReview BuildOffline(Agg a)
    {
        var strengths = new List<string>();
        var leaks = new List<string>();
        var suggestions = new List<string>();

        if (a.WinRate >= 50m) strengths.Add($"Solid {a.WinRate:0}% win rate across {a.Total} trades.");
        if (a.ProfitFactor >= 1.5m) strengths.Add($"Healthy profit factor of {a.ProfitFactor:0.0}.");
        if (Math.Abs(a.AvgWinPct) > Math.Abs(a.AvgLossPct) * 1.2m)
            strengths.Add("Winners are larger than losers — good risk/reward.");

        if (a.ProfitFactor < 1m) leaks.Add($"Profit factor below 1 ({a.ProfitFactor:0.0}) — losing money overall.");
        if (Math.Abs(a.AvgLossPct) > Math.Abs(a.AvgWinPct))
            leaks.Add($"Average loss ({a.AvgLossPct:0.0}%) is bigger than average win ({a.AvgWinPct:+0.0}%) — cutting winners early or letting losers run.");
        if (a.AvgHoldLossMin > a.AvgHoldWinMin * 1.3m && a.AvgHoldWinMin > 0)
            leaks.Add($"You hold losers ~{a.AvgHoldLossMin / Math.Max(1m, a.AvgHoldWinMin):0.0}x longer than winners — classic loss-aversion.");
        if (a.LongCount >= 5 && a.ShortCount >= 5 && Math.Abs(a.LongWinRate - a.ShortWinRate) >= 20m)
        {
            var weak = a.LongWinRate < a.ShortWinRate ? "long" : "short";
            leaks.Add($"Your {weak} trades win far less often ({Math.Min(a.LongWinRate, a.ShortWinRate):0}% vs {Math.Max(a.LongWinRate, a.ShortWinRate):0}%).");
        }
        if (a.MaxLossStreak >= 5) leaks.Add($"A {a.MaxLossStreak}-trade losing streak suggests trading through bad conditions.");
        if (a.WorstBucketPnl < 0 && a.WorstBucketLabel is not null)
            leaks.Add($"\"{a.WorstBucketLabel}\" is your biggest drain ({a.WorstBucketPnl:+0.00;-0.00} USD).");

        if (Math.Abs(a.AvgLossPct) > Math.Abs(a.AvgWinPct))
            suggestions.Add("Set a fixed stop and a take-profit at ≥1.5x the stop to flip your risk/reward.");
        if (a.AvgHoldLossMin > a.AvgHoldWinMin * 1.3m && a.AvgHoldWinMin > 0)
            suggestions.Add("Use a hard time-stop on losers so they don't outlast your winners.");
        if (a.MaxLossStreak >= 5)
            suggestions.Add("Add a daily-loss circuit breaker to stop after N consecutive losers.");
        if (suggestions.Count == 0)
            suggestions.Add("Keep the current process; scale size gradually as the edge persists.");

        var net = a.WinRate >= 50m && a.ProfitFactor >= 1m ? "profitable" : "unprofitable";
        var summary = $"Over {a.Total} trades you are {net}: {a.WinRate:0}% win rate, profit factor {a.ProfitFactor:0.0}, " +
                      $"avg win {a.AvgWinPct:+0.0}% vs avg loss {a.AvgLossPct:0.0}%.";

        return new JournalReview(summary, strengths.ToArray(), leaks.ToArray(), suggestions.ToArray(), "Heuristic (offline)", true);
    }
}
