using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.2 — pure grid math ported from the desktop GridBot (level spacing + fee-aware
/// cycle profit, matching the BUG-10 commission fix). No exchange/DB — just the arithmetic.</summary>
public class ServerGridStrategyTests
{
    [Fact]
    public void Generate_levels_are_evenly_spaced_inclusive()
    {
        var levels = ServerGridStrategy.GenerateLevels(lower: 100m, upper: 200m, gridLevels: 4);

        Assert.Equal(new[] { 100m, 125m, 150m, 175m, 200m }, levels);
    }

    [Fact]
    public void Generate_levels_count_is_n_plus_one()
    {
        var levels = ServerGridStrategy.GenerateLevels(100m, 200m, 10);
        Assert.Equal(11, levels.Length);
    }

    [Theory]
    [InlineData(200, 100, 4)]   // upper <= lower
    [InlineData(100, 200, 0)]   // no levels
    public void Generate_levels_rejects_bad_range(decimal lower, decimal upper, int n)
    {
        Assert.Throws<ArgumentException>(() => ServerGridStrategy.GenerateLevels(lower, upper, n));
    }

    [Fact]
    public void Cycle_profit_subtracts_commission_both_sides()
    {
        // buy 100, sell 110, qty 1, fee 0.1%/side → commission (100+110)*1*0.001 = 0.21
        var profit = ServerGridStrategy.CycleProfit(buyPrice: 100m, sellPrice: 110m, qty: 1m, feePerSide: 0.001m);

        Assert.Equal(9.79m, profit);
    }

    [Fact]
    public void Cycle_profit_is_negative_when_spread_below_fees()
    {
        // 0.1% spread but 0.2% round-trip fees → net loss
        var profit = ServerGridStrategy.CycleProfit(1000m, 1001m, 1m, 0.001m);
        Assert.True(profit < 0m);
    }
}
