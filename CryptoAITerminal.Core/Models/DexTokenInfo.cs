namespace CryptoAITerminal.Core.Models;

public class DexTokenInfo
{
    public string ChainId { get; set; } = string.Empty;
    public string DexId { get; set; } = string.Empty;
    public string PairAddress { get; set; } = string.Empty;
    public string TokenAddress { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string QuoteSymbol { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public decimal PriceUsd { get; set; }
    public decimal PriceNative { get; set; }
    public decimal PriceChange5m { get; set; }
    public decimal PriceChange1h { get; set; }
    public decimal PriceChange24h { get; set; }
    public decimal Volume24h { get; set; }
    public decimal LiquidityUsd { get; set; }
    public decimal MarketCap { get; set; }
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ObservedFirstSeenUtc { get; set; } = DateTime.MinValue;
    public int DexQualityScore { get; set; }
    public string DexQualityLabel { get; set; } = string.Empty;
    public string SignalSourceKind { get; set; } = "indexed";
    public string SignalSourceLabel { get; set; } = "Indexed snapshot";
    public string SignalConfirmationLabel { get; set; } = "Single-source";
    public int SignalSourceCount { get; set; } = 1;
    public bool WatchlistMatched { get; set; }
    public string WatchlistMatchText { get; set; } = string.Empty;
    public string OwnershipSignalStatus { get; set; } = "Ownership signals unavailable from current feed.";
}
