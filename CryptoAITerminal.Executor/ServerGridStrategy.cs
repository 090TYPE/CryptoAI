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
}
