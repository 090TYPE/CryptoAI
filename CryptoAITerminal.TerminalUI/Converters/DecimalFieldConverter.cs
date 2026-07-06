using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>
/// Two-way decimal↔string converter for the trading ticket's inline price fields
/// (limit price, TP, SL, slippage). Displays a clean value with no trailing zeros
/// or thousand separators, and parses user input tolerantly (accepts comma or dot,
/// ignores spaces) so a formatted string never round-trips into an InvalidCastException.
/// </summary>
public sealed class DecimalFieldConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            decimal d => d.ToString("0.####", CultureInfo.InvariantCulture),
            double db => db.ToString("0.####", CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return 0m;
        }

        text = text.Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(",", ".");
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : BindingOperations.DoNothing;
    }
}
