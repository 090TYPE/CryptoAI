using CryptoAITerminal.Core.Enums;

namespace CryptoAITerminal.Executor;

/// <summary>
/// Server-side best-execution selection, ported from the desktop BestExecutionRouterService's
/// single-venue path: compare each connected exchange's top quote on an effective (fee-adjusted)
/// basis and pick the cheapest venue to buy / the richest to sell. Order-book splitting for large
/// orders stays a desktop feature; the server routes the whole order to one best venue.
/// </summary>
public static class ServerBestExecution
{
    /// <summary>A venue's top-of-book price for the symbol and its taker fee (percent per side).</summary>
    public readonly record struct ExchangeQuote(string Exchange, decimal RawPrice, decimal FeeRatePct);

    /// <summary>The chosen venue and the per-unit price saving versus the worst available venue.</summary>
    public readonly record struct RoutingChoice(ExchangeQuote Best, decimal SavingsPerUnit);

    /// <summary>Fee-adjusted price actually paid (buy) or received (sell) at a venue.</summary>
    public static decimal EffectivePrice(OrderSide side, ExchangeQuote q)
        => side == OrderSide.Buy
            ? q.RawPrice * (1m + q.FeeRatePct / 100m)
            : q.RawPrice * (1m - q.FeeRatePct / 100m);

    /// <summary>Pick the best venue by effective price. Null when there are no usable quotes.</summary>
    public static RoutingChoice? PickBest(OrderSide side, IReadOnlyList<ExchangeQuote> quotes)
    {
        var usable = quotes.Where(q => q.RawPrice > 0m).ToList();
        if (usable.Count == 0) return null;

        // Buy → lowest effective price wins; Sell → highest.
        var ranked = usable.OrderBy(q => EffectivePrice(side, q)).ToList();
        var best = side == OrderSide.Buy ? ranked.First() : ranked.Last();
        var worst = side == OrderSide.Buy ? ranked.Last() : ranked.First();

        var savings = side == OrderSide.Buy
            ? EffectivePrice(side, worst) - EffectivePrice(side, best)
            : EffectivePrice(side, best) - EffectivePrice(side, worst);

        return new RoutingChoice(best, savings);
    }
}
