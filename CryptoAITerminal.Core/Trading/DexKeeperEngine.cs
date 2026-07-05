using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.Core.Trading;

public enum KeeperOrderKind { Limit, Stop, Trailing }

public enum KeeperSide { Buy, Sell }

public enum KeeperOrderStatus { Working, Triggered, Cancelled }

/// <summary>
/// One resting conditional order the DEX keeper watches: a LIMIT (buy-below / sell-above =
/// take-profit), a STOP (buy-above / sell-below = stop-loss) or a TRAILING sell that
/// ratchets its stop up as price rises. Bound to a specific token; amounts are quote-USD
/// for buys and token quantity for sells.
/// </summary>
public sealed class DexKeeperOrder
{
    public string Id { get; set; } = string.Empty;
    public string TokenAddress { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public KeeperOrderKind Kind { get; set; }
    public KeeperSide Side { get; set; }
    public decimal TriggerPrice { get; set; }
    public decimal AmountUsd { get; set; }        // for buys
    public decimal AmountTokens { get; set; }     // for sells
    public decimal TrailingDistancePct { get; set; }
    public decimal TrailingPeak { get; set; }
    public KeeperOrderStatus Status { get; set; } = KeeperOrderStatus.Working;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? PlanId { get; set; }
}

/// <summary>
/// Deterministic conditional-order engine for the DEX keeper. Holds resting orders and,
/// on each price <see cref="Tick"/> for a token, returns the orders that should execute
/// now (marking them Triggered). Pure — no network, no timers, no execution. The hosting
/// service is what actually broadcasts the swap (behind the live-execution guard).
/// </summary>
public sealed class DexKeeperEngine
{
    private readonly List<DexKeeperOrder> _orders = new();
    private int _seq;

    public IReadOnlyList<DexKeeperOrder> Orders => _orders;
    public IEnumerable<DexKeeperOrder> WorkingOrders => _orders.Where(o => o.Status == KeeperOrderStatus.Working);

    public DexKeeperOrder Add(DexKeeperOrder order)
    {
        order.Id = string.IsNullOrEmpty(order.Id) ? $"K{++_seq}" : order.Id;
        if (order.CreatedUtc == default) order.CreatedUtc = DateTime.UtcNow;
        if (order.Kind == KeeperOrderKind.Trailing && order.TrailingPeak <= 0m)
        {
            order.TrailingPeak = order.TriggerPrice; // seed with the arm price
        }
        _orders.Add(order);
        return order;
    }

    public void Cancel(string id)
    {
        var o = _orders.FirstOrDefault(x => x.Id == id);
        if (o is not null)
        {
            o.Status = KeeperOrderStatus.Cancelled;
            _orders.Remove(o);
        }
    }

    public void Remove(DexKeeperOrder order) => _orders.Remove(order);

    /// <summary>Ladder <paramref name="legs"/> limit-buy orders down from
    /// <paramref name="startPrice"/> by <paramref name="stepPercent"/> each, splitting the
    /// total quote spend evenly.</summary>
    public IReadOnlyList<DexKeeperOrder> CreateDca(
        string tokenAddress, string chainId, string symbol,
        decimal totalUsd, int legs, decimal startPrice, decimal stepPercent)
    {
        var created = new List<DexKeeperOrder>();
        if (legs <= 0 || totalUsd <= 0m || startPrice <= 0m)
        {
            return created;
        }

        var planId = $"DCA{++_seq}";
        var perLeg = decimal.Round(totalUsd / legs, 6, MidpointRounding.AwayFromZero);
        var step = stepPercent / 100m;
        for (var i = 0; i < legs; i++)
        {
            var mult = 1m;
            for (var j = 0; j < i; j++) mult *= 1m - step;
            created.Add(Add(new DexKeeperOrder
            {
                TokenAddress = tokenAddress,
                ChainId = chainId,
                Symbol = symbol,
                Kind = KeeperOrderKind.Limit,
                Side = KeeperSide.Buy,
                TriggerPrice = decimal.Round(startPrice * mult, 12, MidpointRounding.AwayFromZero),
                AmountUsd = perLeg,
                PlanId = planId,
            }));
        }
        return created;
    }

    /// <summary>Advance one price tick for a token; returns the orders that fire now.</summary>
    public IReadOnlyList<DexKeeperOrder> Tick(string tokenAddress, decimal price)
    {
        var fired = new List<DexKeeperOrder>();
        if (price <= 0m)
        {
            return fired;
        }

        foreach (var o in _orders.Where(o => o.Status == KeeperOrderStatus.Working
                                             && string.Equals(o.TokenAddress, tokenAddress, StringComparison.OrdinalIgnoreCase)))
        {
            if (o.Kind == KeeperOrderKind.Trailing && price > o.TrailingPeak)
            {
                o.TrailingPeak = price;
            }

            if (ShouldFire(o, price))
            {
                o.Status = KeeperOrderStatus.Triggered;
                fired.Add(o);
            }
        }

        return fired;
    }

    private static bool ShouldFire(DexKeeperOrder o, decimal price) => o.Kind switch
    {
        KeeperOrderKind.Limit => o.Side == KeeperSide.Buy ? price <= o.TriggerPrice : price >= o.TriggerPrice,
        KeeperOrderKind.Stop => o.Side == KeeperSide.Buy ? price >= o.TriggerPrice : price <= o.TriggerPrice,
        KeeperOrderKind.Trailing => o.TrailingDistancePct > 0m
            && price <= o.TrailingPeak * (1m - o.TrailingDistancePct / 100m),
        _ => false,
    };
}
