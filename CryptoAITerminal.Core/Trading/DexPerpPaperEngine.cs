using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.Core.Trading;

public enum PerpSide { Long, Short }

public enum PerpMarginMode { Cross, Isolated }

public enum PerpOrderKind { Limit, Stop }

public enum PerpOrderStatus { Working, Filled, Cancelled }

/// <summary>A live paper position. PnL/ROE recompute from <see cref="MarkPrice"/>.</summary>
public sealed class DexPerpPosition
{
    public PerpSide Side { get; internal set; }
    public decimal Size { get; internal set; }          // token units, always positive
    public decimal EntryPrice { get; internal set; }
    public int Leverage { get; internal set; }
    public PerpMarginMode MarginMode { get; internal set; }
    public decimal Margin { get; internal set; }        // locked collateral (quote)
    public decimal MarkPrice { get; internal set; }
    public decimal LiquidationPrice { get; internal set; }
    public decimal TrailingDistance { get; internal set; }   // 0 = no trailing stop
    public decimal TrailingStopPrice { get; internal set; }

    public decimal Notional => Size * MarkPrice;

    public decimal UnrealizedPnl => Side == PerpSide.Long
        ? (MarkPrice - EntryPrice) * Size
        : (EntryPrice - MarkPrice) * Size;

    /// <summary>Return on equity = uPnL / locked margin.</summary>
    public decimal Roe => Margin <= 0m ? 0m : UnrealizedPnl / Margin;
}

/// <summary>A resting order (limit/stop) awaiting its trigger price.</summary>
public sealed class DexPerpWorkingOrder
{
    public string Id { get; init; } = string.Empty;
    public PerpSide Side { get; init; }
    public PerpOrderKind Kind { get; init; }
    public decimal TriggerPrice { get; init; }
    public decimal SizeTokens { get; init; }
    public int Leverage { get; init; }
    public PerpMarginMode MarginMode { get; init; }
    public PerpOrderStatus Status { get; internal set; } = PerpOrderStatus.Working;
    public DateTime CreatedUtc { get; init; }
    public string? PlanId { get; init; }
}

/// <summary>An executed paper fill for the blotter.</summary>
public sealed record DexPerpFill(
    PerpSide Side,
    decimal Price,
    decimal SizeTokens,
    string Kind,        // MARKET / LIMIT / STOP / CLOSE / LIQ / REVERSE
    DateTime TimeUtc);

/// <summary>
/// Deterministic paper perpetuals engine. Holds a single net position, a set of
/// resting orders and a fill history, and advances state on each mark-price
/// <see cref="Tick"/>. Pure domain logic: no UI, no timers, no randomness — the
/// caller supplies the mark price (see DexPerpMarketSimulator). Liquidation and
/// margin use a simple linear model suitable for a paper desk.
/// </summary>
public sealed class DexPerpPaperEngine
{
    private readonly decimal _startingBalance;
    private readonly decimal _mmr;               // maintenance margin rate
    private readonly List<DexPerpWorkingOrder> _orders = new();
    private readonly List<DexPerpFill> _fills = new();
    private int _idSeq;
    private decimal _lastMarkPrice;

    public DexPerpPaperEngine(decimal startingBalance = 10_000m, decimal maintenanceMarginRate = 0.005m)
    {
        _startingBalance = startingBalance;
        _mmr = maintenanceMarginRate;
    }

    private readonly List<decimal> _equityCurve = new();

    public DexPerpPosition? Position { get; private set; }
    public IReadOnlyList<DexPerpWorkingOrder> WorkingOrders => _orders;
    public IReadOnlyList<DexPerpFill> Fills => _fills;
    public decimal RealizedPnl { get; private set; }
    public decimal StartingBalance => _startingBalance;
    /// <summary>Cumulative funding cost (positive = net paid out).</summary>
    public decimal FundingPaid { get; private set; }
    /// <summary>Recent account-equity samples for a session equity curve.</summary>
    public IReadOnlyList<decimal> EquityCurve => _equityCurve;

    /// <summary>Account equity = starting balance + realised + open uPnL.</summary>
    public decimal AccountEquity => _startingBalance + RealizedPnl + (Position?.UnrealizedPnl ?? 0m);

    // ── Market / working orders ───────────────────────────────────────────────

