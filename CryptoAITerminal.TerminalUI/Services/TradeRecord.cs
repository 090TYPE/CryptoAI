using System;

namespace CryptoAITerminal.TerminalUI.Services;

public enum TradeSource { Bot, Sniper, Manual, Dex, Arb, Funding }
public enum TradeDirection { Long, Short }

/// <summary>
/// User-applied journal tag — visible in the Trade Journal tab and used for per-tag statistics.
/// </summary>
public enum JournalTag
{
    None      = 0,
    BotSignal = 1,
    Manual    = 2,
    Sniper    = 3,
    Scalp     = 4,
    Swing     = 5,
    Breakout  = 6,
    Breakdown = 7,
    Reversal  = 8,
}

public sealed class TradeRecord
{
    /// Stable unique key — used for deduplication when importing from sniper history.
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public TradeSource   Source    { get; set; }
    public TradeDirection Direction { get; set; }
    public string Symbol   { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;

    /// <summary>
    /// Specific bot name for grouping, e.g. "Grid Bot", "DCA Bot", "AI Bot",
    /// "CEX Arb", "Funding Arb". Null means unknown / manual.
    /// </summary>
    public string? BotName { get; set; }

    public DateTime OpenedAtUtc { get; set; }
    public DateTime ClosedAtUtc { get; set; }

    public decimal EntryPrice { get; set; }
    public decimal ExitPrice  { get; set; }
    public decimal Quantity   { get; set; }

    /// Net P&L in USD (positive = profit).
    public decimal PnlUsd     { get; set; }

    /// Net P&L as percentage of entry cost.
    public decimal PnlPercent { get; set; }

    public string     ExitReason { get; set; } = string.Empty;
    public string     Notes      { get; set; } = string.Empty;
    public JournalTag Tag        { get; set; } = JournalTag.None;

    // ── Computed helpers ──────────────────────────────────────────────────────

    public bool   IsWin  => PnlUsd > 0m;
    public string SourceLabel => Source.ToString();

    /// <summary>
    /// Extracts the base asset from the symbol, e.g. "BTCUSDT" → "BTC".
    /// Handles USDT, USDC, BTC, ETH quote currencies.
    /// </summary>
    public string Asset
    {
        get
        {
            if (string.IsNullOrEmpty(Symbol)) return "—";
            foreach (var quote in new[] { "USDT", "USDC", "BUSD", "BTC", "ETH", "BNB" })
                if (Symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                    return Symbol[..^quote.Length];
            // Handle OKX-style "BTC-USDT" or "BTC-USDT-SWAP"
            var parts = Symbol.Split('-');
            return parts[0];
        }
    }

    public string DurationLabel
    {
        get
        {
            var d = ClosedAtUtc - OpenedAtUtc;
            if (d.TotalDays  >= 1) return $"{d.TotalDays:0.0}d";
            if (d.TotalHours >= 1) return $"{d.TotalHours:0.0}h";
            return $"{Math.Max(0, d.TotalMinutes):0}m";
        }
    }
}
