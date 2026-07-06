using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>
/// Converts a 0..1 ratio into a star <see cref="GridLength"/> for building
/// proportional bars with two grid columns. ConverterParameter "rest" yields
/// the complementary width (1 - ratio); anything else yields the fill width.
/// </summary>
public sealed class StarRatioConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0.0,
        };

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        var isRest = string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase);
        var weight = isRest ? 1.0 - ratio : ratio;

        // A zero-weight star column collapses cleanly; guard against negative rounding.
        return new GridLength(Math.Max(weight, 0.0), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
