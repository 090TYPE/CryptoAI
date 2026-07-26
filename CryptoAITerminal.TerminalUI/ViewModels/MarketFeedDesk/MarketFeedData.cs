using System;
using System.Globalization;
using Avalonia.Media;
using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

namespace CryptoAITerminal.TerminalUI.ViewModels.MarketFeedDesk;

/// <summary>
/// Palette, sentiment meta and formatters for the Market Feed desk.
/// No sample rows live here: every row the desk renders comes from a live view model.
/// </summary>
public static class MarketFeedData
{
    public static string Accent => BotsDeskData.Accent;
    public static string Green => BotsDeskData.Green;
    public static string Red => BotsDeskData.Red;
    public static string Amber => BotsDeskData.Amber;
    public const string Blue = "#58a6ff";
    public const string Violet = "#b48cff";
    public static string Text => BotsDeskData.Text;
    public static string Text3 => BotsDeskData.Text3;
    public const string Dim = BotsDeskData.Dim;
    public const string Dimmer = BotsDeskData.Dimmer;
    public const string Faint = BotsDeskData.Faint;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Alpha(string hex, string aa) => BotsDeskData.Alpha(hex, aa);

    public static (string label, string color, string bg, string border) Sent(string s) => s switch
    {
        "bullish" => ("BULLISH", Green, "#061615", "#14302e"),
        "bearish" => ("BEARISH", Red, "#14060a", "#3a1620"),
        _ => ("NEUTRAL", Text3, "#050f14", SemanticColor.Stroke),
    };

    // ── formatters ───────────────────────────────────────────────────────────

    /// <summary>Price with a decimal count that suits the magnitude.</summary>
    public static string Price(decimal p) => p <= 0 ? "—"
        : p >= 1000m ? p.ToString("N0", Inv)
        : p >= 1m ? p.ToString("N2", Inv)
        : p.ToString("N4", Inv);

    public static string Money0(decimal v) => "$" + Math.Round(v).ToString("N0", Inv);

    /// <summary>Compact USD notional ($1.2B / $340M / $12K).</summary>
    public static string Compact(decimal v) =>
          v >= 1_000_000_000m ? "$" + (v / 1_000_000_000m).ToString("N1", Inv) + "B"
        : v >= 1_000_000m ? "$" + (v / 1_000_000m).ToString("N1", Inv) + "M"
        : v >= 1_000m ? "$" + (v / 1_000m).ToString("N0", Inv) + "K"
        : "$" + v.ToString("N0", Inv);

    public static string Pct(double v, int d = 2) => v.ToString("F" + d, Inv) + "%";

    public static string Trunc(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    /// <summary>"#AARRGGBB" for the StringToBrush converter (heat bands arrive as brushes).</summary>
    public static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static string Hex(IBrush? brush, string fallback) =>
        brush is ISolidColorBrush scb ? Hex(scb.Color) : fallback;
}
