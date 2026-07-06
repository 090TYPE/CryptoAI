using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>
/// Turns a 0–100 percentage into a star <see cref="GridLength"/> so a two-column
/// Grid can render a proportional fill bar that scales with its container (the
/// filled column gets <c>value*</c>, the remainder column gets <c>(100-value)*</c>
/// via ConverterParameter="rest"). Used by the portfolio-desk health/allocation
/// bars to match the design mock's percentage-width bars.
/// </summary>
public sealed class PercentToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double pct = value switch
        {
            double d => d,
            int i => i,
            decimal m => (double)m,
            _ => double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0,
        };
        pct = Math.Clamp(pct, 0, 100);
        bool rest = string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase);
        var star = rest ? 100 - pct : pct;
        // Avoid a 0* column collapsing oddly; a hair of weight keeps layout stable.
        return new GridLength(Math.Max(star, 0.0001), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
