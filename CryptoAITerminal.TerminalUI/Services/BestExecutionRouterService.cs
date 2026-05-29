using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Computes the cheapest routing plan across Binance / Bybit / OKX for a given order,
/// fetching live order-book depth to simulate accurate fill prices.
/// For large orders it splits the quantity across exchanges to minimise market impact.
/// </summary>
public sealed class BestExecutionRouterService
{
    // ── Exchange gateways ─────────────────────────────────────────────────────

    private readonly Dictionary<string, IExchangeGateway> _gateways;

    internal static readonly string[] ExchangeNames = ["Binance", "Bybit", "OKX"];

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>Taker fee per leg (%). Default 0.10%.</summary>
    public decimal TakerFeePct { get; set; } = 0.10m;

    /// <summary>
    /// Orders above this USD threshold are eligible for cross-exchange splitting.
    /// Default $5 000.
    /// </summary>
    public decimal SplitThresholdUsd { get; set; } = 5_000m;

    /// <summary>Order-book depth to fetch per exchange. Default 10.</summary>
    public int BookDepth { get; set; } = 10;

    // ── Constructor ───────────────────────────────────────────────────────────

    public BestExecutionRouterService(
        IExchangeGateway? binance,
        IExchangeGateway? bybit,
        IExchangeGateway? okx)
    {
        _gateways = new(StringComparer.OrdinalIgnoreCase);
        if (binance != null) _gateways["Binance"] = binance;
        if (bybit   != null) _gateways["Bybit"]   = bybit;
        if (okx     != null) _gateways["OKX"]     = okx;
    }

    // ── Routing computation ───────────────────────────────────────────────────

    /// <summary>
    /// Fetches order-book snapshots from all exchanges in parallel, then
    /// computes the optimal routing (single exchange or split).
    /// </summary>
    /// <param name="symbol">Standard symbol, e.g. "BTCUSDT".</param>
    /// <param name="side">Buy or Sell.</param>
    /// <param name="notionalUsd">USD notional size of the order.</param>
    public async Task<RoutingPlan> ComputeRoutingAsync(
        string symbol, OrderSide side, decimal notionalUsd, CancellationToken ct = default)
    {
        if (_gateways.Count == 0 || notionalUsd <= 0)
            return EmptyPlan(symbol, side, notionalUsd);

        // ── Fetch order books in parallel ────────────────────────────────────
        var bookTasks = _gateways
            .Select(kv => FetchBookAsync(kv.Key, kv.Value, symbol, ct))
            .ToArray();
        var books = await Task.WhenAll(bookTasks);

        // ── Build per-exchange quotes ─────────────────────────────────────────
        var quotes = new List<ExchangeQuote>(books.Length);
        foreach (var (exch, book) in books)
        {
            var levels = side == OrderSide.Buy ? book?.Asks : book?.Bids;
            if (levels == null || levels.Count == 0)
            {
                quotes.Add(new ExchangeQuote { Exchange = exch, HasData = false });
                continue;
            }

            var topPrice    = levels[0].Price;
            var totalAvail  = levels.Sum(l => l.Quantity);
            var fee         = TakerFeePct;
            var effPrice    = side == OrderSide.Buy
                ? topPrice * (1 + fee / 100m)
                : topPrice * (1 - fee / 100m);

            quotes.Add(new ExchangeQuote
            {
                Exchange       = exch,
                RawPrice       = topPrice,
                FeeRatePct     = fee,
                EffectivePrice = Math.Round(effPrice, 8),
                AvailableQty   = totalAvail,
                HasData        = true,
            });
        }

        var validQuotes = quotes.Where(q => q.HasData).ToList();
        if (validQuotes.Count == 0)
            return EmptyPlan(symbol, side, notionalUsd);

        // ── Estimate total coin quantity from best quote ───────────────────────
        var bestRawPrice = side == OrderSide.Buy
            ? validQuotes.Min(q => q.RawPrice)
            : validQuotes.Max(q => q.RawPrice);
        if (bestRawPrice <= 0) return EmptyPlan(symbol, side, notionalUsd);

        var totalQty = Math.Round(notionalUsd / bestRawPrice, 8);
        if (totalQty <= 0) return EmptyPlan(symbol, side, notionalUsd);

        // ── Choose routing strategy ───────────────────────────────────────────
        IReadOnlyList<RoutingLeg> legs;

        if (notionalUsd < SplitThresholdUsd || validQuotes.Count == 1)
        {
            // Single best exchange
            legs = RouteSingle(side, totalQty, validQuotes);
        }
        else
        {
            // Merge all order-book levels and fill greedily across exchanges
            legs = RouteSplit(side, totalQty, books.Where(b => b.book != null).ToArray());
        }

        if (legs.Count == 0) return EmptyPlan(symbol, side, notionalUsd);

        // ── Compute summary stats ─────────────────────────────────────────────
        var totalValue   = legs.Sum(l => l.ValueUsd);

        // Weighted avg fill price (before fee)
        var totalCoins   = legs.Sum(l => l.Quantity);
        var wavg         = totalCoins > 0
            ? Math.Round(legs.Sum(l => l.AvgFillPrice * l.Quantity) / totalCoins, 4)
            : 0m;

        // Worst-case: all on worst exchange
        var worstEff = side == OrderSide.Buy
            ? validQuotes.Max(q => q.EffectivePrice)
            : validQuotes.Min(q => q.EffectivePrice);
        var worstTotal = totalCoins * worstEff;
        var savings    = side == OrderSide.Buy
            ? Math.Round(worstTotal - totalValue, 4)
            : Math.Round(totalValue - worstTotal, 4);
        var savingsPct = notionalUsd > 0 ? Math.Round(savings / notionalUsd * 100m, 4) : 0m;

        return new RoutingPlan
        {
            Symbol           = symbol,
            Side             = side,
            TotalQuantity    = totalCoins,
            TotalNotionalUsd = notionalUsd,
            Legs             = legs,
            Quotes           = quotes,
            WeightedAvgPrice = wavg,
            SavingsUsd       = savings,
            SavingsPct       = savingsPct,
            ComputedAt       = DateTime.UtcNow,
        };
    }