    public void PlaceMarket(PerpSide side, decimal sizeTokens, int leverage, PerpMarginMode marginMode, decimal markPrice)
    {
        if (sizeTokens <= 0m || markPrice <= 0m)
        {
            return;
        }

        _lastMarkPrice = markPrice;
        FillInto(side, sizeTokens, leverage, marginMode, markPrice, "MARKET");
    }

    public DexPerpWorkingOrder PlaceLimit(PerpSide side, decimal triggerPrice, decimal sizeTokens, int leverage, PerpMarginMode marginMode, string? planId = null)
        => AddOrder(side, PerpOrderKind.Limit, triggerPrice, sizeTokens, leverage, marginMode, planId);

    public DexPerpWorkingOrder PlaceStop(PerpSide side, decimal triggerPrice, decimal sizeTokens, int leverage, PerpMarginMode marginMode, string? planId = null)
        => AddOrder(side, PerpOrderKind.Stop, triggerPrice, sizeTokens, leverage, marginMode, planId);

    private DexPerpWorkingOrder AddOrder(PerpSide side, PerpOrderKind kind, decimal triggerPrice, decimal sizeTokens, int leverage, PerpMarginMode marginMode, string? planId)
    {
        var order = new DexPerpWorkingOrder
        {
            Id = $"P{++_idSeq}",
            Side = side,
            Kind = kind,
            TriggerPrice = triggerPrice,
            SizeTokens = sizeTokens,
            Leverage = leverage,
            MarginMode = marginMode,
            CreatedUtc = DateTime.UtcNow,
            PlanId = planId,
        };
        _orders.Add(order);
        return order;
    }

    public void CancelOrder(string id)
    {
        var order = _orders.FirstOrDefault(o => o.Id == id);
        if (order is not null)
        {
            order.Status = PerpOrderStatus.Cancelled;
            _orders.Remove(order);
        }
    }

    // ── DCA / grid plans ──────────────────────────────────────────────────────

    /// <summary>Ladder <paramref name="legs"/> limit orders stepped away from
    /// <paramref name="startPrice"/> by <paramref name="stepPercent"/> each (down for a
    /// long, up for a short), splitting the total size evenly.</summary>
    public IReadOnlyList<DexPerpWorkingOrder> CreateDcaPlan(PerpSide side, decimal totalSizeTokens, int legs, decimal startPrice, decimal stepPercent, int leverage, PerpMarginMode marginMode)
    {
        var created = new List<DexPerpWorkingOrder>();
        if (legs <= 0 || totalSizeTokens <= 0m || startPrice <= 0m)
        {
            return created;
        }

        var planId = $"DCA{++_idSeq}";
        var legSize = decimal.Round(totalSizeTokens / legs, 8, MidpointRounding.AwayFromZero);
        var stepFactor = stepPercent / 100m;

        for (var i = 0; i < legs; i++)
        {
            var multiplier = side == PerpSide.Long
                ? Pow(1m - stepFactor, i)
                : Pow(1m + stepFactor, i);
            var price = decimal.Round(startPrice * multiplier, 10, MidpointRounding.AwayFromZero);
            created.Add(AddOrder(side, PerpOrderKind.Limit, price, legSize, leverage, marginMode, planId));
        }

        return created;
    }

    /// <summary>Lay <paramref name="levels"/> evenly spaced rungs across
    /// [<paramref name="lowerPrice"/>, <paramref name="upperPrice"/>]: buy (long) rungs
    /// below the mark, sell (short) rungs above it.</summary>
    public IReadOnlyList<DexPerpWorkingOrder> CreateGridPlan(decimal lowerPrice, decimal upperPrice, int levels, decimal sizePerLevelTokens, decimal markPrice, int leverage, PerpMarginMode marginMode)
    {
        var created = new List<DexPerpWorkingOrder>();
        if (levels <= 1 || upperPrice <= lowerPrice || sizePerLevelTokens <= 0m)
        {
            return created;
        }

        var planId = $"GRID{++_idSeq}";
        var step = (upperPrice - lowerPrice) / (levels - 1);

        for (var i = 0; i < levels; i++)
        {
            var price = decimal.Round(lowerPrice + step * i, 10, MidpointRounding.AwayFromZero);
            if (price == markPrice)
            {
                continue; // no rung exactly at the mark
            }

            var side = price < markPrice ? PerpSide.Long : PerpSide.Short;
            var kind = PerpOrderKind.Limit; // buy-below / sell-above are both resting limits
            created.Add(AddOrder(side, kind, price, sizePerLevelTokens, leverage, marginMode, planId));
        }

        return created;
    }

