using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class AlertNlServiceTests
{
    [Theory]
    [InlineData("alert me when BTC goes above 70000", "BTCUSDT", AlertCondition.PriceAbove, 70000)]
    [InlineData("ping me if bitcoin breaks 75k", "BTCUSDT", AlertCondition.PriceAbove, 75000)]
    [InlineData("notify when ETH drops below 3000", "ETHUSDT", AlertCondition.PriceBelow, 3000)]
    [InlineData("tell me when solana falls under $120", "SOLUSDT", AlertCondition.PriceBelow, 120)]
    [InlineData("above $1,250,000 for BTC", "BTCUSDT", AlertCondition.PriceAbove, 1250000)]
    public void Offline_PriceLevels_Parse(string text, string sym, AlertCondition cond, double threshold)
    {
        var r = AlertNlService.ParseOffline(text);
        Assert.True(r.Success);
        Assert.Equal(sym, r.Symbol);
        Assert.Equal(cond, r.Condition);
        Assert.Equal((decimal)threshold, r.Threshold);
        Assert.True(r.IsFallback);
    }

    [Theory]
    [InlineData("tell me when ETH drops 10% in 1h", "ETHUSDT", AlertCondition.ChangePercent1hAbove, 10)]
    [InlineData("alert if SOL pumps 5% in 5m", "SOLUSDT", AlertCondition.ChangePercent5mAbove, 5)]
    [InlineData("notify when BTC moves 8 percent today", "BTCUSDT", AlertCondition.ChangePercent24hAbove, 8)]
    public void Offline_PercentMoves_Parse(string text, string sym, AlertCondition cond, double pct)
    {
        var r = AlertNlService.ParseOffline(text);
        Assert.True(r.Success);
        Assert.Equal(sym, r.Symbol);
        Assert.Equal(cond, r.Condition);
        Assert.Equal((decimal)pct, r.Threshold);
    }

    [Fact]
    public void Offline_VolumeSpike_Parses()
    {
        var r = AlertNlService.ParseOffline("alert on DOGE volume spike 4x");
        Assert.True(r.Success);
        Assert.Equal("DOGEUSDT", r.Symbol);
        Assert.Equal(AlertCondition.VolumeSpike, r.Condition);
        Assert.Equal(4m, r.Threshold);
    }

    [Fact]
    public void Offline_NoSymbol_Fails()
    {
        var r = AlertNlService.ParseOffline("alert me when it goes up a lot");
        Assert.False(r.Success);
    }

    [Fact]
    public void Offline_SymbolButNoNumber_FailsWithHint()
    {
        var r = AlertNlService.ParseOffline("watch BTC closely");
        Assert.False(r.Success);
        Assert.Contains("BTC", r.Explanation);
    }

    [Fact]
    public async System.Threading.Tasks.Task ParseAsync_NoKey_UsesOfflineParser()
    {
        var svc = new AlertNlService { ApiKey = "" };
        Assert.False(svc.UsesLiveModel);

        var r = await svc.ParseAsync("alert when ETH above 4000");
        Assert.True(r.Success);
        Assert.Equal("ETHUSDT", r.Symbol);
        Assert.Equal(AlertCondition.PriceAbove, r.Condition);
        Assert.Equal(4000m, r.Threshold);
    }
}
