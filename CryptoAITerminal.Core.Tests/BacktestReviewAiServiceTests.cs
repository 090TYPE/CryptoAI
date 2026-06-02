using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class BacktestReviewAiServiceTests
{
    private static readonly BacktestReviewAiService Svc = new() { ApiKey = "" }; // offline

    [Fact]
    public async Task FewTrades_FlaggedOverfit()
    {
        var m = new BacktestMetrics("RSI", NetReturnPct: 120m, BuyHoldReturnPct: 10m,
            WinRatePct: 90m, Trades: 4, MaxDrawdownPct: 5m, Sharpe: 4m, BestTradePct: 60m, WorstTradePct: -3m);
        var r = await Svc.ReviewAsync(m);
        Assert.Equal("OVERFIT", r.Verdict);
        Assert.True(r.IsFallback);
        Assert.NotEmpty(r.Risks);
    }

    [Fact]
    public async Task NoEdgeOverBuyHold_FlaggedWeak()
    {
        var m = new BacktestMetrics("MA Cross", NetReturnPct: 8m, BuyHoldReturnPct: 20m,
            WinRatePct: 48m, Trades: 60, MaxDrawdownPct: 18m, Sharpe: 0.6m, BestTradePct: 12m, WorstTradePct: -9m);
        var r = await Svc.ReviewAsync(m);
        Assert.Equal("WEAK", r.Verdict);
    }

    [Fact]
    public async Task StrongSampleWithEdge_FlaggedRobust()
    {
        var m = new BacktestMetrics("Breakout", NetReturnPct: 55m, BuyHoldReturnPct: 20m,
            WinRatePct: 58m, Trades: 80, MaxDrawdownPct: 14m, Sharpe: 1.6m, BestTradePct: 22m, WorstTradePct: -8m);
        var r = await Svc.ReviewAsync(m);
        Assert.Equal("ROBUST", r.Verdict);
    }
}
