using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Семантическая палитра для view-models.
///
/// Раньше каждая VM носила свой hex, и «рост/падение» существовал в пяти разных парах —
/// светлая тема или ребрендинг требовали правки сотен файлов. Здесь один слой косвенности:
/// VM просит смысл («positive»), значение приходит из ресурсов приложения
/// (<c>Styles/AppStyles.axaml</c>), а палитра остаётся в разметке в единственном экземпляре.
///
/// Два способа обращения:
/// <list type="bullet">
/// <item><description><c>SemanticColor.Positive</c> — готовая строка "#RRGGBB". Годится везде,
/// включая привязки <c>Foreground="{Binding X}"</c> без конвертера, где Avalonia сама парсит
/// строку в кисть.</description></item>
/// <item><description><c>SemanticColor.Keys.Positive</c> — сам ключ. Его понимает
/// <c>StringToBrushConverter</c> и отдаёт живую кисть из ресурсов; годится только там,
/// где привязка идёт через этот конвертер.</description></item>
/// </list>
/// </summary>
public static class SemanticColor
{
    /// <summary>Ключи, которые понимает <c>StringToBrushConverter</c>.</summary>
    public static class Keys
    {
        public const string Positive   = "positive";
        public const string Negative   = "negative";
        public const string Warning    = "warning";
        public const string Accent     = "accent";
        public const string Muted      = "muted";
        public const string Soft       = "soft";
        public const string Dim        = "dim";
        public const string Primary    = "primary";
        public const string Surface    = "surface";
        public const string SurfaceAlt = "surface-alt";
        public const string Stroke     = "stroke";
    }

    /// <summary>
    /// Ключ → имя ресурса в палитре + жёсткий фолбэк. Фолбэк повторяет текущее значение
    /// токена: он нужен на этапе, когда <see cref="Application.Current"/> ещё не поднят
    /// (превьюер дизайнера, VM, собранная до инициализации приложения).
    /// </summary>
    private static readonly Dictionary<string, (string Resource, string Fallback)> Tokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Keys.Positive]   = ("Positive",    "#3DDC84"),
            [Keys.Negative]   = ("Negative",    "#FF6B6B"),
            [Keys.Warning]    = ("Warning",     "#F4B860"),
            [Keys.Accent]     = ("Accent",      "#21E6C1"),
            [Keys.Muted]      = ("TextMuted",   "#8FA3B8"),
            [Keys.Soft]       = ("TextSoft",    "#7C97AF"),
            [Keys.Dim]        = ("TextDim",     "#6A85A0"),
            [Keys.Primary]    = ("TextPrimary", "#E8F4FF"),
            [Keys.Surface]    = ("Surface1",    "#07101A"),
            [Keys.SurfaceAlt] = ("Surface2",    "#0A1422"),
            [Keys.Stroke]     = ("PanelStroke", "#152233"),
        };

    private static readonly ConcurrentDictionary<string, string> HexCache = new(StringComparer.OrdinalIgnoreCase);

    private static int _themeHookInstalled;

    // ── Готовые строки цвета ────────────────────────────────────────────────

    /// <summary>Рост, прибыль, «включено», успешная проверка.</summary>
    public static string Positive => Get(Keys.Positive);
    /// <summary>Падение, убыток, отказ, критическая ошибка.</summary>
    public static string Negative => Get(Keys.Negative);
    /// <summary>Предупреждение, «армед», деньги на кону.</summary>
    public static string Warning => Get(Keys.Warning);
    /// <summary>Бренд-акцент: активные вкладки, ссылки, кнопки действия.</summary>
    public static string Accent => Get(Keys.Accent);
    /// <summary>Подписи колонок и вторичный текст.</summary>
    public static string Muted => Get(Keys.Muted);
    /// <summary>Текст на ступень тише <see cref="Muted"/>.</summary>
    public static string Soft => Get(Keys.Soft);
    /// <summary>Самый тихий текст, ещё проходящий по контрасту.</summary>
    public static string Dim => Get(Keys.Dim);
    /// <summary>Основной текст.</summary>
    public static string Primary => Get(Keys.Primary);
    /// <summary>Фон карточки.</summary>
    public static string Surface => Get(Keys.Surface);
    /// <summary>Фон вложенной панели внутри карточки.</summary>
    public static string SurfaceAlt => Get(Keys.SurfaceAlt);
    /// <summary>Рамка панели, разделители.</summary>
    public static string Stroke => Get(Keys.Stroke);

    // ── Разрешение ──────────────────────────────────────────────────────────

    /// <summary>
    /// Значение токена как "#RRGGBB" (или "#AARRGGBB", если в палитре задана прозрачность).
    /// Неизвестный ключ возвращается как есть — так строка с готовым hex проходит насквозь.
    /// </summary>
    public static string Get(string key) => HexCache.GetOrAdd(key, Lookup);

    /// <summary>Ключ известен палитре? Отдаёт имя ресурса и фолбэк для конвертера.</summary>
    public static bool TryToken(string key, out string resource, out string fallback)
    {
        if (Tokens.TryGetValue(key, out var token))
        {
            resource = token.Resource;
            fallback = token.Fallback;
            return true;
        }
        resource = string.Empty;
        fallback = string.Empty;
        return false;
    }

    /// <summary>
    /// Кисть токена прямо из ресурсов приложения. Возвращается сам объект из словаря,
    /// а не копия: смена темы, подменяющая ресурс, доходит до уже отрисованных элементов.
    /// </summary>
    public static bool TryPaletteBrush(string resource, out IBrush brush)
    {
        brush = Brushes.Transparent;
        var app = Application.Current;
        if (app is null) return false;

        // ActualThemeVariant — Avalonia-свойство; читаем его только с UI-потока.
        // С фонового достаточно нетематизированного словаря: в нём и лежит палитра.
        var onUiThread = Dispatcher.UIThread.CheckAccess();
        ThemeVariant? theme = onUiThread ? app.ActualThemeVariant : null;
        if (!app.TryGetResource(resource, theme, out var value) || value is not IBrush found) return false;

        if (onUiThread) HookThemeChanges(app);
        brush = found;
        return true;
    }

    /// <summary>Сбрасывает кеш строк — палитра будет перечитана при следующем обращении.</summary>
    public static void Invalidate() => HexCache.Clear();

    /// <summary>
    /// "#RRGGBB" + две hex-цифры альфы → "#AARRGGBB" (в Avalonia альфа идёт первой).
    /// Нужен для подложек, которые должны быть тем же цветом, что и текст на них.
    /// </summary>
    public static string Alpha(string color, string aa)
        => string.IsNullOrEmpty(color) || color[0] != '#' || color.Length < 7
            ? color
            : "#" + aa + color.Substring(color.Length - 6);

    private static string Lookup(string key)
    {
        if (!Tokens.TryGetValue(key, out var token)) return key;
        return TryPaletteBrush(token.Resource, out var brush) && brush is ISolidColorBrush solid
            ? Format(solid.Color)
            : token.Fallback;
    }

    private static void HookThemeChanges(Application app)
    {
        if (Interlocked.Exchange(ref _themeHookInstalled, 1) != 0) return;
        app.ActualThemeVariantChanged += (_, _) => Invalidate();
    }

    /// <summary>Alpha опускается, когда цвет непрозрачен: строку ждут помощники вида "#RRGGBB".</summary>
    private static string Format(Color c) => c.A == 0xFF
        ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
        : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
