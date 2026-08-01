using System;
using System.Globalization;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Bots;

/// <summary>
/// Вкладка «Боты» считала «использовано бюджета» у DCA, разбирая обратно печатный вид суммы.
/// Печатается он ТЕКУЩЕЙ культурой (<c>$"{e.TotalUsdt:N2}"</c>), а разбирался инвариантной с
/// <c>NumberStyles.Any</c>. На локали с запятой в роли десятичного разделителя — русской, немецкой,
/// французской — «123,45» инвариантно читается как сто двадцать три тысячи сорок пять: запятая
/// становится разделителем разрядов. Полоса «использовано» показывала стократное завышение, а
/// суммы с разделителем разрядов не читались вообще и полоса пропадала.
///
/// Тест бежит под русской культурой намеренно: это и есть боевая локаль владельца.
/// </summary>
public class DcaExecutionRowLocaleTests
{
    /// <summary>Выполняет действие под заданной культурой и возвращает всё как было.</summary>
    private static T Under<T>(string culture, Func<T> body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            return body();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static DcaExecution Buy(decimal total) => new()
    {
        Symbol = "BTCUSDT",
        ExecutedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
        Price = 100m,
        Quantity = total / 100m,
        TotalUsdt = total,
        Executed = true,
        Reason = "Scheduled",
    };

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    public void The_numeric_total_survives_every_locale(string culture)
    {
        var row = Under(culture, () => new DcaExecutionRowViewModel(Buy(123.45m)));

        Assert.Equal(123.45m, row.TotalUsdt);
    }

    [Fact]
    public void The_printed_total_stays_localised_for_the_human()
    {
        // Число — для расчётов, текст — для человека; ломать вывод правка не должна.
        var ru = Under("ru-RU", () => new DcaExecutionRowViewModel(Buy(123.45m)).TotalLabel);
        var us = Under("en-US", () => new DcaExecutionRowViewModel(Buy(123.45m)).TotalLabel);

        Assert.Contains("123", ru);
        Assert.Contains("123", us);
        Assert.Contains("45", ru);
    }

    [Fact]
    public void The_old_round_trip_really_was_off_by_a_hundred()
    {
        // Фиксируем сам механизм ошибки, чтобы никто не вернул разбор печатного вида обратно.
        var label = Under("ru-RU", () => new DcaExecutionRowViewModel(Buy(123.45m)).TotalLabel);

        Assert.True(double.TryParse(label, NumberStyles.Any, CultureInfo.InvariantCulture, out var wrong));
        Assert.Equal(12345d, wrong);           // именно так и считалось раньше
        Assert.NotEqual(123.45d, wrong);
    }

    [Fact]
    public void A_thousands_separated_total_used_to_vanish_entirely()
    {
        // 12 345.67 на русской локали печатается с неразрывным пробелом — инвариантный разбор
        // проваливался, сумма не попадала в итог, и полоса «использовано» показывала пустоту.
        var label = Under("ru-RU", () => new DcaExecutionRowViewModel(Buy(12_345.67m)).TotalLabel);
        var row = Under("ru-RU", () => new DcaExecutionRowViewModel(Buy(12_345.67m)));

        Assert.False(double.TryParse(label, NumberStyles.Any, CultureInfo.InvariantCulture, out _));
        Assert.Equal(12_345.67m, row.TotalUsdt);
    }

    [Fact]
    public void A_skipped_row_carries_no_amount()
    {
        var skipped = new DcaExecution
        {
            Symbol = "BTCUSDT", ExecutedAt = DateTime.UtcNow, Price = 100m,
            Quantity = 0, TotalUsdt = 0, Executed = false, Reason = "Price above MA",
        };

        var row = new DcaExecutionRowViewModel(skipped);

        Assert.Equal(0m, row.TotalUsdt);
        Assert.Equal("—", row.TotalLabel);
    }
}
