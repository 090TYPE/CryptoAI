using System;
using System.Collections.Generic;

namespace CryptoAITerminal.Core.Trading;

/// <summary>One synthetic order-book level for the paper PERP desk.</summary>
public sealed record DexPerpBookLevel(decimal Price, decimal Size, decimal Cumulative);

/// <summary>
/// Seeded mark-price + funding + order-book generator for the paper PERP desk. The
/// mark takes a bounded random walk anchored to the selected token's real spot price
/// so it always stays positive and within a sane band; funding tracks the mark's
/// premium/discount to the anchor. Deterministic for a given seed so it is testable.
/// </summary>
public sealed class DexPerpMarketSimulator
{
    private readonly Random _rng;
    private readonly decimal _anchor;
    private readonly decimal _volatility;
    private readonly decimal _bandLow;
    private readonly decimal _bandHigh;

    public DexPerpMarketSimulator(decimal anchorPrice, decimal volatility = 0.004m, decimal bandPercent = 0.10m, int seed = 12345)
    {
        _anchor = anchorPrice > 0m ? anchorPrice : 1m;
        _volatility = Math.Max(0.0001m, volatility);
        _bandLow = _anchor * (1m - bandPercent);
        _bandHigh = _anchor * (1m + bandPercent);
        _rng = new Random(seed);
        Mark = _anchor;
        FundingRate = 0m;
    }

    public decimal Mark { get; private set; }

    /// <summary>Per-interval funding rate (fraction of notional), bounded ±1%.</summary>
    public decimal FundingRate { get; private set; }

    /// <summary>Anchor spot price the mark is tethered to.</summary>
    public decimal Anchor => _anchor;

    /// <summary>Re-anchor when the user selects a different token (keeps the band fresh).</summary>
    public void Reanchor(decimal anchorPrice)
    {
        if (anchorPrice <= 0m)
        {
            return;
        }

        Mark = anchorPrice;
    }

    /// <summary>Advance one step and return the new mark price.</summary>
    public decimal NextMark()
    {
        var drift = (decimal)(_rng.NextDouble() - 0.5) * 2m * _volatility;
        var next = Mark * (1m + drift);
        Mark = Math.Clamp(next, _bandLow, _bandHigh);

        // Funding tracks the mark's premium to the anchor, damped and bounded.
        var premium = (Mark - _anchor) / _anchor;
        FundingRate = Math.Clamp(premium * 0.1m, -0.01m, 0.01m);
        return Mark;
    }

    /// <summary>Build a synthetic L2 book around the current mark: <paramref name="depth"/>
    /// asks (ascending) then <paramref name="depth"/> bids (descending), each with a
    /// running cumulative size.</summary>
    public (IReadOnlyList<DexPerpBookLevel> Asks, IReadOnlyList<DexPerpBookLevel> Bids) BuildOrderBook(int depth = 12)
    {
        depth = Math.Max(1, depth);
        var tick = Math.Max(_anchor * 0.0002m, 0.00000001m);

        var asks = new List<DexPerpBookLevel>(depth);
        var bids = new List<DexPerpBookLevel>(depth);

        decimal askCum = 0m, bidCum = 0m;
        for (var i = 1; i <= depth; i++)
        {
            var size = 0.5m + (decimal)_rng.NextDouble() * 4m;

            askCum += size;
            asks.Add(new DexPerpBookLevel(Mark + tick * i, size, askCum));

            bidCum += size;
            bids.Add(new DexPerpBookLevel(Mark - tick * i, size, bidCum));
        }

        return (asks, bids);
    }
}
