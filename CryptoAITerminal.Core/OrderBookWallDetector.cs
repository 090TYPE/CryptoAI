using System;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Core;

/// <summary>Pure rules deciding which order-book levels are "walls".</summary>
public static class OrderBookWallDetector
{
    public static bool IsLarge(decimal price, decimal qty, WallHighlightMode mode, decimal usdThreshold, decimal qtyThreshold) =>
        mode switch
        {
            WallHighlightMode.Usd => usdThreshold > 0m && price * qty >= usdThreshold,
            WallHighlightMode.Qty => qtyThreshold > 0m && qty >= qtyThreshold,
            _ => false
        };

    /// <summary>Relative size 0..1 used for line thickness/opacity.</summary>
    public static double Intensity(decimal notional, decimal maxNotional)
    {
        if (maxNotional <= 0m) return 0d;
        return Math.Clamp((double)(notional / maxNotional), 0d, 1d);
    }
}