    // ── Position management ───────────────────────────────────────────────────

    public void Close()
    {
        if (Position is null)
        {
            return;
        }

        RealizedPnl += Position.UnrealizedPnl;
        _fills.Add(new DexPerpFill(Position.Side, Position.MarkPrice, Position.Size, "CLOSE", DateTime.UtcNow));
        Position = null;
    }

    /// <summary>Realise a fraction (0..1] of the open position at the mark, keeping the rest.</summary>
    public void ClosePartial(decimal fraction)
    {
        if (Position is null)
        {
            return;
        }

        fraction = Math.Clamp(fraction, 0m, 1m);
        if (fraction <= 0m)
        {
            return;
        }
        if (fraction >= 1m)
        {
            Close();
            return;
        }

        var closeSize = Position.Size * fraction;
        var pnlPerToken = Position.Side == PerpSide.Long
            ? Position.MarkPrice - Position.EntryPrice
            : Position.EntryPrice - Position.MarkPrice;

        RealizedPnl += pnlPerToken * closeSize;
        Position.Size -= closeSize;
        Position.Margin *= 1m - fraction;
        _fills.Add(new DexPerpFill(Position.Side, Position.MarkPrice, closeSize, "CLOSE", DateTime.UtcNow));
    }

    /// <summary>Attach a trailing stop <paramref name="distance"/> away from the mark; it
    /// ratchets in the position's favour each tick and closes the trade if hit.</summary>
    public void ArmTrailingStop(decimal distance)
    {
        if (Position is null || distance <= 0m)
        {
            return;
        }

        Position.TrailingDistance = distance;
        Position.TrailingStopPrice = Position.Side == PerpSide.Long
            ? Position.MarkPrice - distance
            : Position.MarkPrice + distance;
    }

    public void Reverse()
    {
        if (Position is null)
        {
            return;
        }

        var side = Position.Side == PerpSide.Long ? PerpSide.Short : PerpSide.Long;
        var size = Position.Size;
        var leverage = Position.Leverage;
        var marginMode = Position.MarginMode;
        var mark = Position.MarkPrice;

        Close();
        _lastMarkPrice = mark;
        FillInto(side, size, leverage, marginMode, mark, "REVERSE");
    }

    /// <summary>Apply one funding settlement at <paramref name="fundingRate"/>: longs pay
    /// (receive) when the rate is positive (negative); shorts are the mirror.</summary>
    public void AccrueFunding(decimal fundingRate)
    {
        if (Position is null)
        {
            return;
        }

        var payment = fundingRate * Position.Notional;
        var signed = Position.Side == PerpSide.Long ? -payment : payment;
        RealizedPnl += signed;
        FundingPaid += -signed;
    }

    // ── Tick: fire orders, mark, liquidate ────────────────────────────────────

    public void Tick(decimal markPrice)
    {
        if (markPrice <= 0m)
        {
            return;
        }

        // 1. Trigger resting orders whose price has been reached.
        foreach (var order in _orders.Where(o => o.Status == PerpOrderStatus.Working).ToList())
        {
            if (IsTriggered(order, markPrice))
            {
                order.Status = PerpOrderStatus.Filled;
                _orders.Remove(order);
                FillInto(order.Side, order.SizeTokens, order.Leverage, order.MarginMode, order.TriggerPrice,
                    order.Kind == PerpOrderKind.Limit ? "LIMIT" : "STOP");
            }
        }

        // 2. Mark the open position and check trailing stop / liquidation.
        if (Position is not null)
        {
            Position.MarkPrice = markPrice;

            // Trailing stop ratchets in the trade's favour, then closes if breached.
            if (Position.TrailingDistance > 0m)
            {
                if (Position.Side == PerpSide.Long)
                {
                    var candidate = markPrice - Position.TrailingDistance;
                    if (candidate > Position.TrailingStopPrice) Position.TrailingStopPrice = candidate;
                }
                else
                {
                    var candidate = markPrice + Position.TrailingDistance;
                    if (candidate < Position.TrailingStopPrice) Position.TrailingStopPrice = candidate;
                }

                var trailHit = Position.Side == PerpSide.Long
                    ? markPrice <= Position.TrailingStopPrice
                    : markPrice >= Position.TrailingStopPrice;
                if (trailHit)
                {
                    RealizedPnl += Position.UnrealizedPnl;
                    _fills.Add(new DexPerpFill(Position.Side, markPrice, Position.Size, "TRAIL", DateTime.UtcNow));
                    Position = null;
                }
            }
        }

        if (Position is not null)
        {
            var liquidated = Position.Side == PerpSide.Long
                ? markPrice <= Position.LiquidationPrice
                : markPrice >= Position.LiquidationPrice;

            if (liquidated)
            {
                RealizedPnl += Position.UnrealizedPnl; // ≈ -margin
                _fills.Add(new DexPerpFill(Position.Side, markPrice, Position.Size, "LIQ", DateTime.UtcNow));
                Position = null;
            }
        }

        _lastMarkPrice = markPrice;

        _equityCurve.Add(AccountEquity);
        if (_equityCurve.Count > 240)
        {
            _equityCurve.RemoveAt(0);
        }
    }

