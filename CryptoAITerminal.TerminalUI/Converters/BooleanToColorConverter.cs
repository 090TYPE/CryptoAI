using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Converters;

/// <summary>true/false → зелёная/красная кисть из палитры приложения.</summary>
public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Своей пары «рост/падение» здесь больше нет: цвет берётся из тех же токенов,
        // что и у остальных знаковых значений.
        var key = value is bool b && b ? SemanticColor.Keys.Positive : SemanticColor.Keys.Negative;
        return StringToBrushConverter.Instance.Convert(key, targetType, parameter, culture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
