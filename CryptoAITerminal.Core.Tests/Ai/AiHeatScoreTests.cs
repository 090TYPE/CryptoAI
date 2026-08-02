using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Мультитаймфреймовая карта на вкладке AI Signals была сплошь янтарной — ни одной зелёной или
/// красной клетки, и одно и то же число во всех.
///
/// Причина: карта таймфреймы не считала. Она брала ОДИН сигнал по монете, умножала его уверенность
/// на фиксированный вектор весов и всё, что оказалось ниже 0.5, объявляла «hold»:
///
///     var c = Math.Clamp(s.Conf * tfWeight[ti], 0.35, 0.98);
///     var dir = c &lt; 0.5 &amp;&amp; baseDir != "hold" ? "hold" : baseDir;
///
/// На спокойном рынке уверенность держится в районе 0.35–0.40, поэтому условие срабатывало на
/// КАЖДОЙ клетке, а печатаемое число упиралось в нижнюю границу clamp'а — отсюда «35» повсюду.
///
/// Теперь каждая клетка читает собственные свечи своего таймфрейма. Метрика — z-оценка импульса:
/// отклонение последней цены от средней окна в единицах разброса этого же окна. Она сама по себе
/// разная на разных таймфреймах и не требует подбора порогов под монету: дешёвый альт и биткоин
/// меряются одной шкалой.
/// </summary>
public class AiHeatScoreTests
{
    private static IReadOnlyList<DexOhlcvPoint> Candles(params double[] closes) =>
        closes.Select((c, i) => new DexOhlcvPoint
        {
            Timestamp = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
            Open = (decimal)c, High = (decimal)c, Low = (decimal)c, Close = (decimal)c, Volume = 1m,
        }).ToList();

    /// <summary>Ровный ход в одну сторону: 100, 101, 102, …</summary>
    private static IReadOnlyList<DexOhlcvPoint> Ramp(double from, double step, int count) =>
        Candles(Enumerable.Range(0, count).Select(i => from + step * i).ToArray());

    // ── Направление ───────────────────────────────────────────────────────────

    [Fact]
    public void A_rising_window_reads_as_buy()
    {
        var cell = AiHeatScore.Read(Ramp(100, +1, 30));

        Assert.Equal("buy", cell.Dir);
        Assert.True(cell.Conf > 0.5, $"уверенность {cell.Conf:F2} слишком мала для явного роста");
    }

    [Fact]
    public void A_falling_window_reads_as_sell()
    {
        var cell = AiHeatScore.Read(Ramp(100, -1, 30));

        Assert.Equal("sell", cell.Dir);
        Assert.True(cell.Conf > 0.5);
    }

    [Fact]
    public void Green_and_red_are_actually_reachable()
    {
        // Суть правки: раньше ни одна клетка не могла стать зелёной или красной.
        var dirs = new[]
        {
            AiHeatScore.Read(Ramp(100, +1, 30)).Dir,
            AiHeatScore.Read(Ramp(100, -1, 30)).Dir,
        };

        Assert.Contains("buy", dirs);
        Assert.Contains("sell", dirs);
    }

    // ── Отсутствие движения ───────────────────────────────────────────────────

    [Fact]
    public void A_flat_window_reads_as_hold_with_no_confidence()
    {
        var cell = AiHeatScore.Read(Candles(Enumerable.Repeat(100d, 30).ToArray()));

        Assert.Equal("hold", cell.Dir);
        Assert.Equal(0d, cell.Conf);
    }

    [Fact]
    public void Noise_around_a_mean_is_not_a_direction()
    {
        // Цена гуляет вокруг средней и заканчивает ровно на ней — направления здесь нет.
        var cell = AiHeatScore.Read(Candles(100, 101, 99, 101, 99, 101, 99, 101, 99, 100));

        Assert.Equal("hold", cell.Dir);
    }

    // ── Нечего сказать ────────────────────────────────────────────────────────

