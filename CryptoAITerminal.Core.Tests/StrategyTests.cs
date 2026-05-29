using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

// ── Common helpers ──────────────────────────────────────────────────────────

public static class StrategyTestHelper
{
    public static MarketData Tick(decimal price, decimal high = 0m, decimal low = 0m) =>
        new() { LastPrice = price, High24h = high, Low24h = low };

    public static void FeedPrices(IStrategy s, params decimal[] prices)
    {
        foreach (var p in prices) s.Analyze(Tick(p));
    }
}

// ═══════════════════════════ SimpleMaStrategy ═══════════════════════════════

public class SimpleMaStrategyTests
{
    [Fact]
    public void NotEnoughData_ReturnsHold()
    {
        var s = new SimpleMaStrategy(3, 5);
        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m));
        Assert.Equal("HOLD", sig);
    }

    [Fact]
    public void FastMa_CrossesAboveSlowMa_EmitsBuy()
    {
        var s = new SimpleMaStrategy(3, 5);
        // Заливаем убывающие цены — после 5 тиков prev = (fast=85, slow=90), fast<slow.
        StrategyTestHelper.FeedPrices(s, 100, 95, 90, 85, 80);

        // 6-й тик: резкий рост → fast(85,80,120)=95 > slow=94 — cross up.
        var (sig, conf) = s.Analyze(StrategyTestHelper.Tick(120m));

        Assert.Equal("BUY", sig);
        Assert.True(conf >= 0.55m && conf <= 0.95m);
    }

    [Fact]
    public void FastMa_CrossesBelowSlowMa_EmitsSell()
    {
        var s = new SimpleMaStrategy(3, 5);
        // После 5 тиков fast>slow.
        StrategyTestHelper.FeedPrices(s, 100, 105, 110, 115, 120);
        // 6-й тик: резкий обвал → fast<slow.
        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(80m));

        Assert.Equal("SELL", sig);
    }

    [Fact]
    public void FlatPrices_AfterCross_DoNotReEmit()
    {
        var s = new SimpleMaStrategy(3, 5);
        StrategyTestHelper.FeedPrices(s, 100, 100, 100, 100, 100, 100, 100);
        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m));
        Assert.Equal("HOLD", sig);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var s = new SimpleMaStrategy(3, 5);
        StrategyTestHelper.FeedPrices(s, 100, 95, 90, 85, 80, 120, 140);
        s.Reset();

        // После reset снова нужны 5 тиков для slow MA.
        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m));
        Assert.Equal("HOLD", sig);
    }

    [Fact]
    public void Name_FormatsPeriods()
    {
        Assert.Equal("SMA(10/30)", new SimpleMaStrategy(10, 30).Name);
        Assert.Equal("SMA(5/200)", new SimpleMaStrategy(5, 200).Name);
    }
}

// ═══════════════════════════ BollingerBandsStrategy ═════════════════════════

public class BollingerBandsStrategyTests
{
    [Fact]
    public void NotEnoughData_ReturnsHold()
    {
        var s = new BollingerBandsStrategy(20, 2.0m);
        for (int i = 0; i < 19; i++)
        {
            var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m + i * 0.1m));
            Assert.Equal("HOLD", sig);
        }
    }

    [Fact]
    public void FlatPrices_StdDevZero_ReturnsHold()
    {
        var s = new BollingerBandsStrategy(10, 2.0m);
        for (int i = 0; i < 10; i++) s.Analyze(StrategyTestHelper.Tick(100m));
        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m));
        Assert.Equal("HOLD", sig);
    }

    [Fact]
    public void PriceBelowLowerBand_EmitsBuy()
    {
        var s = new BollingerBandsStrategy(10, 2.0m);
        // Заливаем стабильные цены вокруг 100, лёгкий шум.
        decimal[] series = [100, 101, 99, 100, 102, 98, 101, 99, 100, 100];
        StrategyTestHelper.FeedPrices(s, series);

        // Резкий проход вниз ниже lower band.
        var (sig, conf) = s.Analyze(StrategyTestHelper.Tick(80m));
        Assert.Equal("BUY", sig);
        Assert.True(conf > 0.5m);
    }

    [Fact]
    public void PriceAboveUpperBand_EmitsSell()
    {
        var s = new BollingerBandsStrategy(10, 2.0m);
        decimal[] series = [100, 101, 99, 100, 102, 98, 101, 99, 100, 100];
        StrategyTestHelper.FeedPrices(s, series);

        var (sig, conf) = s.Analyze(StrategyTestHelper.Tick(120m));
        Assert.Equal("SELL", sig);
        Assert.True(conf > 0.5m);
    }

    [Fact]
    public void PriceInUpperQuarter_EmitsPartialSell()
    {
        var s = new BollingerBandsStrategy(10, 2.0m);
        // Серия c понятным σ, чтобы цена ниже upper band но выше 75% band position.
        decimal[] series = [100, 102, 98, 100, 102, 98, 100, 102, 98, 100];
        StrategyTestHelper.FeedPrices(s, series);

        // Цена внутри band, ближе к upper.
        // Для BB(10) на этой серии σ≈1.55, upper≈103.1, lower≈96.9, range≈6.2.
        // Цена 102.5: bandPos≈0.90 → SELL.
        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(102.5m));
        Assert.Equal("SELL", sig);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var s = new BollingerBandsStrategy(10, 2.0m);
        StrategyTestHelper.FeedPrices(s, 100, 101, 99, 100, 102, 98, 101, 99, 100, 100);
        s.Reset();
        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(80m));
        // После reset недостаточно данных, должен быть HOLD.
        Assert.Equal("HOLD", sig);
    }
}

