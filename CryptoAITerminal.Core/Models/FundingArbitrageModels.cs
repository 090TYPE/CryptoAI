using System;

namespace CryptoAITerminal.Core.Models;

/// <summary>One funding-rate opportunity detected on a single exchange.</summary>
public sealed record FundingArbitrageOpportunity
{
    /// <summary>"Binance" | "Bybit" | "OKX"</summary>
    public string   Exchange        { get; init; } = "";
    /// <summary>Internal symbol, e.g. "BTCUSDT"</summary>
    public string   Symbol          { get; init; } = "";
    /// <summary>Current funding rate as a percentage per 8-hour period, e.g. 0.05 = 0.05 %</summary>
    public decimal  FundingRatePct  { get; init; }
    /// <summary>Annualised yield: rate × 3 × 365</summary>
    public decimal  AnnualizedPct   { get; init; }
    public DateTime NextFundingTime { get; init; }
    /// <summary>Mark price in USD at the time of fetch.</summary>
    public decimal  MarkPrice       { get; init; }
}

public enum FundingArbPositionState { Open, Closing, Closed }

/// <summary>
/// A delta-neutral funding-rate arb position:
/// long spot + short perpetual, same notional.
/// </summary>
public sealed class FundingArbPosition
{
    public Guid    Id                  { get; init; } = Guid.NewGuid();
    public string  Exchange            { get; set;  } = "";
    public string  Symbol              { get; set;  } = "";

    public decimal SpotQty             { get; set;  }
    public decimal PerpQty             { get; set;  }
    public decimal SpotEntryPrice      { get; set;  }
    public decimal PerpEntryPrice      { get; set;  }
    public decimal NotionalUsd         { get; set;  }

    /// <summary>Funding rate (% per 8 h) captured at the moment of entry.</summary>
    public decimal EntryFundingRatePct { get; set;  }
    public DateTime OpenedAt           { get; set;  }

    /// <summary>Estimated funding income accumulated since open (updated by service).</summary>
    public decimal FundingCollectedUsd { get; set;  }

    /// <summary>Most recently observed spot mark price.</summary>
    public decimal CurrentSpotPrice    { get; set;  }
    /// <summary>Most recently observed perp mark price.</summary>
    public decimal CurrentPerpPrice    { get; set;  }

    public FundingArbPositionState State { get; set; } = FundingArbPositionState.Open;

    /// <summary>Number of times funding income was automatically reinvested.</summary>
    public int ReinvestCount { get; set; }

    // ── Computed helpers (called by ViewModel) ────────────────────────────────

    /// <summary>Unrealised P&L on the spot leg (price appreciation).</summary>
    public decimal SpotPnlUsd  => (CurrentSpotPrice - SpotEntryPrice) * SpotQty;

    /// <summary>Unrealised P&L on the short-perp leg.</summary>
    public decimal PerpPnlUsd  => (PerpEntryPrice - CurrentPerpPrice) * PerpQty;

    /// <summary>
    /// Basis drift: (currentPerp - currentSpot) / spotEntryPrice × 100.
    /// Large positive drift means the perp premium is widening (risk signal).
    /// </summary>
    public decimal BasisDriftPct =>
        SpotEntryPrice > 0
            ? (CurrentPerpPrice - CurrentSpotPrice) / SpotEntryPrice * 100m
            : 0m;

    /// <summary>Total P&L = spot + perp legs + funding income.</summary>
    public decimal TotalPnlUsd => SpotPnlUsd + PerpPnlUsd + FundingCollectedUsd;

    public TimeSpan Age => DateTime.UtcNow - OpenedAt;
}
