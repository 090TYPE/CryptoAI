using System;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Persisted order preset — fills the trading form when applied.
/// Slot 1–9 maps to Shift+1 … Shift+9 hotkeys.
/// </summary>
public sealed class OrderTemplate
{
    public Guid    Id             { get; init; } = Guid.NewGuid();
    /// <summary>Hotkey slot 1–9 (Shift+N).</summary>
    public int     Slot           { get; set; }  = 1;
    public string  Name           { get; set; }  = "My Template";
    public string  Symbol         { get; set; }  = "BTCUSDT";
    public string  Side           { get; set; }  = "BUY";
    public string  OrderType      { get; set; }  = "Limit";
    public decimal Quantity       { get; set; }  = 0.001m;
    /// <summary>
    /// Limit-price offset as a percentage relative to the current market price.
    /// Negative = below (typical for BUY limit), positive = above.
    /// </summary>
    public decimal LimitOffsetPct { get; set; }  = -0.10m;
    /// <summary>Take-profit distance from entry in %.</summary>
    public decimal TakeProfitPct  { get; set; }  = 3.0m;
    /// <summary>Stop-loss distance from entry in %.</summary>
    public decimal StopLossPct    { get; set; }  = 1.5m;
}
