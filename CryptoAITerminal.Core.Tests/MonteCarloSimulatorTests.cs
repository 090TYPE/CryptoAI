using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

file sealed class HoldOnlyStrategy : IStrategy
{
    public string Name => "Hold";
    public (string Signal, decimal Confidence) Analyze(MarketData data) => ("HOLD", 1.0m);
    public void Reset() { }
}

file sealed class AlternatingStrategy : IStrategy
{
    private int _i;
    public string Name => "Alt";

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        var sig = (_i++ % 2) == 0 ? "BUY" : "SELL";
        return (sig, 1.0m);
    }

    public void Reset() => _i = 0;
}

public class MonteCarloSimulatorTests
{
    private static DexOhlcvPoint Candle(int dayOffset, decimal price) => new()
    {
        Timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(dayOffset),
        Open = price, High = price, Low = price, Close = price, Volume = 1_000m
    };

    [Fact]
    public void Empty_Candles_Returns_NotReady()
    {
        var result = MonteCarloSimulator.Run(() => new HoldOnlyStrategy(), [], runs: 10, subsampleFraction: 0.5);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Below_Minimum_Candles_Returns_NotReady()
    {
        var candles = Enumerable.Range(0, 5).Select(i => Candle(i, 100m)).ToList();
        var result  = MonteCarloSimulator.Run(() => new HoldOnlyStrategy(), candles, runs: 10, subsampleFraction: 0.5);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Hold_Strategy_Produces_No_Runs()
    {
        // Стратегия никогда не торгует → BacktestEngine.Run возвращает IsReady=false → 0 completed runs
        var candles = Enumerable.Range(0, 50).Select(i => Candle(i, 100m + i)).ToList();
        var result  = MonteCarloSimulator.Run(() => new HoldOnlyStrategy(), candles, runs: 25, subsampleFraction: 0.7);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Same_Seed_Produces_Same_Distribution()
    {
        var candles = Enumerable.Range(0, 60).Select(i => Candle(i, 100m + (i % 7) * 5m)).ToList();

        var a = MonteCarloSimulator.Run(() => new AlternatingStrategy(), candles, runs: 30, subsampleFraction: 0.7, seed: 42);
        var b = MonteCarloSimulator.Run(() => new AlternatingStrategy(), candles, runs: 30, subsampleFraction: 0.7, seed: 42);

        Assert.True(a.IsReady);
        Assert.True(b.IsReady);
        Assert.Equal(a.CompletedRuns,        b.CompletedRuns);
        Assert.Equal(a.MeanReturnPercent,    b.MeanReturnPercent);
        Assert.Equal(a.MedianReturnPercent,  b.MedianReturnPercent);
        Assert.Equal(a.Percentile5,          b.Percentile5);
        Assert.Equal(a.Percentile95,         b.Percentile95);
    }

    [Fact]
    public void Confidence_Interval_Is_Ordered()
    {
        var candles = Enumerable.Range(0, 80).Select(i => Candle(i, 100m + (i % 5) * 4m)).ToList();
        var result  = MonteCarloSimulator.Run(() => new AlternatingStrategy(), candles, runs: 50, subsampleFraction: 0.6, seed: 1);

        if (!result.IsReady) return;

        Assert.True(result.WorstReturnPercent <= result.Percentile5);
        Assert.True(result.Percentile5        <= result.MedianReturnPercent);
        Assert.True(result.MedianReturnPercent <= result.Percentile95);
        Assert.True(result.Percentile95        <= result.BestReturnPercent);
    }
}
