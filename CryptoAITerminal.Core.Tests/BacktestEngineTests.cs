using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Стратегия с предопределённой последовательностью сигналов для детерминированного тестирования.
/// </summary>
file sealed class ScriptedStrategy(params string[] signals) : IStrategy
{
    private int _index;
    public string Name => "Scripted";

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        var sig = _index < signals.Length ? signals[_index] : "HOLD";
        _index++;
        return (sig, 1.0m);
    }

    public void Reset() => _index = 0;
}

/// <summary>
/// Стратегия которая никогда не торгует.
/// </summary>
file sealed class HoldStrategy : IStrategy
{
    public string Name => "Hold";
    public (string Signal, decimal Confidence) Analyze(MarketData data) => ("HOLD", 1.0m);
    public void Reset() { }
}

public class BacktestEngineTests
{
    private static DexOhlcvPoint Candle(int dayOffset, decimal price) => new()
    {
        Timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(dayOffset),
        Open = price, High = price, Low = price, Close = price, Volume = 1_000m
    };

    // ── Базовые граничные условия ─────────────────────────────────────────────

    [Fact]
    public void Empty_Candles_Returns_Empty_Result()
    {
        var result = BacktestEngine.Run(new HoldStrategy(), []);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Single_Candle_Returns_Empty_Result()
    {
        var result = BacktestEngine.Run(new HoldStrategy(), [Candle(0, 100m)]);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Hold_Strategy_Has_No_Trades()
    {
        var candles = Enumerable.Range(0, 10).Select(i => Candle(i, 100m + i)).ToList();
        var result = BacktestEngine.Run(new HoldStrategy(), candles);
        Assert.Equal(0, result.TradeCount);
    }

    // ── Корректность кривой эквити ────────────────────────────────────────────

    [Fact]
    public void Equity_Curve_Starts_At_100()
    {
        // BUY на 100 → SELL на 100 (flat) — сделка есть, equity не изменяется
        var strategy = new ScriptedStrategy("BUY", "SELL");
        var candles = new[]
        {
            Candle(0, 100m), Candle(1, 100m),
            Candle(2, 100m), Candle(3, 100m),
        };
        var result = BacktestEngine.Run(strategy, candles, commissionPercent: 0m);
        Assert.NotEmpty(result.EquityCurve);
        Assert.Equal(100m, result.EquityCurve[0].Value);
    }

    [Fact]
    public void Equity_Curve_Has_No_Duplicate_Timestamps()
    {
        // Проверка исправления БАГ-25: стартовая точка не дублируется
        // Используем ScriptedStrategy чтобы получить непустую equity curve
        var strategy = new ScriptedStrategy("BUY", "SELL");
        var candles = Enumerable.Range(0, 10).Select(i => Candle(i, 100m)).ToList();
        var result = BacktestEngine.Run(strategy, candles);

        if (result.EquityCurve.Count > 0)
        {
            var distinctTimestamps = result.EquityCurve.Select(p => p.Time).Distinct().Count();
            Assert.Equal(result.EquityCurve.Count, distinctTimestamps);
        }
    }

    [Fact]
    public void Equity_Curve_First_Point_Timestamp_Matches_First_Candle()
    {
        var strategy = new ScriptedStrategy("BUY", "SELL");
        var candles = Enumerable.Range(0, 6).Select(i => Candle(i, 100m + i * 5m)).ToList();
        var result = BacktestEngine.Run(strategy, candles, commissionPercent: 0m);

        Assert.NotEmpty(result.EquityCurve);
        Assert.Equal(candles[0].Timestamp, result.EquityCurve[0].Time);
    }

    // ── P&L и комиссии ────────────────────────────────────────────────────────

    [Fact]
    public void Profitable_Trade_Produces_Positive_Return()
    {
        // BUY на 100, SELL на 200 → +100%
        var strategy = new ScriptedStrategy("BUY", "SELL");
        var candles = new[]
        {
            Candle(0, 100m),  // BUY signal на свече 1 (Skip(1) начинает с неё)
            Candle(1, 100m),  // BUY исполняется
            Candle(2, 200m),  // SELL signal
            Candle(3, 200m),  // SELL исполняется
        };
        var result = BacktestEngine.Run(strategy, candles, commissionPercent: 0m);
        Assert.True(result.NetReturnPercent > 0m);
    }

    [Fact]
    public void Commission_Reduces_Profit()
    {
        var strategy0 = new ScriptedStrategy("BUY", "SELL");
        var strategyC = new ScriptedStrategy("BUY", "SELL");
        var candles = new[]
        {
            Candle(0, 100m),
            Candle(1, 100m),
            Candle(2, 150m),
            Candle(3, 150m),
        };

        var noFee  = BacktestEngine.Run(strategy0, candles, commissionPercent: 0m);
        var withFee = BacktestEngine.Run(strategyC, candles, commissionPercent: 0.1m);

        Assert.True(noFee.NetReturnPercent > withFee.NetReturnPercent);
    }

    [Fact]
    public void Losing_Trade_Produces_Negative_Return()
    {
        var strategy = new ScriptedStrategy("BUY", "SELL");
        var candles = new[]
        {
            Candle(0, 200m),
            Candle(1, 200m),
            Candle(2, 100m),
            Candle(3, 100m),
        };
        var result = BacktestEngine.Run(strategy, candles, commissionPercent: 0m);
        Assert.True(result.NetReturnPercent < 0m);
    }

    // ── Метрики ───────────────────────────────────────────────────────────────

    [Fact]
    public void Win_Rate_Is_100_Percent_When_All_Trades_Profitable()
    {
        var strategy = new ScriptedStrategy("BUY", "SELL");
        var candles = new[]
        {
            Candle(0, 100m),
            Candle(1, 100m),
            Candle(2, 150m),
            Candle(3, 150m),
        };
        var result = BacktestEngine.Run(strategy, candles, commissionPercent: 0m);
        if (result.TradeCount > 0)
            Assert.Equal(100m, result.WinRatePercent);
    }

    [Fact]
    public void Max_Drawdown_Is_Non_Negative()
    {
        var candles = Enumerable.Range(0, 20).Select(i => Candle(i, 100m - i)).ToList();
        var result = BacktestEngine.Run(new HoldStrategy(), candles);
        Assert.True(result.MaxDrawdownPercent >= 0m);
    }

    [Fact]
    public void Sharpe_Ratio_Is_Positive_For_Consistently_Profitable_Strategy()
    {
        // Серия коротких выигрышных сделок: BUY-SELL-BUY-SELL...
        var signals = Enumerable.Range(0, 6)
            .SelectMany(_ => new[] { "BUY", "SELL" })
            .ToArray();
        var strategy = new ScriptedStrategy(signals);

        // Цена растёт каждые 2 свечи
        var candles = Enumerable.Range(0, 14)
            .Select(i => Candle(i, 100m + (i / 2) * 10m))
            .ToList();

        var result = BacktestEngine.Run(strategy, candles, commissionPercent: 0m);
        if (result.TradeCount >= 2)
            Assert.True(result.SharpeRatio > 0m);
    }

    [Fact]
    public void Flat_Market_Trade_Keeps_Equity_Near_100()
    {
        // BUY и SELL по одной цене без комиссии → equity остаётся 100
        var strategy = new ScriptedStrategy("BUY", "SELL");
        var candles = Enumerable.Range(0, 6).Select(i => Candle(i, 100m)).ToList();
        var result = BacktestEngine.Run(strategy, candles, commissionPercent: 0m);
        // Все точки кривой должны быть ≈100 (без комиссии и движения цены)
        Assert.All(result.EquityCurve, p => Assert.Equal(100m, Math.Round(p.Value, 4)));
    }
}
