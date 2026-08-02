using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>Направление и сила одной ячейки тепловой карты.</summary>
/// <param name="Dir">"buy" | "sell" | "hold" — словарь ячейки, тот же, что у карточек сигналов.</param>
/// <param name="Conf">0..1 — насколько выражено движение; ниже <see cref="AiHeatScore.Flat"/> это «hold».</param>
public readonly record struct AiHeatCell(string Dir, double Conf);

/// <summary>
/// Чтение одного таймфрейма по его собственным свечам.
///
/// Тепловая карта раньше не считала таймфреймы вовсе: она брала ОДИН сигнал по монете и умножала
/// его уверенность на фиксированный вектор весов. Пока уверенность высокая, это выглядело
/// правдоподобно, а на спокойном рынке разваливалось: при уверенности 0.35–0.40 каждая ячейка
/// попадала под правило «ниже 0.5 — значит hold», и вся карта становилась сплошь янтарной, а
/// нижняя граница clamp'а печатала в каждой клетке одно и то же число 35. Зелёного и красного на
/// экране не появлялось никогда — при том что 1m и 1D в это же время могли смотреть в разные
/// стороны, и именно ради этого карта и существует.
///
/// Метрика — согласованность движения, умноженная на его размах:
///
///   • СОГЛАСОВАННОСТЬ — корреляция цены со временем внутри окна (коэффициент Пирсона). Ровный ход
///     в одну сторону даёт ±1, болтанка вокруг средней — около нуля. Именно это и должна показывать
///     карта таймфреймов: не «дёрнулось на последней свече», а «идёт ли туда весь отрезок».
///   • РАЗМАХ — ход окна в долях собственной цены. Нужен, чтобы идеально ровный, но ничтожный
///     дрейф в 0,02 % не красил клетку как настоящий тренд.
///
/// Обе величины безразмерны, поэтому пороги не приходится подбирать под монету: биткоин и
/// мем-коин меряются одной шкалой.
///
/// Первым вариантом здесь стояла z-оценка последней цены относительно окна, и её сняли тесты:
/// тренд раздувает собственный разброс, поэтому ровный ход получал МЕНЬШЕ уверенности, чем
/// одиночный всплеск на последней свече. Для карты таймфреймов это ровно наоборот.
/// </summary>
public static class AiHeatScore
{
    /// <summary>Ниже этой убеждённости клетка считается нейтральной.</summary>
    public const double Flat = 0.35;

    /// <summary>Ход окна, начиная с которого размах перестаёт сдерживать оценку (0,4 % цены).</summary>
    private const double FullRange = 0.004;

    /// <summary>Меньше этого числа свечей окно не описывает ничего.</summary>
    public const int MinCandles = 8;

    /// <summary>
    /// Читает окно свечей. Пустое или слишком короткое окно — честный «hold» с нулевой
    /// уверенностью: это «нечего сказать», а не «рынок стоит».
    /// </summary>
    public static AiHeatCell Read(IReadOnlyList<DexOhlcvPoint>? candles)
    {
        if (candles is null || candles.Count < MinCandles) return new AiHeatCell("hold", 0d);

        var closes = candles.Select(c => (double)c.Close).Where(c => c > 0d).ToArray();
        if (closes.Length < MinCandles) return new AiHeatCell("hold", 0d);

        var mean = closes.Average();
        if (mean <= 0d) return new AiHeatCell("hold", 0d);

        var r = TrendCorrelation(closes);
        if (double.IsNaN(r)) return new AiHeatCell("hold", 0d);

        var range = (closes.Max() - closes.Min()) / mean;
        var conviction = Math.Abs(r) * Math.Min(1d, range / FullRange);

        if (conviction < Flat) return new AiHeatCell("hold", conviction);
        return new AiHeatCell(r > 0d ? "buy" : "sell", Math.Min(1d, conviction));
    }

    /// <summary>
    /// Корреляция Пирсона между номером свечи и её ценой: +1 — безостановочный рост, −1 —
    /// безостановочное падение, около нуля — движения без направления. NaN, когда цена в окне
    /// не менялась вовсе (делить не на что) — вызывающая сторона трактует это как «нет движения».
    /// </summary>
    private static double TrendCorrelation(double[] closes)
    {
        var n = closes.Length;
        var meanX = (n - 1) / 2d;
        var meanY = closes.Average();

        double sxy = 0d, sxx = 0d, syy = 0d;
        for (var i = 0; i < n; i++)
        {
            var dx = i - meanX;
            var dy = closes[i] - meanY;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }

        if (sxx <= 0d || syy <= 0d) return double.NaN;
        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>
    /// Запасной путь, когда свечей нет (шлюз не отдал, окно пустое): читаем направление из самого
    /// сигнала по монете.
    ///
    /// Раньше здесь стояло <c>c &lt; 0.5 ? "hold" : dir</c>, и на спокойном рынке это стирало
    /// направление у ВСЕХ ячеек разом. Направление сигнала — это то немногое, что здесь достоверно
    /// известно, и затирать его порогом нельзя; уверенность пусть управляет насыщенностью цвета.
    /// Смягчаем только к дальним таймфреймам, где короткий сигнал и правда мало что значит.
    /// </summary>
    public static AiHeatCell FromSignal(string signalDir, double signalConf, double timeframeWeight)
    {
        var dir = signalDir switch { "BUY" => "buy", "SELL" => "sell", _ => "hold" };
        var conf = Math.Clamp(signalConf * timeframeWeight, 0d, 0.98d);
        return new AiHeatCell(dir, conf);
    }
}
