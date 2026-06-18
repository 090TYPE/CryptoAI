namespace CryptoAITerminal.Core.Models;

/// <summary>How the "large order" threshold is interpreted.</summary>
public enum WallHighlightMode
{
    Usd,
    Qty
}

/// <summary>Per-symbol highlight thresholds. A threshold of 0 means "off" for that mode.</summary>
public sealed class BookWallSettings
{
    public decimal UsdThreshold { get; set; } = 250_000m;
    public decimal QtyThreshold { get; set; } = 0m;
}

/// <summary>One large resting order projected onto the price chart.</summary>
public readonly record struct BookWall(decimal Price, bool IsBid, double Intensity, decimal Notional, decimal Quantity);
