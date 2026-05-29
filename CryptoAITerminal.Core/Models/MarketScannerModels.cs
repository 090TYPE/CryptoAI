using System;
using System.Collections.Generic;

namespace CryptoAITerminal.Core.Models;

// ════════════════════════════════════════════════════════════════════════════
//  Multi-Market Scanner — domain models
// ════════════════════════════════════════════════════════════════════════════

public enum AlertSeverity { Info, Warning, Hot }

/// <summary>Preset filter IDs for the scanner UI.</summary>
public enum ScanPreset
{
    All,
    Gainers,
    Losers,
    Oversold,
    Overbought,
    Hot,
    Custom,
}

/// <summary>
/// Configurable filter applied to every symbol on each scan tick.
/// All thresholds are optional — null means "don't filter on this criterion".
/// </summary>
public sealed class ScannerFilter
{
    /// <summary>Symbol must contain this substring (case-insensitive). Null = all.</summary>
    public string?  SymbolContains    { get; set; }
    /// <summary>24h change ≥ X %.</summary>
    public decimal? MinChangePct      { get; set; }
    /// <summary>24h change ≤ X %.</summary>
    public decimal? MaxChangePct      { get; set; }
    /// <summary>RSI(14) ≥ X.</summary>
    public decimal? MinRsi            { get; set; }
    /// <summary>RSI(14) ≤ X.</summary>
    public decimal? MaxRsi            { get; set; }
    /// <summary>24h USDT volume ≥ X.</summary>
    public decimal? MinVolume24hUsd   { get; set; }
    /// <summary>Activity score (ticks/min in the last 60 s) ≥ X.</summary>
    public decimal? MinActivityScore  { get; set; }

    public bool Matches(ScanResult r)
    {
        if (SymbolContains   != null && !r.Symbol.Contains(SymbolContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (MinChangePct     != null && r.ChangePct24h < MinChangePct)   return false;
        if (MaxChangePct     != null && r.ChangePct24h > MaxChangePct)   return false;
        if (MinRsi           != null && r.Rsi14        < MinRsi)         return false;
        if (MaxRsi           != null && r.Rsi14        > MaxRsi)         return false;
        if (MinVolume24hUsd  != null && r.Volume24hUsd < MinVolume24hUsd) return false;
        if (MinActivityScore != null && r.ActivityScore< MinActivityScore) return false;
        return true;
    }
}

/// <summary>Live scanner result for one symbol.</summary>
public sealed class ScanResult
{
    public string  Symbol         { get; set; } = "";
    /// <summary>Exchange the price came from ("Binance" | "Bybit" | "OKX").</summary>
    public string  Exchange       { get; set; } = "";
    public decimal LastPrice      { get; set; }
    public decimal High24h        { get; set; }
    public decimal Low24h         { get; set; }
    /// <summary>24-hour price change in percent (from ticker stream).</summary>
    public decimal ChangePct24h   { get; set; }
    /// <summary>24-hour USDT volume.</summary>
    public decimal Volume24hUsd   { get; set; }
    /// <summary>RSI(14) computed from rolling 10-second price snapshots.</summary>
    public decimal Rsi14          { get; set; }
    /// <summary>Ticks received in the last 60 seconds (proxy for activity).</summary>
    public decimal ActivityScore  { get; set; }
    /// <summary>True when any alert condition is active for this symbol.</summary>
    public bool    IsHot          { get; set; }
    public DateTime UpdatedAt     { get; set; }
}

/// <summary>
/// A user-defined price level that triggers an alert when price crosses it.
/// </summary>
public sealed class PriceLevel
{
    public Guid    Id           { get; init; } = Guid.NewGuid();
    public string  Symbol       { get; set; } = "";
    public decimal Price        { get; set; }
    /// <summary>True = resistance; False = support.</summary>
    public bool    IsResistance { get; set; }
    public string  Note         { get; set; } = "";
    public bool    Triggered    { get; set; }
    public DateTime? TriggeredAt { get; set; }
}

/// <summary>Fired when a scanner condition is met.</summary>
public sealed class ScannerAlert
{
    public Guid         Id          { get; init; } = Guid.NewGuid();
    public string       Symbol      { get; set; } = "";
    public string       Exchange    { get; set; } = "";
    public AlertSeverity Severity   { get; set; }
    public string       Message     { get; set; } = "";
    public DateTime     TriggeredAt { get; set; }
}
