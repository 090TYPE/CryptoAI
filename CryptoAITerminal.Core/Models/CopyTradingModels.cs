using System;

namespace CryptoAITerminal.Core.Models;

/// <summary>A single executed trade published by a copy-trading leader.</summary>
public sealed record CopyTrade
{
    public string   Id          { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime ExecutedUtc { get; init; } = DateTime.UtcNow;
    public string   Exchange    { get; init; } = "";
    public string   Market      { get; init; } = "Spot";   // "Spot" | "Futures"
    public string   Symbol      { get; init; } = "";
    public string   Side        { get; init; } = "";       // "BUY" | "SELL"
    public decimal  Quantity    { get; init; }
    public decimal  Price       { get; init; }
    public string   Source      { get; init; } = "";       // "AIBot" | "GridBot" | "Sniper" | "Manual"
}

/// <summary>A follower's mirrored execution record.</summary>
public sealed class CopyExecution
{
    public string   LeaderTradeId { get; init; } = "";
    public DateTime ExecutedUtc   { get; init; } = DateTime.UtcNow;
    public string   Symbol        { get; init; } = "";
    public string   Side          { get; init; } = "";
    public decimal  Quantity      { get; init; }
    public decimal  Price         { get; init; }
    public bool     Success       { get; init; }
    public string   Error         { get; init; } = "";
}
