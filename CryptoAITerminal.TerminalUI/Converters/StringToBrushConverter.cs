using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>
/// Turns a dynamic colour <b>string</b> (e.g. "#3ddc84", "#AARRGGBB", or an
/// "rgba(r,g,b,a)" literal carried over from the design mock) into an
/// <see cref="IBrush"/>. Many portfolio-desk panels compute their accent colour
/// per-row on the view model, so a single converter keeps the XAML free of a
/// bespoke brush property per cell. Parsed brushes are cached and frozen.
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
        return Cache.GetOrAdd(text.Trim(), Parse) ?? Fallback;
    }

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
