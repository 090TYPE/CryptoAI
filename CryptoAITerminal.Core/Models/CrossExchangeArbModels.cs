using System;
using System.Collections.Generic;

namespace CryptoAITerminal.Core.Models;

/// <summary>A live price discrepancy between two exchanges for the same spot symbol.</summary>
public sealed record CrossExchangeOpportunity
{
    public string   Symbol         { get; init; } = "";
    /// <summary>Buy here (lower ask).</summary>
    public string   BuyExchange    { get; init; } = "";
    /// <summary>Ask price on the buy side.</summary>
    public decimal  BuyAsk         { get; init; }
    /// <summary>Sell here (higher bid).</summary>
    public string   SellExchange   { get; init; } = "";
    /// <summary>Bid price on the sell side.</summary>
    public decimal  SellBid        { get; init; }
    /// <summary>(SellBid – BuyAsk) / BuyAsk × 100</summary>
    public decimal  GrossSpreadPct { get; init; }
    /// <summary>GrossSpread minus round-trip taker fees.</summary>
    public decimal  NetSpreadPct   { get; init; }
    public bool     IsProfitable   => NetSpreadPct > 0;
    public DateTime DetectedAt     { get; init; }
}

/// <summary>Per-exchange price snapshot for one symbol.</summary>
public sealed record ExchangePriceData(
    decimal  Bid,
    decimal  Ask,
    decimal  Last,
    DateTime UpdatedAt)
{
    public bool    IsStale    => (DateTime.UtcNow - UpdatedAt).TotalSeconds > 8;
    /// <summary>Effective bid — falls back to Last × 0.9999 if Bid is zero.</summary>
    public decimal EffBid     => Bid  > 0 ? Bid  : Last * 0.9999m;
    /// <summary>Effective ask — falls back to Last × 1.0001 if Ask is zero.</summary>
    public decimal EffAsk     => Ask  > 0 ? Ask  : Last * 1.0001m;
}

/// <summary>All exchange prices for one symbol, plus the best arb opportunity.</summary>
public sealed class CrossExchangePriceRow
{
    public string Symbol { get; set; } = "";
    /// <summary>Key = exchange name ("Binance" | "Bybit" | "OKX").</summary>
    public Dictionary<string, ExchangePriceData> Prices { get; } = new();
    /// <summary>Best arb opportunity for this symbol (may be null / not profitable).</summary>
    public CrossExchangeOpportunity? BestOpportunity { get; set; }
}

/// <summary>Record of one executed arb pair.</summary>
public sealed class CrossExchangeArbExecution
{
    public Guid    Id                  { get; init; } = Guid.NewGuid();
    public string  Symbol              { get; set;  } = "";
    public string  BuyExchange         { get; set;  } = "";
    public decimal BuyPrice            { get; set;  }
    public string  SellExchange        { get; set;  } = "";
    public decimal SellPrice           { get; set;  }
    public decimal Quantity            { get; set;  }
    public decimal NotionalUsd         { get; set;  }
    public decimal GrossSpreadPct      { get; set;  }
    public decimal EstimatedProfitUsd  { get; set;  }
    public DateTime ExecutedAt         { get; set;  }
    /// <summary>"Executed" | "Failed: reason"</summary>
    public string  Status              { get; set;  } = "";
    public bool    IsSuccess           => Status == "Executed";
}
