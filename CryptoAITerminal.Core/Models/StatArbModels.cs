using System;

namespace CryptoAITerminal.Core.Models;

public sealed class StatArbConfig
{
    public string  SymbolA     { get; set; } = "BTCUSDT";
    public string  SymbolB     { get; set; } = "ETHUSDT";
    public int     Window      { get; set; } = 50;
    public decimal EntryZScore { get; set; } = 2.0m;
    public decimal ExitZScore  { get; set; } = 0.5m;
    public decimal NotionalUsd { get; set; } = 500m;
    public string  Exchange    { get; set; } = "Binance";
}

public enum StatArbPositionDirection { None, LongAShortB, LongBShortA }

public sealed class StatArbPosition
{
    public Guid                    Id          { get; init; } = Guid.NewGuid();
    public string                  SymbolA     { get; init; } = "";
    public string                  SymbolB     { get; init; } = "";
    public StatArbPositionDirection Direction   { get; set;  }
    public decimal                 QtyA        { get; set;  }
    public decimal                 QtyB        { get; set;  }
    public decimal                 EntryPriceA { get; set;  }
    public decimal                 EntryPriceB { get; set;  }
    public decimal                 EntrySpread { get; set;  }
    public decimal                 EntryZScore { get; set;  }
    public decimal                 CurrentZScore { get; set; }
    public DateTime                OpenedAt    { get; init; } = DateTime.UtcNow;

    public decimal CurrentPriceA { get; set; }
    public decimal CurrentPriceB { get; set; }

    public decimal PnlUsd =>
        Direction == StatArbPositionDirection.LongAShortB
            ? (CurrentPriceA - EntryPriceA) * QtyA - (CurrentPriceB - EntryPriceB) * QtyB
            : (CurrentPriceB - EntryPriceB) * QtyB - (CurrentPriceA - EntryPriceA) * QtyA;
}

public sealed record SpreadPoint(DateTime Time, decimal Spread, decimal ZScore);
