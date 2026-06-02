using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class MarketRankingAiServiceTests
{
    private static ScanResult Row(string sym, decimal chg, decimal vol, decimal rsi, decimal act = 0, bool hot = false) =>
        new() { Symbol = sym, Exchange = "Binance", LastPrice = 100m, ChangePct24h = chg, Volume24hUsd = vol, Rsi14 = rsi, ActivityScore = act, IsHot = hot };

    [Fact]
    public async Task Offline_RanksByStrength_AndCapsTopN()
    {
        var svc = new MarketRankingAiService { ApiKey = "" }; // force offline
        var results = new List<ScanResult>
        {
            Row("AAAUSDT", chg: 0.2m, vol: 1000m, rsi: 50m),       // dull
            Row("BBBUSDT", chg: 12m, vol: 5_000_000m, rsi: 78m, act: 40, hot: true), // strong
            Row("CCCUSDT", chg: -9m, vol: 3_000_000m, rsi: 22m),  // strong (oversold dump)
            Row("DDDUSDT", chg: 1m, vol: 2000m, rsi: 55m),
        };

        var ranked = await svc.RankAsync(results, topN: 2);

        Assert.True(ranked.IsFallback);
        Assert.Equal(2, ranked.Opportunities.Count);
        // The two strong movers must outrank the dull ones.
        var top2 = ranked.Opportunities.Select(o => o.Symbol).ToHashSet();
        Assert.Contains("BBBUSDT", top2);
        Assert.Contains("CCCUSDT", top2);
    }

    [Fact]
    public async Task Offline_AssignsDirectionalBias()
    {
        var svc = new MarketRankingAiService { ApiKey = "" };
        var results = new List<ScanResult>
        {
            Row("UPUSDT", chg: 8m, vol: 2_000_000m, rsi: 33m),    // up + oversold-ish → LONG
            Row("DNUSDT", chg: -8m, vol: 2_000_000m, rsi: 72m),   // down + overbought → SHORT
        };

        var ranked = await svc.RankAsync(results, topN: 5);

        Assert.Equal("LONG",  ranked.Opportunities.Single(o => o.Symbol == "UPUSDT").Bias);
        Assert.Equal("SHORT", ranked.Opportunities.Single(o => o.Symbol == "DNUSDT").Bias);
    }

    [Fact]
    public async Task EmptyInput_ReturnsEmptyFallback()
    {
        var svc = new MarketRankingAiService { ApiKey = "" };
        var ranked = await svc.RankAsync(new List<ScanResult>(), topN: 5);
        Assert.True(ranked.IsFallback);
        Assert.Empty(ranked.Opportunities);
    }
}
