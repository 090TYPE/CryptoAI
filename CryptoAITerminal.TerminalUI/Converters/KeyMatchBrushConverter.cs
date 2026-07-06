using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>
/// Returns <see cref="ActiveBrush"/> when the bound selection string equals the
/// element's key (passed as ConverterParameter), otherwise <see cref="InactiveBrush"/>.
/// Used to light up the active filter pill / sort option / add-mode segment /
/// timeframe button in the Markets board without a bespoke brush property per key.
/// </summary>
public sealed class KeyMatchBrushConverter : IValueConverter
{
    public IBrush ActiveBrush { get; set; } = Brushes.Aqua;
    public IBrush InactiveBrush { get; set; } = Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? ActiveBrush
            : InactiveBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
