using System;
using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class PreTradeRiskServiceTests
{
    private static PreTradeRiskInput Input(
        decimal orderUsd = 50m,
        decimal equity = 0m,
        decimal existingExposure = 0m,
        decimal sameSymbol = 0m,
        decimal maxExposure = 500m,
        int leverage = 1,
        decimal dailyPnl = 0m,
        decimal maxDailyLoss = 100m,
        string symbol = "BTCUSDT",
        string side = "buy")
        => new(symbol, side, orderUsd, equity, existingExposure, sameSymbol, maxExposure, leverage, dailyPnl, maxDailyLoss);

    [Fact]
    public void SmallSafeOrder_IsApproved()
    {
        var r = PreTradeRiskService.Evaluate(Input(orderUsd: 30m, maxExposure: 1000m));
        Assert.Equal("APPROVE", r.Verdict);
        Assert.True(r.Score < 40);
        Assert.True(r.IsFallback);
    }

    [Fact]
    public void ProjectedExposureOverCap_IsBlocked()
    {
        // existing 480 + order 100 = 580 > 500 cap → hard block regardless of score.
        var r = PreTradeRiskService.Evaluate(Input(orderUsd: 100m, existingExposure: 480m, maxExposure: 500m));
        Assert.Equal("BLOCK", r.Verdict);
        Assert.Contains(r.Reasons, x => x.Contains("EXCEEDS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DailyLossLimitHit_IsBlocked()
    {
        var r = PreTradeRiskService.Evaluate(Input(orderUsd: 20m, dailyPnl: -120m, maxDailyLoss: 100m));
        Assert.Equal("BLOCK", r.Verdict);
        Assert.Contains(r.Reasons, x => x.Contains("loss", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HeavyConcentration_FlaggedAndRaisesScore()
    {
        // Adding 200 to a symbol already holding 300, on a small book → concentrated.
        var concentrated = PreTradeRiskService.Evaluate(
            Input(orderUsd: 200m, existingExposure: 300m, sameSymbol: 300m, maxExposure: 2000m));
        var diversified = PreTradeRiskService.Evaluate(
            Input(orderUsd: 200m, existingExposure: 300m, sameSymbol: 0m, maxExposure: 2000m));

        Assert.True(concentrated.Score > diversified.Score);
        Assert.Contains(concentrated.Reasons, x => x.Contains("concentrated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HighLeverage_AddsRiskFlag()
    {
        var r = PreTradeRiskService.Evaluate(Input(orderUsd: 50m, leverage: 20, maxExposure: 1000m));
        Assert.Contains(r.Reasons, x => x.Contains("leverage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateAsync_NoKey_ReturnsDeterministicOffline()
    {
        var svc = new PreTradeRiskService();
        svc.ConfigureAi("", null);
        Assert.False(svc.UsesLiveModel);

        var input = Input(orderUsd: 100m, existingExposure: 480m, maxExposure: 500m);
        var viaAsync = await svc.EvaluateAsync(input);
        var viaPure = PreTradeRiskService.Evaluate(input);

        Assert.True(viaAsync.IsFallback);
        Assert.Equal(viaPure.Verdict, viaAsync.Verdict);
        Assert.Equal(viaPure.Score, viaAsync.Score);
    }
}