    private static bool IsTriggered(DexPerpWorkingOrder order, decimal price) => order.Kind switch
    {
        // buy-limit fills at/below trigger; sell-limit at/above
        PerpOrderKind.Limit => order.Side == PerpSide.Long ? price <= order.TriggerPrice : price >= order.TriggerPrice,
        // buy-stop fills at/above trigger; sell-stop at/below
        PerpOrderKind.Stop => order.Side == PerpSide.Long ? price >= order.TriggerPrice : price <= order.TriggerPrice,
        _ => false,
    };

    private void FillInto(PerpSide side, decimal sizeTokens, int leverage, PerpMarginMode marginMode, decimal price, string kind)
    {
        leverage = Math.Max(1, leverage);

        if (Position is null)
        {
            var margin = sizeTokens * price / leverage;
            Position = new DexPerpPosition
            {
                Side = side,
                Size = sizeTokens,
                EntryPrice = price,
                Leverage = leverage,
                MarginMode = marginMode,
                Margin = margin,
                MarkPrice = price,
                LiquidationPrice = LiquidationPrice(side, price, leverage),
            };
        }
        else if (Position.Side == side)
        {
            // Same side: increase and re-average.
            var newSize = Position.Size + sizeTokens;
            Position.EntryPrice = (Position.EntryPrice * Position.Size + price * sizeTokens) / newSize;
            Position.Size = newSize;
            Position.Leverage = leverage;
            Position.Margin += sizeTokens * price / leverage;
            Position.MarkPrice = price;
            Position.LiquidationPrice = LiquidationPrice(side, Position.EntryPrice, leverage);
        }
        else
        {
            // Opposite side: realise against the smaller leg, flip any remainder.
            var closeSize = Math.Min(Position.Size, sizeTokens);
            var pnlPerToken = Position.Side == PerpSide.Long ? price - Position.EntryPrice : Position.EntryPrice - price;
            RealizedPnl += pnlPerToken * closeSize;

            var remainder = sizeTokens - Position.Size;
            if (remainder > 0m)
            {
                var margin = remainder * price / leverage;
                Position = new DexPerpPosition
                {
                    Side = side,
                    Size = remainder,
                    EntryPrice = price,
                    Leverage = leverage,
                    MarginMode = marginMode,
                    Margin = margin,
                    MarkPrice = price,
                    LiquidationPrice = LiquidationPrice(side, price, leverage),
                };
            }
            else if (Position.Size == sizeTokens)
            {
                Position = null;
            }
            else
            {
                Position.Size -= sizeTokens;
                Position.MarkPrice = price;
            }
        }

        _fills.Add(new DexPerpFill(side, price, sizeTokens, kind, DateTime.UtcNow));
    }

    private decimal LiquidationPrice(PerpSide side, decimal entry, int leverage)
    {
        var inv = 1m / leverage;
        return side == PerpSide.Long
            ? entry * (1m + _mmr - inv)
            : entry * (1m - _mmr + inv);
    }

    private static decimal Pow(decimal baseValue, int exp)
    {
        var result = 1m;
        for (var i = 0; i < exp; i++)
        {
            result *= baseValue;
        }
        return result;
    }
}