    // ── Routing strategies ────────────────────────────────────────────────────

    private static IReadOnlyList<RoutingLeg> RouteSingle(
        OrderSide side, decimal qty, List<ExchangeQuote> quotes)
    {
        var best = side == OrderSide.Buy
            ? quotes.MinBy(q => q.EffectivePrice)!
            : quotes.MaxBy(q => q.EffectivePrice)!;

        var value = side == OrderSide.Buy
            ? qty * best.RawPrice * (1 + best.FeeRatePct / 100m)
            : qty * best.RawPrice * (1 - best.FeeRatePct / 100m);

        return [new RoutingLeg
        {
            Exchange     = best.Exchange,
            Quantity     = qty,
            AvgFillPrice = best.RawPrice,
            FeeRatePct   = best.FeeRatePct,
            ValueUsd     = Math.Round(value, 4),
        }];
    }

    private IReadOnlyList<RoutingLeg> RouteSplit(
        OrderSide side, decimal totalQty,
        (string exch, OrderBook? book)[] books)
    {
        // Merge all relevant levels from all exchanges
        // Each entry: (effectivePrice, rawPrice, qty, exchange)
        var merged = new List<(decimal effPrice, decimal rawPrice, decimal qty, string exch)>();

        foreach (var (exch, book) in books)
        {
            if (book == null) continue;
            var levels = side == OrderSide.Buy ? book.Asks : book.Bids;
            foreach (var lv in levels)
            {
                if (lv.Price <= 0 || lv.Quantity <= 0) continue;
                var eff = side == OrderSide.Buy
                    ? lv.Price * (1 + TakerFeePct / 100m)
                    : lv.Price * (1 - TakerFeePct / 100m);
                merged.Add((eff, lv.Price, lv.Quantity, exch));
            }
        }

        // Sort: cheapest first for buy, most expensive first for sell
        merged = side == OrderSide.Buy
            ? merged.OrderBy(x => x.effPrice).ToList()
            : merged.OrderByDescending(x => x.effPrice).ToList();

        // Greedy fill
        var exchQty   = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var exchCost  = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var exchFills = new Dictionary<string, List<(decimal price, decimal qty)>>(StringComparer.OrdinalIgnoreCase);
        var remaining = totalQty;

        foreach (var (_, rawPrice, avail, exch) in merged)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, avail);
            remaining -= take;

