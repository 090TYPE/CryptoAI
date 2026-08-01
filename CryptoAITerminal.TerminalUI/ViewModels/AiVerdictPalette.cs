using System;
using System.Collections.Generic;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Цвет вердикта AI по его слову.
///
/// Раньше это была одна и та же конструкция <c>switch</c> в девяти вью-моделях, и словари у них
/// пересекались: BULLISH в одной панели красился иначе, чем в другой, а незнакомое слово в одной
/// давало серый, в другой — красный. Пользователь при этом видит их рядом, на одной странице, и
/// читает цвет как смысл — так что расхождение было не косметикой, а разной трактовкой одного и
/// того же ответа.
///
/// Список закрытый и явный. Догадываться по слову нельзя: SHORT_A_LONG_B и BEARISH оба про
/// падение, но первое — рабочий вход в позицию (зелёный), второе — предупреждение (красный).
/// Незнакомое слово даёт нейтральный цвет, а не выдуманный: модель может вернуть что угодно, и
/// покрасить это наугад значит соврать увереннее, чем позволяют данные.
/// </summary>
public static class AiVerdictPalette
{
    /// <summary>Смысл вердикта — то, чем он красится и чем подписывается иконка.</summary>
    public enum Tone
    {
        /// <summary>Благоприятно, вход разрешён, проверка пройдена.</summary>
        Good,
        /// <summary>Требует внимания: тонко, рано, слабо, подождать.</summary>
        Caution,
        /// <summary>Плохо: избегать, заблокировать, переобучено.</summary>
        Bad,
        /// <summary>Интересно, но не приговор: перспективно, рано, разнесено.</summary>
        Notable,
        /// <summary>Ничего определённого.</summary>
        Neutral,
    }

    // Значения перенесены один в один из панелей, где они и жили, — чтобы объединение словарей не
    // поменяло смысл ни на одной странице.
    private static readonly IReadOnlyDictionary<string, Tone> Words =
        new Dictionary<string, Tone>(StringComparer.OrdinalIgnoreCase)
        {
            // Настроение рынка и дайджесты
            ["BULLISH"] = Tone.Good,
            ["BEARISH"] = Tone.Bad,
            ["RISK_ON"] = Tone.Good,
            ["RISK_OFF"] = Tone.Bad,

            // Пред-торговая проверка
            ["APPROVE"] = Tone.Good,
            ["CAUTION"] = Tone.Caution,
            ["BLOCK"] = Tone.Bad,

            // Вердикт по токену (снайпер)
            ["FAVORABLE"] = Tone.Good,
            ["RISKY"] = Tone.Caution,
            ["AVOID"] = Tone.Bad,

            // Разбор бэктеста
            ["ROBUST"] = Tone.Good,
            ["PROMISING"] = Tone.Notable,
            ["WEAK"] = Tone.Caution,
            ["OVERFIT"] = Tone.Bad,

            // Корреляции портфеля
            ["DIVERSIFIED"] = Tone.Notable,
            ["CONCENTRATED"] = Tone.Bad,

            // Карта ликвидаций: сторона, куда тянет цену
            ["MAGNET_ABOVE"] = Tone.Good,
            ["MAGNET_BELOW"] = Tone.Bad,

            // Потоки китов: монеты уходят с бирж или приходят на них
            ["ACCUMULATION"] = Tone.Good,
            ["DISTRIBUTION"] = Tone.Bad,

            // Статарбитраж: оба направления — рабочий вход, ждать — нет
            ["LONG_A_SHORT_B"] = Tone.Good,
            ["SHORT_A_LONG_B"] = Tone.Good,
            ["WAIT"] = Tone.Caution,

            // Торговый ассистент и сигналы
            ["LONG"] = Tone.Good,
            ["SHORT"] = Tone.Bad,
            ["BUY"] = Tone.Good,
            ["SELL"] = Tone.Bad,
            ["HOLD"] = Tone.Neutral,

            // Тренды DEX
            ["MOMENTUM"] = Tone.Good,
            ["EARLY"] = Tone.Notable,
            ["FADING"] = Tone.Caution,

            // Опционы
            ["BUY_PREMIUM"] = Tone.Notable,
            ["SELL_PREMIUM"] = Tone.Caution,

            // Явно нейтральные слова разных словарей
            ["NEUTRAL"] = Tone.Neutral,
            ["BALANCED"] = Tone.Neutral,
            ["WATCH"] = Tone.Neutral,
        };

    /// <summary>Смысл слова. Незнакомое — нейтрально.</summary>
    public static Tone ToneOf(string? verdict) =>
        verdict is not null && Words.TryGetValue(verdict.Trim(), out var tone) ? tone : Tone.Neutral;

    /// <summary>Цвет текста вердикта.</summary>
    public static string ColorOf(string? verdict) => Color(ToneOf(verdict));

    public static string Color(Tone tone) => tone switch
    {
        Tone.Good => SemanticColor.Positive,
        Tone.Bad => SemanticColor.Negative,
        Tone.Caution => SemanticColor.Warning,
        Tone.Notable => SemanticColor.Accent,
        _ => SemanticColor.Muted,
    };

    /// <summary>
    /// Подложка плашки: тот же цвет, что и текст, но прозрачный. Плашка одного цвета с текстом
    /// читается как одно целое, а не как два разных сообщения рядом.
    /// </summary>
    public static string TintOf(string? verdict) => SemanticColor.Alpha(ColorOf(verdict), "1E");

    /// <summary>Рамка плашки — тот же цвет чуть плотнее подложки.</summary>
    public static string StrokeOf(string? verdict) => SemanticColor.Alpha(ColorOf(verdict), "55");

    /// <summary>
    /// Слово вердикта для показа: подчёркивания заменяются пробелами.
    /// <c>MAGNET_ABOVE</c> — это имя из схемы ответа, а не текст для человека.
    /// </summary>
    public static string Display(string? verdict) =>
        string.IsNullOrWhiteSpace(verdict) ? "" : verdict.Trim().Replace('_', ' ');
}
