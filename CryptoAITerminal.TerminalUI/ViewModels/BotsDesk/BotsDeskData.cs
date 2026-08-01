using System;
using System.Globalization;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

/// <summary>
/// Palette, type/status metadata and formatting helpers for the Bots desk.
/// Contains no data: every row and every number the desk shows comes from the
/// live engine view-models the shell owns (see <c>BotsDeskViewModel.Attach</c>).
/// </summary>
public static class BotsDeskData
{
    // ── palette ─────────────────────────────────────────────────────────────
    // Смысловые роли берутся из палитры приложения (Styles/AppStyles.axaml) через
    // SemanticColor — здесь остаются только оттенки, для которых токена нет.
    public static string Accent => SemanticColor.Accent;
    public static string Green => SemanticColor.Positive;
    public static string Red => SemanticColor.Negative;
    public static string Amber => SemanticColor.Warning;
    public static string Text => SemanticColor.Primary;
    public const string Text2 = "#c8dcef";
    public static string Text3 => SemanticColor.Muted;
    public const string Dim = "#4a6a82";
    public const string Dimmer = "#3d5a72";
    public const string Faint = "#2d4a5e";
    public const string Ghost = "#1e3048";

    /// <summary>Rendered wherever the app has no source for a metric.</summary>
    public const string Dash = "—";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── engine types (one per real bot engine in the app) ───────────────────
    public static (string c, string name) TypeMeta(string t) => t switch
    {
        "GRID" => (Accent, "Grid bot"),
        "DCA" => ("#58a6ff", "DCA bot"),
        "RULE" => (Amber, "Rule bot"),
        "AI" => ("#b48cff", "AI trader"),
        "AGENT" => ("#ff8fa3", "Autonomous agent"),
        "TRAIL" => ("#7dd3fc", "Trailing stop"),
        _ => (Accent, "Bot")
    };

    public static readonly string[] Types = { "GRID", "DCA", "RULE", "AI", "AGENT", "TRAIL" };

    /// <summary>The engines expose running / paused / stopped only — there is no error state.</summary>
    public static (string label, string color) StatusMeta(string s) => s switch
    {
        "running" => ("RUNNING", Green),
        "paused" => ("PAUSED", Amber),
        "stopped" => ("STOPPED", Dimmer),
        _ => (Dash, Dimmer)
    };

    // key, label
    public static readonly (string key, string label)[] ColumnDefs =
    {
        ("venue", "Venue · market"), ("mode", "Paper / live"), ("status", "Status"), ("pnl24", "PnL 24h"),
        ("pnlTotal", "PnL total"), ("spark", "Equity 7d"), ("trades", "Fills · winrate"),
        ("alloc", "Budget · used"), ("dd", "Max drawdown"), ("uptime", "Uptime")
    };

    // key, label, icon, color
    public static readonly (string key, string label, string icon, string color)[] ActionDefs =
    {
        ("start", "Start", "▶", Green), ("pause", "Pause", "❙❙", Amber), ("stop", "Stop", "■", Dimmer),
        ("runNow", "Run once", "⚡", Accent), ("kill", "Kill-switch", "⛔", Red),
        ("edit", "Parameters", "✎", Text3), ("logs", "Logs", "≡", Text3),
        ("trades", "Fills", "⌁", Accent), ("risk", "Risk", "🛡", Text3)
    };

    // ── formatting ──────────────────────────────────────────────────────────
    private const string Minus = "−";

    public static string Money(double v, bool sign = false)
    {
        var a = Math.Abs(v);
        string s = a >= 1000
            ? a.ToString("#,##0", Inv)
            : a.ToString(a < 10 ? "0.00" : "0.0", Inv);
        var prefix = sign && v > 0 ? "+" : v < 0 ? Minus : "";
        return prefix + "$" + s;
    }

    public static string Pct(double v)
        => (v > 0 ? "+" : v < 0 ? Minus : "") + Math.Abs(v).ToString("0.00", Inv) + "%";

    public static string Sgn(double v) => v > 0 ? Green : v < 0 ? Red : Text3;

    /// <summary>"#RRGGBB" + two-hex alpha → Avalonia "#AARRGGBB" (alpha first).</summary>
    public static string Alpha(string hex, string aa) => SemanticColor.Alpha(hex, aa);

    public static string Digits(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());

    public static string Decimalish(string? s)
        => new string((s ?? "").Where(c => char.IsDigit(c) || c == '.').ToArray());

    /// <summary>Elapsed time as "19d 4h" / "3h 12m" / "48s".</summary>
    public static string Elapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h";
        if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
        if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + "m " + t.Seconds + "s";
        return (int)t.TotalSeconds + "s";
    }

    // ── engine catalogue (wizard cards / empty state) ───────────────────────
    // These are the six engines the app actually ships. No backtest or APR
    // numbers: the app has no per-engine backtest archive to read them from.
    // id, type, name, desc
    public static readonly (string id, string type, string name, string desc)[] Templates =
    {
        ("grid", "GRID", "Grid bot", "Ladder of buy/sell orders inside a price range on Binance spot or USD-M futures."),
        ("dca", "DCA", "DCA bot", "Weighted spot basket bought on a fixed schedule, with an optional MA filter per coin."),
        // Ни здесь, ни ниже вендор не назван: обслуживает то семейство, которое выбрал пользователь,
        // и слово "Claude" в карточке движка было верным ровно для половины пользователей.
        ("rule", "RULE", "Rule bot", "Indicator strategy engine: MA cross, RSI, Bollinger, Breakout, MACD, VWAP or AI."),
        ("ai", "AI", "AI trader", "The model drives a tool-use loop and places its own orders inside hard caps. Paper by default."),
        ("agent", "AGENT", "Autonomous agent", "Runs one agent turn per interval through the action guard: allowlist, trade and budget caps."),
        ("trail", "TRAIL", "Trailing stop", "Ratchets a stop level on every tick: %, ATR, Chandelier, break-even or swing low."),
    };
}