            if (!exchQty.ContainsKey(exch)) { exchQty[exch] = 0; exchCost[exch] = 0; exchFills[exch] = []; }
            exchQty[exch]  += take;
            exchFills[exch].Add((rawPrice, take));
        }

        if (remaining > 0)
        {
            // Insufficient depth — assign remaining to best-price exchange
            var best = side == OrderSide.Buy
                ? merged.MinBy(x => x.effPrice).exch
                : merged.MaxBy(x => x.effPrice).exch;
            if (!exchQty.ContainsKey(best)) exchQty[best] = 0;
            exchQty[best] += remaining;
        }

        // Build legs
        var legs = new List<RoutingLeg>(exchQty.Count);
        foreach (var (exch, qty) in exchQty)
        {
            if (qty <= 0) continue;
            decimal wavg = 0;
            if (exchFills.TryGetValue(exch, out var fills) && fills.Count > 0)
            {
                var totalFilled = fills.Sum(f => f.qty);
                wavg = totalFilled > 0
                    ? fills.Sum(f => f.price * f.qty) / totalFilled
                    : fills[0].price;
            }
            else
            {
                // Fallback: use best available price
                wavg = side == OrderSide.Buy
                    ? merged.Where(m => m.exch == exch).MinBy(m => m.rawPrice).rawPrice
                    : merged.Where(m => m.exch == exch).MaxBy(m => m.rawPrice).rawPrice;
            }

            var value = side == OrderSide.Buy
                ? qty * wavg * (1 + TakerFeePct / 100m)
                : qty * wavg * (1 - TakerFeePct / 100m);

            legs.Add(new RoutingLeg
            {
                Exchange     = exch,
                Quantity     = Math.Round(qty, 8),
                AvgFillPrice = Math.Round(wavg, 4),
                FeeRatePct   = TakerFeePct,
                ValueUsd     = Math.Round(value, 4),
            });
        }

        // Sort legs by value descending (largest allocation first)
        legs.Sort((a, b) => b.ValueUsd.CompareTo(a.ValueUsd));
        return legs;
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes all legs of a routing plan simultaneously.
    /// Returns errors per leg if any fail.
    /// </summary>
    public async Task<(bool Ok, string Error)> ExecutePlanAsync(
        RoutingPlan plan, CancellationToken ct = default)
    {
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Task.WhenAll(plan.Legs.Select(leg => Task.Run(async () =>
        {
            if (!_gateways.TryGetValue(leg.Exchange, out var gw))
            {
                errors.Add($"{leg.Exchange}: gateway not configured");
                return;
            }
            try
            {
                await gw.PlaceOrderAsync(new Order
                {
                    Symbol   = plan.Symbol,
                    Side     = plan.Side,
                    Type     = OrderType.Market,
                    Quantity = leg.Quantity,
                });
            }
            catch (Exception ex)
            {
                errors.Add($"{leg.Exchange}: {ex.Message}");
            }
        }, ct)));

        return errors.IsEmpty
            ? (true, "")
            : (false, string.Join(" | ", errors));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(string exch, OrderBook? book)> FetchBookAsync(
        string exchange, IExchangeGateway gw, string symbol, CancellationToken ct)
    {
        try
        {
            var book = await gw.GetOrderBookAsync(symbol, BookDepth);
            return (exchange, book);
        }
        catch
        {
            return (exchange, null);
        }
    }

    private static RoutingPlan EmptyPlan(string symbol, OrderSide side, decimal notional) =>
        new()
        {
            Symbol           = symbol,
            Side             = side,
            TotalNotionalUsd = notional,
            ComputedAt       = DateTime.UtcNow,
        };
}
