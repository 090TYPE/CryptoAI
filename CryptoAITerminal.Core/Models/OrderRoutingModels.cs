using CryptoAITerminal.Core.Enums;
using System;
using System.Collections.Generic;

namespace CryptoAITerminal.Core.Models;

// ════════════════════════════════════════════════════════════════════════════
//  Best Execution Router — domain models
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Top-of-book price snapshot from a single exchange for one symbol, after fees.
/// </summary>
public sealed record ExchangeQuote
{
    /// <summary>Exchange name ("Binance" | "Bybit" | "OKX").</summary>
    public string  Exchange        { get; init; } = "";
    /// <summary>Raw best ask (for buy) or best bid (for sell).</summary>
    public decimal RawPrice        { get; init; }
    /// <summary>Taker fee rate used (%).</summary>
    public decimal FeeRatePct      { get; init; }
    /// <summary>
    /// Effective price after fee:
    /// Buy  → RawPrice × (1 + FeeRate/100)
    /// Sell → RawPrice × (1 − FeeRate/100)
    /// </summary>
    public decimal EffectivePrice  { get; init; }
    /// <summary>Total depth available at the top levels (coins).</summary>
    public decimal AvailableQty    { get; init; }
    /// <summary>False when gateway failed or returned empty book.</summary>
    public bool    HasData         { get; init; }
}

/// <summary>One leg of a (potentially split) routed order.</summary>
public sealed record RoutingLeg
{
    public string  Exchange      { get; init; } = "";
    /// <summary>Number of coins to trade on this exchange.</summary>
    public decimal Quantity      { get; init; }
    /// <summary>Weighted average fill price across consumed order-book levels.</summary>
    public decimal AvgFillPrice  { get; init; }
    public decimal FeeRatePct    { get; init; }
    /// <summary>
    /// Total cost (buy) or proceeds (sell) USD including fee.
    /// Buy:  Quantity × AvgFillPrice × (1 + FeeRate/100)
    /// Sell: Quantity × AvgFillPrice × (1 − FeeRate/100)
    /// </summary>
    public decimal ValueUsd      { get; init; }
}

/// <summary>Complete routing decision for one order.</summary>
public sealed record RoutingPlan
{
    public string                    Symbol          { get; init; } = "";
    public OrderSide                 Side            { get; init; }
    /// <summary>Total coins to trade.</summary>
    public decimal                   TotalQuantity   { get; init; }
    /// <summary>Total USD equivalent.</summary>
    public decimal                   TotalNotionalUsd { get; init; }
    /// <summary>One entry per exchange that will receive part of the order.</summary>
    public IReadOnlyList<RoutingLeg> Legs            { get; init; } = [];
    /// <summary>Per-exchange quotes used to build this plan.</summary>
    public IReadOnlyList<ExchangeQuote> Quotes       { get; init; } = [];
    /// <summary>Quantity-weighted average fill price across all legs.</summary>
    public decimal                   WeightedAvgPrice { get; init; }
    /// <summary>USD saved versus routing the entire order to the worst-priced exchange.</summary>
    public decimal                   SavingsUsd      { get; init; }
    /// <summary>SavingsUsd / TotalNotionalUsd × 100.</summary>
    public decimal                   SavingsPct      { get; init; }
    /// <summary>True when the order is distributed across more than one exchange.</summary>
    public bool                      IsSplit         => Legs.Count > 1;
    public DateTime                  ComputedAt      { get; init; }
}
