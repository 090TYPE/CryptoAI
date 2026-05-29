using CryptoAITerminal.Core.Enums;

namespace CryptoAITerminal.Core.Models;

public class FuturesPosition
{
    public string Symbol { get; set; } = string.Empty;
    public FuturesPositionSide PositionSide { get; set; } = FuturesPositionSide.Both;
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal LiquidationPrice { get; set; }
    public int Leverage { get; set; }
    public FuturesMarginMode MarginMode { get; set; } = FuturesMarginMode.Cross;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
