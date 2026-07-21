namespace CryptoAITerminal.Executor;

/// <summary>
/// Server-side grid math, ported from the desktop GridBot. Pure functions — level generation and
/// fee-aware cycle profit (commission on both sides, matching the BUG-10 fix). Order placement and
/// fill reconciliation build on top of these (Phase 4.2 wiring).
/// </summary>
public static class ServerGridStrategy
{
    /// <summary>Evenly-spaced price levels between lower and upper, inclusive. Returns gridLevels+1 prices.</summary>
    public static decimal[] GenerateLevels(decimal lower, decimal upper, int gridLevels)
    {
        if (upper <= lower) throw new ArgumentException("upper must be greater than lower");
        if (gridLevels <= 0) throw new ArgumentException("gridLevels must be positive");

        var spacing = (upper - lower) / gridLevels;
        var prices = new decimal[gridLevels + 1];
        for (int i = 0; i <= gridLevels; i++)
            prices[i] = lower + spacing * i;
        return prices;
    }

    /// <summary>Profit of one buy→sell cycle net of commission charged on both sides.</summary>
    public static decimal CycleProfit(decimal buyPrice, decimal sellPrice, decimal qty, decimal feePerSide)
    {
        var commission = (buyPrice + sellPrice) * qty * feePerSide;
        return (sellPrice - buyPrice) * qty - commission;
    }

    /// <summary>A limit order the grid wants placed at startup.</summary>
    public readonly record struct GridOrder(string Side, decimal Price);

    /// <summary>
    /// Initial grid orders for a price. Each cell fully below price → limit buy at its bottom;
    /// each cell fully above price → limit sell at its top, but only on futures (short entry).
    /// Spot places buys only. Ported from GridBot.PlaceInitialOrders.
    /// </summary>
    public static IReadOnlyList<GridOrder> InitialOrders(decimal[] levels, decimal currentPrice, bool futures)
    {
        var orders = new List<GridOrder>();
        for (int i = 0; i < levels.Length - 1; i++)
        {
            decimal bottom = levels[i], top = levels[i + 1];
            if (top <= currentPrice)
                orders.Add(new GridOrder("buy", bottom));
            else if (bottom >= currentPrice && futures)
                orders.Add(new GridOrder("sell", top));
        }
        return orders;
    }
}
