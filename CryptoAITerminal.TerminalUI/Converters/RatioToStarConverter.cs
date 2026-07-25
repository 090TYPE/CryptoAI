using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>
/// Turns a 0..1 fill fraction into a star <see cref="GridLength"/> so a two-column
/// grid can render a proportional progress/allocation bar without hard-coded pixel
/// widths. Pass ConverterParameter="rest" for the remaining (unfilled) column.
/// Matches the design mock's "width: NN%" bars.
/// </summary>
public sealed class RatioToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ratio = value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            _ => double.TryParse(value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0
        };
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) ratio = 0;
        ratio = Math.Clamp(ratio, 0, 1);
        var rest = string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase);
        var portion = rest ? 1 - ratio : ratio;
        // Avalonia rejects a zero-star GridLength on some versions; keep a hair.
        return new GridLength(Math.Max(portion, 0.0001), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