    [Fact]
    public void No_candles_at_all_is_hold_with_zero_confidence()
    {
        Assert.Equal(new AiHeatCell("hold", 0d), AiHeatScore.Read(null));
        Assert.Equal(new AiHeatCell("hold", 0d), AiHeatScore.Read([]));
    }

    [Fact]
    public void Too_few_candles_is_hold_rather_than_a_guess()
    {
        var cell = AiHeatScore.Read(Ramp(100, +5, AiHeatScore.MinCandles - 1));

        Assert.Equal("hold", cell.Dir);
        Assert.Equal(0d, cell.Conf);
    }

    [Fact]
    public void A_zero_priced_feed_cannot_produce_a_direction()
    {
        // Деление на разброс: нулевые цены не должны давать NaN и уж точно не «покупать».
        var cell = AiHeatScore.Read(Candles(Enumerable.Repeat(0d, 30).ToArray()));

        Assert.Equal("hold", cell.Dir);
        Assert.False(double.IsNaN(cell.Conf));
    }

    // ── Шкала одинакова для дорогих и дешёвых монет ───────────────────────────

    [Fact]
    public void The_scale_does_not_depend_on_the_price_of_the_coin()
    {
        // Один и тот же ход в процентах на биткоине и на мем-коине обязан читаться одинаково —
        // иначе пороги пришлось бы подбирать под каждую монету.
        var expensive = AiHeatScore.Read(Ramp(60_000, +600, 30));
        var cheap = AiHeatScore.Read(Ramp(0.05, +0.0005, 30));

        Assert.Equal(expensive.Dir, cheap.Dir);
        Assert.Equal(expensive.Conf, cheap.Conf, 6);
    }

    [Fact]
    public void Confidence_is_bounded_to_a_usable_range()
    {
        // Насыщенность цвета берётся отсюда — значение за пределами 0..1 сломало бы отрисовку.
        foreach (var step in new[] { 0.01, 1d, 100d, 10_000d })
        {
            var cell = AiHeatScore.Read(Ramp(1000, step, 30));
            Assert.InRange(cell.Conf, 0d, 1d);
        }
    }

    [Fact]
    public void A_stronger_move_is_not_less_confident_than_a_weaker_one()
    {
        var weak = AiHeatScore.Read(Candles(100, 100, 100, 100, 100, 100, 100, 100, 100, 100.4));
        var strong = AiHeatScore.Read(Ramp(100, +2, 30));

        Assert.True(strong.Conf >= weak.Conf);
    }

    // ── Запасной путь без свечей ──────────────────────────────────────────────

    [Fact]
    public void Without_candles_the_signal_direction_is_kept_not_erased()
    {
        // Прежнее правило «ниже 0.5 — значит hold» стирало направление у всех клеток разом именно
        // на таких значениях уверенности. Оно и красило карту в один цвет.
        var cell = AiHeatScore.FromSignal("BUY", signalConf: 0.38, timeframeWeight: 0.92);

        Assert.Equal("buy", cell.Dir);
        Assert.True(cell.Conf > 0d);
    }

    [Fact]
    public void The_fallback_keeps_a_hold_signal_neutral()
    {
        Assert.Equal("hold", AiHeatScore.FromSignal("HOLD", 0.9, 1.0).Dir);
    }

    [Fact]
    public void The_fallback_softens_towards_the_longer_frames()
    {
        var near = AiHeatScore.FromSignal("SELL", 0.8, 1.0);
        var far = AiHeatScore.FromSignal("SELL", 0.8, 0.72);

        Assert.Equal("sell", near.Dir);
        Assert.Equal("sell", far.Dir);          // направление не теряется…
        Assert.True(far.Conf < near.Conf);      // …но цвет бледнее
    }

    [Fact]
    public void The_fallback_never_leaves_the_drawable_range()
    {
        Assert.InRange(AiHeatScore.FromSignal("BUY", 1.5, 1.0).Conf, 0d, 0.98d);
        Assert.InRange(AiHeatScore.FromSignal("BUY", -1d, 1.0).Conf, 0d, 0.98d);
    }
}
