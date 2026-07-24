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

    /// <summary>A limit order the grid wants placed. Side null = no order (grid edge).</summary>
    public readonly record struct GridOrder(string? Side, decimal Price);

    /// <summary>What to do after a fill: the opposite order to place (Side null at the grid edge)
    /// and whether a full buy→sell cycle just closed (for PnL accounting).</summary>
    public readonly record struct GridFillResult(GridOrder Order, bool CycleClosed);

    /// <summary>Reaction to a filled grid order. Ported from GridBot.PollFills.</summary>
    public static GridFillResult OnFill(decimal[] levels, string filledSide, int filledLevel)
    {
        // Filled buy at i → place sell at i+1 (take profit one level up).
        if (string.Equals(filledSide, "buy", StringComparison.OrdinalIgnoreCase))
        {
            int up = filledLevel + 1;
            return up < levels.Length
                ? new GridFillResult(new GridOrder("sell", levels[up]), CycleClosed: false)
                : new GridFillResult(new GridOrder(null, 0m), false);
        }

        // Filled sell at i → place buy at i-1 (re-arm lower); a full cycle just closed.
        int down = filledLevel - 1;
        return down >= 0
            ? new GridFillResult(new GridOrder("buy", levels[down]), CycleClosed: true)
            : new GridFillResult(new GridOrder(null, 0m), false);
    }

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
