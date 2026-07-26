using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>
/// Turns a dynamic colour <b>string</b> into an <see cref="IBrush"/>. Two shapes are accepted:
/// <list type="bullet">
/// <item><description>a semantic key from <see cref="SemanticColor.Keys"/> ("positive", "stroke", …),
/// resolved against the application palette so the colour survives a theme swap;</description></item>
/// <item><description>a literal — "#3ddc84", "#AARRGGBB", a named colour, or an "rgba(r,g,b,a)"
/// string carried over from the design mock.</description></item>
/// </list>
/// Many portfolio-desk panels compute their accent colour per-row on the view model, so a single
/// converter keeps the XAML free of a bespoke brush property per cell. Parsed literals are cached
/// and frozen; semantic keys are not, because the palette behind them can change.
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public static readonly StringToBrushConverter Instance = new();

    private static readonly ConcurrentDictionary<string, IBrush> Cache = new();

    /// <summary>Brush returned when the value is null/blank/unparseable.</summary>
    public IBrush Fallback { get; set; } = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IBrush brush) return brush;
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return Fallback;
        var raw = text.Trim();
        if (TryResolveToken(raw, out var themed)) return themed;
        return Cache.GetOrAdd(raw, Parse) ?? Fallback;
    }

    /// <summary>
    /// Semantic key → the brush object living in the application resources. Deliberately outside
    /// <see cref="Cache"/>: caching would freeze the palette that was loaded first.
    /// </summary>
    private static bool TryResolveToken(string raw, out IBrush brush)
    {
        brush = Brushes.Transparent;
        if (!SemanticColor.TryToken(raw, out var resource, out var fallback)) return false;
        if (SemanticColor.TryPaletteBrush(resource, out var live))
        {
            brush = live;
            return true;
        }
        // Палитра ещё не поднята (превьюер дизайнера) — рисуем токен его дефолтом,
        // иначе ключ провалился бы в парсер hex и элемент остался бы без цвета.
        brush = Cache.GetOrAdd(fallback, ParseLiteral);
        return true;
    }

    private static IBrush ParseLiteral(string raw)
        => TryParseColor(raw, out var color)
            ? new SolidColorBrush(color).ToImmutable()
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;

    private IBrush Parse(string raw)
    {
        if (TryParseColor(raw, out var color))
        {
            var b = new SolidColorBrush(color);
            return b.ToImmutable();
        }
        return Fallback;
    }

    /// <summary>
    /// Accepts "#rgb", "#rrggbb", "#aarrggbb", named colours (via Color.Parse) and
    /// CSS-style "rgb(...)" / "rgba(...)" strings that Avalonia's parser rejects.
    /// </summary>
    public static bool TryParseColor(string raw, out Color color)
    {
        color = default;
        raw = raw.Trim();

        if (raw.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = raw.IndexOf('(');
            var close = raw.IndexOf(')');
            if (open < 0 || close <= open) return false;
            var parts = raw[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) return false;
            if (!byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)) return false;
            if (!byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)) return false;
            byte a = 255;
            if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var af))
                a = (byte)Math.Clamp((int)Math.Round(af * 255), 0, 255);
            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        try { color = Color.Parse(raw); return true; }
        catch { return false; }
    }
}
