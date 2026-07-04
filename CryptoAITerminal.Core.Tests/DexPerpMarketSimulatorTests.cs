using System.Linq;
using CryptoAITerminal.Core.Trading;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class DexPerpMarketSimulatorTests
{
    [Fact]
    public void Mark_StaysPositive_AndWithinBand_OverManySteps()
    {
        var sim = new DexPerpMarketSimulator(anchorPrice: 100m, bandPercent: 0.10m, seed: 7);

        for (var i = 0; i < 2_000; i++)
        {
            var mark = sim.NextMark();
            Assert.True(mark > 0m);
            Assert.InRange(mark, 90m, 110m);
        }
    }

    [Fact]
    public void FundingRate_IsBounded()
    {
        var sim = new DexPerpMarketSimulator(anchorPrice: 2500m, seed: 3);
        for (var i = 0; i < 500; i++)
        {
            sim.NextMark();
            Assert.InRange(sim.FundingRate, -0.01m, 0.01m);
        }
    }

    [Fact]
    public void Deterministic_ForSameSeed()
    {
        var a = new DexPerpMarketSimulator(100m, seed: 42);
        var b = new DexPerpMarketSimulator(100m, seed: 42);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextMark(), b.NextMark());
        }
    }

    [Fact]
    public void OrderBook_HasMonotonicCumulative_AndBracketsMark()
    {
        var sim = new DexPerpMarketSimulator(100m, seed: 11);
        sim.NextMark();
        var (asks, bids) = sim.BuildOrderBook(depth: 10);

        Assert.Equal(10, asks.Count);
        Assert.Equal(10, bids.Count);

        // asks ascend above mark, bids descend below it
        Assert.All(asks, l => Assert.True(l.Price > sim.Mark));
        Assert.All(bids, l => Assert.True(l.Price < sim.Mark));
        Assert.True(asks.First().Price < asks.Last().Price);
        Assert.True(bids.First().Price > bids.Last().Price);

        // cumulative totals strictly increase and sizes are positive
        for (var i = 1; i < asks.Count; i++)
        {
            Assert.True(asks[i].Cumulative > asks[i - 1].Cumulative);
            Assert.True(asks[i].Size > 0m);
        }
    }
}
