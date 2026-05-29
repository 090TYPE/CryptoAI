using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class VwapStrategyTests
{
    private static MarketData Tick(decimal price, decimal vol24h = 0m) =>
        new() { LastPrice = price, Volume24hUsd = vol24h };

    [Fact]
    public void EarlyTicks_ReturnHold()
    {
        var s = new VwapStrategy();
        for (int i = 0; i < 4; i++)
        {
            var (signal, _) = s.Analyze(Tick(100m));
            Assert.Equal("HOLD", signal);
        }
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var s = new VwapStrategy();
        for (int i = 0; i < 10; i++) s.Analyze(Tick(100m + i));

        s.Reset();

        // После Reset первые тики снова HOLD как в начальном состоянии.
        var (signal, conf) = s.Analyze(Tick(100m));
        Assert.Equal("HOLD", signal);
        Assert.Equal(0m, conf);
    }

    [Fact]
    public void ZeroOrNegativePrice_ReturnsHold()
    {
        var s = new VwapStrategy();
        var (signal, _) = s.Analyze(Tick(0m));
        Assert.Equal("HOLD", signal);
    }

    [Fact]
    public void PriceCrossesAboveVwap_EmitsBuy()
    {
        var s = new VwapStrategy(bandThresholdPct: 0m);

        // Сначала формируем VWAP вокруг 100 на нескольких тиках чтобы система прогрелась.
        for (int i = 0; i < 6; i++) s.Analyze(Tick(100m));

        // Резкий рост цены даёт пересечение снизу вверх через VWAP.
        s.Analyze(Tick(90m));   // ниже VWAP — формируем prevPrice<prevVwap
        var (signal, conf) = s.Analyze(Tick(115m));
        Assert.Equal("BUY", signal);
        Assert.True(conf >= 0.5m && conf <= 0.95m);
    }

    [Fact]
    public void PriceCrossesBelowVwap_EmitsSell()
    {
        var s = new VwapStrategy(bandThresholdPct: 0m);
        for (int i = 0; i < 6; i++) s.Analyze(Tick(100m));

        s.Analyze(Tick(115m));   // выше VWAP
        var (signal, _) = s.Analyze(Tick(85m));
        Assert.Equal("SELL", signal);
    }

    [Fact]
    public void BandThreshold_SuppressesNoiseAroundVwap()
    {
        var sLoose = new VwapStrategy(bandThresholdPct: 0m);
        var sTight = new VwapStrategy(bandThresholdPct: 5m);

        // Прогрев у обеих
        for (int i = 0; i < 6; i++)
        {
            sLoose.Analyze(Tick(100m));
            sTight.Analyze(Tick(100m));
        }

        // Цена колеблется в пределах ±1% — должна давать сигналы только без band.
        sLoose.Analyze(Tick(99m));
        sTight.Analyze(Tick(99m));
        var (looseSig, _) = sLoose.Analyze(Tick(101m));
        var (tightSig, _) = sTight.Analyze(Tick(101m));

        Assert.Equal("BUY", looseSig);
        Assert.Equal("HOLD", tightSig);
    }

    [Fact]
    public void Volume24h_UsedAsWeight_WhenAvailable()
    {
        var s = new VwapStrategy(bandThresholdPct: 0m);

        // Прогрев с растущим Volume24h — каждый дельта-инкремент становится весом.
        s.Analyze(Tick(100m, vol24h: 1_000_000m));
        s.Analyze(Tick(100m, vol24h: 1_001_000m));
        s.Analyze(Tick(100m, vol24h: 1_002_000m));
        s.Analyze(Tick(100m, vol24h: 1_003_000m));
        s.Analyze(Tick(100m, vol24h: 1_004_000m));
        s.Analyze(Tick(100m, vol24h: 1_005_000m));

        // Перекрёстный паттерн — должен сработать.
        s.Analyze(Tick(90m, vol24h: 1_006_000m));
        var (signal, _) = s.Analyze(Tick(120m, vol24h: 1_007_000m));
        Assert.Equal("BUY", signal);
    }

    [Fact]
    public void NegativeVolumeDelta_FallsBackToUnitWeight()
    {
        // 24h rolling volume может откатываться. Это не должно ломать стратегию.
        var s = new VwapStrategy(bandThresholdPct: 0m);
        s.Analyze(Tick(100m, vol24h: 1_000_000m));
        s.Analyze(Tick(100m, vol24h:   500_000m));  // delta < 0 — fallback на unit weight
        s.Analyze(Tick(100m, vol24h:   800_000m));
        s.Analyze(Tick(100m, vol24h:   900_000m));
        s.Analyze(Tick(100m, vol24h: 1_000_000m));
        s.Analyze(Tick(100m, vol24h: 1_100_000m));

        s.Analyze(Tick(90m, vol24h: 1_100_000m));
        var (signal, _) = s.Analyze(Tick(120m, vol24h: 1_200_000m));
        Assert.Equal("BUY", signal);
    }

    [Fact]
    public void Name_ReflectsBandConfig()
    {
        Assert.Equal("VWAP(band=0.05%)", new VwapStrategy(0.05m).Name);
        Assert.Equal("VWAP(band=0.5%)", new VwapStrategy(0.5m).Name);
    }

    [Fact]
    public void NegativeBand_IsClampedToZero()
    {
        var s = new VwapStrategy(bandThresholdPct: -1m);
        // Не должно бросать; Name отражает clamp.
        Assert.Equal("VWAP(band=0%)", s.Name);

        for (int i = 0; i < 6; i++) s.Analyze(Tick(100m));
        s.Analyze(Tick(90m));
        var (signal, _) = s.Analyze(Tick(110m));
        Assert.Equal("BUY", signal);
    }
}
