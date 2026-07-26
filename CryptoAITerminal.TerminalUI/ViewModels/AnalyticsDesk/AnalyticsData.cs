using System;
using System.Globalization;
using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

namespace CryptoAITerminal.TerminalUI.ViewModels.AnalyticsDesk;

/// <summary>Palette and formatters for the Analytics desk. No seed data.</summary>
public static class AnalyticsData
{
    public static string Accent => BotsDeskData.Accent;
    public static string Green => BotsDeskData.Green;
    public static string Red => BotsDeskData.Red;
    public static string Amber => BotsDeskData.Amber;
    public const string Violet = "#b48cff";
    public const string Blue = "#58a6ff";
    public static string Text => BotsDeskData.Text;
    public static string Text3 => BotsDeskData.Text3;
    public const string Dim = BotsDeskData.Dim;
    public const string Dimmer = BotsDeskData.Dimmer;
    public const string Faint = BotsDeskData.Faint;

    /// <summary>Shown wherever the live store has nothing to report.</summary>
    public const string Empty = "—";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Minus = "−";

    public static string Alpha(string hex, string aa) => BotsDeskData.Alpha(hex, aa);

    public static string Money(double v, bool sign = false)
    {
        var a = Math.Abs(v);
        string s = a >= 1000 ? a.ToString("#,##0", Inv) : a.ToString(a < 100 ? "0.00" : "0.0", Inv);
        return (sign && v > 0 ? "+" : v < 0 ? Minus : "") + "$" + s;
    }
    public static string Sgn(double v) => v > 0 ? Green : v < 0 ? Red : Text3;
    public static string Pct(double v) => (v > 0 ? "+" : Minus) + Math.Abs(v).ToString("0.00", Inv) + "%";

    /// <summary>Period chips. The id is the literal PnlDashboardViewModel.SelectedPeriod value.</summary>
    public static readonly (string id, string label)[] Periods =
        { ("Today", "TODAY"), ("Week", "WEEK"), ("Month", "MONTH"), ("Year", "YEAR"), ("All", "ALL") };
}
