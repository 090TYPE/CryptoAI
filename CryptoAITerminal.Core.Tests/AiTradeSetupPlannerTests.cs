using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Core.Trading;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class AiTradeSetupPlannerTests
{
    private static List<DexOhlcvPoint> Series(IEnumerable<decimal> closes, decimal range = 2m)
    {
        var t = DateTime.UtcNow.AddHours(-100);
        return closes.Select(c => new DexOhlcvPoint
        {
            Timestamp = t = t.AddHours(1),
            Open = c,
            High = c + range,
            Low = c - range,
            Close = c,
            Volume = 1_000m,
        }).ToList();
    }

    [Fact]
    public void Returns_Null_When_Not_Enough_Candles()
    {
        Assert.Null(AiTradeSetupPlanner.Build(Series(new[] { 100m, 101m }), 101m, TradeBiasMode.Auto, TradeRiskProfile.Balanced, TradeHorizon.Swing));
    }

    [Fact]
    public void Uptrend_Auto_Produces_Long_With_Stop_Below_And_Target_Above()
    {
        var candles = Series(Enumerable.Range(0, 30).Select(i => 100m + i)); // rising
        var setup = AiTradeSetupPlanner.Build(candles, 130m, TradeBiasMode.Auto, TradeRiskProfile.Balanced, TradeHorizon.Swing);

        Assert.NotNull(setup);
        Assert.Equal("LONG", setup!.Bias);
        Assert.True(setup.StopLoss < setup.Entry);
        Assert.True(setup.TakeProfit > setup.Entry);
    }

    [Fact]
    public void Downtrend_Auto_Produces_Short_With_Stop_Above_And_Target_Below()
    {
        var candles = Series(Enumerable.Range(0, 30).Select(i => 200m - i)); // falling
        var setup = AiTradeSetupPlanner.Build(candles, 170m, TradeBiasMode.Auto, TradeRiskProfile.Balanced, TradeHorizon.Swing);

        Assert.NotNull(setup);
        Assert.Equal("SHORT", setup!.Bias);
        Assert.True(setup.StopLoss > setup.Entry);
        Assert.True(setup.TakeProfit < setup.Entry);
    }

    [Fact]
    public void Explicit_Short_Bias_Overrides_Uptrend()
    {
        var candles = Series(Enumerable.Range(0, 30).Select(i => 100m + i));
        var setup = AiTradeSetupPlanner.Build(candles, 130m, TradeBiasMode.Short, TradeRiskProfile.Balanced, TradeHorizon.Swing);
        Assert.Equal("SHORT", setup!.Bias);
    }

    [Fact]
    public void Intent_Keyword_Steers_Bias_In_Auto()
    {
        var candles = Series(Enumerable.Range(0, 30).Select(i => 100m + i)); // uptrend
        var setup = AiTradeSetupPlanner.Build(candles, 130m, TradeBiasMode.Auto, TradeRiskProfile.Balanced, TradeHorizon.Swing, intent: "я хочу шорт");
        Assert.Equal("SHORT", setup!.Bias);
    }

    [Fact]
    public void Aggressive_Uses_More_Leverage_And_Size_Than_Conservative()
    {
        var candles = Series(Enumerable.Range(0, 30).Select(i => 100m + i));
        var aggr = AiTradeSetupPlanner.Build(candles, 130m, TradeBiasMode.Long, TradeRiskProfile.Aggressive, TradeHorizon.Swing)!;
        var cons = AiTradeSetupPlanner.Build(candles, 130m, TradeBiasMode.Long, TradeRiskProfile.Conservative, TradeHorizon.Swing)!;

        Assert.True(aggr.Leverage > cons.Leverage);
        Assert.True(aggr.SizePercent > cons.SizePercent);
        Assert.True(aggr.RiskReward > cons.RiskReward);
    }

    [Fact]
    public void RiskReward_Matches_Distances()
    {
        var candles = Series(Enumerable.Range(0, 30).Select(i => 100m + i));
        var s = AiTradeSetupPlanner.Build(candles, 130m, TradeBiasMode.Long, TradeRiskProfile.Balanced, TradeHorizon.Swing)!;

        var risk = s.Entry - s.StopLoss;
        var reward = s.TakeProfit - s.Entry;
        Assert.True(risk > 0m);
        Assert.Equal((double)s.RiskReward, (double)(reward / risk), 1); // ~2.2R within tolerance
    }

    [Fact]
    public void RiskBased_Sizing_Shrinks_When_Stop_Is_Wider()
    {
        var candles = Series(Enumerable.Range(0, 30).Select(i => 100m + i));
        var scalp = AiTradeSetupPlanner.Build(candles, 130m, TradeBiasMode.Long, TradeRiskProfile.Balanced, TradeHorizon.Scalp, riskPercentPerTrade: 1m)!;
        var position = AiTradeSetupPlanner.Build(candles, 130m, TradeBiasMode.Long, TradeRiskProfile.Balanced, TradeHorizon.Position, riskPercentPerTrade: 1m)!;

        // A wider stop (Position horizon) with the same 1% risk budget → smaller size.
        Assert.True(position.SizePercent < scalp.SizePercent);
        Assert.InRange(scalp.SizePercent, 1m, 100m);
    }

    [Fact]
    public void Confluence_Is_High_In_Clean_Uptrend_And_Bounded()
    {
        var candles = Series(Enumerable.Range(0, 40).Select(i => 100m + i)); // strong, clean uptrend
        var s = AiTradeSetupPlanner.Build(candles, 140m, TradeBiasMode.Long, TradeRiskProfile.Balanced, TradeHorizon.Swing)!;
        Assert.InRange(s.Confluence, 0, 100);
        Assert.Equal(100, s.Confluence); // long bias agrees with fast>mid>slow
    }

    [Fact]
    public void Confidence_Is_Within_Bounds()
    {
        var candles = Series(Enumerable.Range(0, 30).Select(i => 100m + (decimal)Math.Sin(i) * 3m));
        var s = AiTradeSetupPlanner.Build(candles, 100m, TradeBiasMode.Auto, TradeRiskProfile.Balanced, TradeHorizon.Scalp)!;
        Assert.InRange(s.Confidence, 38, 93);
    }
}