// ═══════════════════════════ BreakoutStrategy ═══════════════════════════════

public class BreakoutStrategyTests
{
    [Fact]
    public void NotEnoughData_ReturnsHold()
    {
        var s = new BreakoutStrategy(5);
        for (int i = 0; i < 4; i++)
        {
            var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m, high: 101m, low: 99m));
            Assert.Equal("HOLD", sig);
        }
    }

    [Fact]
    public void Upside_Breakout_EmitsBuy()
    {
        var s = new BreakoutStrategy(5);
        // Боковик 95..105 на 5 свечах.
        s.Analyze(StrategyTestHelper.Tick(100m, high: 105m, low: 95m));
        s.Analyze(StrategyTestHelper.Tick(100m, high: 104m, low: 96m));
        s.Analyze(StrategyTestHelper.Tick(100m, high: 103m, low: 97m));
        s.Analyze(StrategyTestHelper.Tick(100m, high: 105m, low: 95m));
        s.Analyze(StrategyTestHelper.Tick(100m, high: 102m, low: 98m));

        // 6-я свеча: цена 120 > resistance 105.
        var (sig, conf) = s.Analyze(StrategyTestHelper.Tick(120m, high: 121m, low: 100m));
        Assert.Equal("BUY", sig);
        Assert.True(conf > 0.5m);
    }

    [Fact]
    public void Downside_Breakout_EmitsSell()
    {
        var s = new BreakoutStrategy(5);
        for (int i = 0; i < 5; i++)
            s.Analyze(StrategyTestHelper.Tick(100m, high: 105m, low: 95m));

        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(80m, high: 100m, low: 79m));
        Assert.Equal("SELL", sig);
    }

    [Fact]
    public void PriceInsideRange_ReturnsHold()
    {
        var s = new BreakoutStrategy(5);
        for (int i = 0; i < 5; i++)
            s.Analyze(StrategyTestHelper.Tick(100m, high: 105m, low: 95m));

        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m, high: 103m, low: 97m));
        Assert.Equal("HOLD", sig);
    }

    [Fact]
    public void FlatRange_ZeroSize_ReturnsHold()
    {
        var s = new BreakoutStrategy(3);
        // Все цены идентичны → range = 0.
        for (int i = 0; i < 3; i++)
            s.Analyze(StrategyTestHelper.Tick(100m, high: 100m, low: 100m));

        var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m, high: 100m, low: 100m));
        Assert.Equal("HOLD", sig);
    }

    [Fact]
    public void Name_FormatsPeriod()
    {
        Assert.Equal("Breakout(20)", new BreakoutStrategy(20).Name);
        Assert.Equal("Breakout(50)", new BreakoutStrategy(50).Name);
    }
}

// ═══════════════════════════ MacdStrategy ═══════════════════════════════════

public class MacdStrategyTests
{
    [Fact]
    public void NotEnoughData_ReturnsHold()
    {
        var s = new MacdStrategy(12, 26, 9);
        for (int i = 0; i < 5; i++)
        {
            var (sig, _) = s.Analyze(StrategyTestHelper.Tick(100m));
            Assert.Equal("HOLD", sig);
        }
    }

    [Fact]
    public void UpwardTrend_AfterDownward_EmitsBuy()
    {
        var s = new MacdStrategy(3, 6, 3);

        // Сначала нисходящий тренд — гистограмма уйдёт ниже 0.
        for (int i = 0; i < 25; i++)
            s.Analyze(StrategyTestHelper.Tick(100m - i));

        // Резко разворачиваем — гистограмма перейдёт через 0 снизу вверх.
        bool sawBuy = false;
        for (int i = 0; i < 30; i++)
        {
            var (sig, _) = s.Analyze(StrategyTestHelper.Tick(75m + i * 5));
            if (sig == "BUY") { sawBuy = true; break; }
        }
        Assert.True(sawBuy, "Expected MACD BUY signal during reversal");
    }

    [Fact]
    public void DownwardTrend_AfterUpward_EmitsSell()
    {
        var s = new MacdStrategy(3, 6, 3);

        for (int i = 0; i < 25; i++)
            s.Analyze(StrategyTestHelper.Tick(100m + i));

        bool sawSell = false;
        for (int i = 0; i < 30; i++)
        {
            var (sig, _) = s.Analyze(StrategyTestHelper.Tick(125m - i * 5));
            if (sig == "SELL") { sawSell = true; break; }
        }
        Assert.True(sawSell, "Expected MACD SELL signal during reversal");
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var s = new MacdStrategy(3, 6, 3);
        for (int i = 0; i < 25; i++) s.Analyze(StrategyTestHelper.Tick(100m + i));
        s.Reset();

        // После reset первый тик возвращает HOLD как из чистого состояния.
        var (sig, conf) = s.Analyze(StrategyTestHelper.Tick(100m));
        Assert.Equal("HOLD", sig);
        Assert.Equal(0m, conf);
    }

    [Fact]
    public void Name_FormatsPeriods()
    {
        Assert.Equal("MACD(12/26/9)", new MacdStrategy(12, 26, 9).Name);
        Assert.Equal("MACD(5/10/3)",  new MacdStrategy(5, 10, 3).Name);
    }

    [Fact]
    public void ConstructorEnforcesMonotonicPeriods()
    {
        // _slowPeriod ≥ fastPeriod+1
        var s = new MacdStrategy(20, 5, 3);
        // Name отражает корректировку (slow=21).
        Assert.Contains("/21/", s.Name);
    }
}
