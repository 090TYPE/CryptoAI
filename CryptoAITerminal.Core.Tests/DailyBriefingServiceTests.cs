using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class DailyBriefingServiceTests
{
    private static BriefingInput Input(
        int openPositions = 0,
        decimal upnl = 0m,
        int newsPulse = 0,
        string newsLabel = "Neutral",
        int fearGreed = 50,
        IReadOnlyList<string>? picks = null)
        => new(openPositions, upnl, null, 0m, null, 0m, newsPulse, newsLabel, fearGreed, picks ?? Array.Empty<string>());

    [Fact]
    public void Offline_BullishNewsAndGreed_IsRiskOn()
    {
        var r = DailyBriefingService.BuildOffline(
            Input(newsPulse: 60, newsLabel: "Bullish", fearGreed: 80, upnl: 250m));

        Assert.Equal("RISK_ON", r.Signal);
        Assert.True(r.IsFallback);
        Assert.Contains("greed", r.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(r.Bullets);
    }

    [Fact]
    public void Offline_BearishNewsAndFear_IsRiskOff()
    {
        var r = DailyBriefingService.BuildOffline(
            Input(newsPulse: -70, newsLabel: "Bearish", fearGreed: 15, upnl: -120m));

        Assert.Equal("RISK_OFF", r.Signal);
        Assert.Contains("defensive", r.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Offline_MixedSignals_IsNeutral()
    {
        var r = DailyBriefingService.BuildOffline(
            Input(newsPulse: 0, newsLabel: "Neutral", fearGreed: 50));

        Assert.Equal("NEUTRAL", r.Signal);
    }

    [Fact]
    public void Offline_FlatBook_SaysFlat()
    {
        var r = DailyBriefingService.BuildOffline(Input(openPositions: 0));
        Assert.Contains("flat", r.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Offline_IncludesTopPicksBullet()
    {
        var r = DailyBriefingService.BuildOffline(
            Input(newsPulse: 20, picks: new[] { "BTCUSDT [LONG 82]", "ETHUSDT [WATCH 60]" }));

        Assert.Contains(r.Bullets, b => b.Contains("BTCUSDT"));
    }

    [Fact]
    public async Task BuildAsync_NoKey_FallsBackToOfflineHeuristic()
    {
        var svc = new DailyBriefingService();
        svc.ConfigureAi("", null);
        Assert.False(svc.UsesLiveModel);

        var r = await svc.BuildAsync(Input(newsPulse: 60, newsLabel: "Bullish", fearGreed: 80));

        Assert.Equal("RISK_ON", r.Signal);
        Assert.True(r.IsFallback);
    }
}
