using System;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.Base;

/// <summary>
/// Pure arithmetic for snapping an order onto a venue's grid of allowed prices and quantities.
/// Deliberately free of any SDK type so bots, routers and gateways all round the same way — the
/// old <c>Math.Round(x, 6)</c> scattered through copy-trading and funding arbitrage rounded *up*
/// half the time, which is the one direction that gets an order rejected for exceeding balance.
///
/// A step or tick of zero means "no rule published"; every method then returns the value untouched
/// rather than collapsing it to zero.
/// </summary>
public static class SymbolFilterMath
{
    /// <summary>
    /// Largest multiple of <paramref name="step"/> that is ≤ <paramref name="value"/>.
    /// Quantities always round DOWN: rounding up spends money the account may not have.
    /// </summary>
    public static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0m) return value;
        if (value <= 0m) return 0m;
        // decimal division is base-10 exact for the powers-of-ten steps venues publish, so
        // 0.3 / 0.1 is 3 here and not 2.9999… as it would be in double.
        return decimal.Floor(value / step) * step;
    }

    /// <summary>
    /// Smallest multiple of <paramref name="step"/> that is ≥ <paramref name="value"/>. Needed when
    /// a quantity has to be raised to clear <see cref="SymbolFilters.MinQuantity"/> or the notional
    /// floor, where flooring would leave it just below the limit forever.
    /// </summary>
    public static decimal CeilToStep(decimal value, decimal step)
    {
        if (step <= 0m) return value;
        if (value <= 0m) return 0m;
        return decimal.Ceiling(value / step) * step;
    }

    /// <summary>
    /// Snaps a price onto the tick grid. <paramref name="up"/> chooses the direction: a resting buy
    /// rounds down and a resting sell rounds up, so the order never crosses further into the book
    /// than the caller intended.
    /// </summary>
    public static decimal RoundToTick(decimal price, decimal tick, bool up)
    {
        if (tick <= 0m) return price;
        var ticks = price / tick;
        return (up ? decimal.Ceiling(ticks) : decimal.Floor(ticks)) * tick;
    }

    /// <summary>Order value clears the venue's floor. A zero floor means the venue publishes none.</summary>
    public static bool MeetsMinNotional(decimal price, decimal quantity, decimal minNotional)
        => minNotional <= 0m || price * quantity >= minNotional;

    /// <summary>
    /// Order value clears the venue's floor. Null filters (the gateway could not tell) pass —
    /// the venue stays the final judge.
    /// </summary>
    public static bool MeetsMinNotional(decimal price, decimal quantity, SymbolFilters? filters)
        => filters is null || MeetsMinNotional(price, quantity, filters.MinNotional);

    /// <summary>
    /// Price snapped to <see cref="SymbolFilters.TickSize"/>, or unchanged when the gateway could
    /// not resolve filters.
    /// </summary>
    public static decimal NormalizePrice(decimal price, SymbolFilters? filters, bool up)
        => filters is null ? price : RoundToTick(price, filters.TickSize, up);

    /// <summary>
    /// Quantity floored to <see cref="SymbolFilters.StepSize"/>, then zeroed if it fell below
    /// <see cref="SymbolFilters.MinQuantity"/> — a quantity under the minimum is not a smaller
    /// order, it is a rejected one, and the caller has to see that as "cannot trade this size".
    /// </summary>
    public static decimal NormalizeQuantity(decimal quantity, SymbolFilters? filters)
    {
        if (filters is null) return quantity;
        var snapped = FloorToStep(quantity, filters.StepSize);
        return filters.MinQuantity > 0m && snapped < filters.MinQuantity ? 0m : snapped;
    }
}
