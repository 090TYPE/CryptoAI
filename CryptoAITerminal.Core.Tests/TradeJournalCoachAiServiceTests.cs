using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class TradeJournalCoachAiServiceTests
{
    private static readonly TradeJournalCoachAiService Svc = new() { ApiKey = "" }; // offline

    private static TradeRecord T(decimal pnlUsd, decimal pnlPct, TradeDirection dir, int holdMin, DateTime closed, string? bot = null) =>
        new()
        {
            Symbol = "BTCUSDT", Direction = dir, PnlUsd = pnlUsd, PnlPercent = pnlPct, BotName = bot,
            OpenedAtUtc = closed.AddMinutes(-holdMin), ClosedAtUtc = closed, EntryPrice = 100, ExitPrice = 101, Quantity = 1
        };

    [Fact]
    public async Task TooFewTrades_ReturnsGuidance()
    {
        var r = await Svc.ReviewAsync(new List<TradeRecord> { T(5, 1, TradeDirection.Long, 10, DateTime.UtcNow) });
        Assert.True(r.IsFallback);
        Assert.Contains("at least 3", r.Summary);
    }

    [Fact]
    public async Task DetectsLossAversion_HoldingLosersLonger()
    {
        var now = DateTime.UtcNow;
        var trades = new List<TradeRecord>
        {
            T(+20, +2, TradeDirection.Long, 10, now.AddMinutes(-50)),   // win, short hold
            T(+15, +1.5m, TradeDirection.Long, 12, now.AddMinutes(-40)),
            T(-30, -3, TradeDirection.Long, 120, now.AddMinutes(-30)),  // loss, long hold
            T(-28, -2.8m, TradeDirection.Long, 140, now.AddMinutes(-20)),
            T(-25, -2.5m, TradeDirection.Long, 130, now.AddMinutes(-10)),
        };

        var r = await Svc.ReviewAsync(trades);

        Assert.True(r.IsFallback);
        // Holding losers far longer than winners should be surfaced as a leak.
        Assert.Contains(r.Leaks, l => l.Contains("longer", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(r.Suggestions);
    }

    [Fact]
    public async Task ProfitableBook_ListsStrengths()
    {
        var now = DateTime.UtcNow;
        var trades = Enumerable.Range(0, 10)
            .Select(i => T(i < 7 ? +30 : -10, i < 7 ? +3 : -1, TradeDirection.Long, 20, now.AddMinutes(-i * 5)))
            .ToList();

        var r = await Svc.ReviewAsync(trades);

        Assert.True(r.IsFallback);
        Assert.NotEmpty(r.Strengths);
        Assert.Contains("profitable", r.Summary);
    }
}
