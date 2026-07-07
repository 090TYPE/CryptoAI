using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>
/// Returns <c>true</c> when the bound string equals the <c>ConverterParameter</c>
/// (case-insensitive). Used to drive the <c>active</c> style pseudo-class on the
/// trading desk's segmented buttons (timeframe, chart type, order type, …) so the
/// active look lives entirely in styles instead of per-key brush properties.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
