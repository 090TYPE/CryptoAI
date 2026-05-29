namespace CryptoAITerminal.Core.Models;

public class MarketData
{
    public string  Symbol        { get; set; } = "";
    public decimal BestBid       { get; set; }
    public decimal BestAsk       { get; set; }
    public decimal LastPrice     { get; set; }
    public DateTime Timestamp    { get; set; }

    // ── 24-h stats (populated when available from the ticker stream) ──────────
    /// <summary>24-hour quote asset volume in USDT. Zero if unavailable.</summary>
    public decimal Volume24hUsd  { get; set; }
    /// <summary>24-hour price change in percent. Zero if unavailable.</summary>
    public decimal ChangePct24h  { get; set; }
    /// <summary>24-hour high. Zero if unavailable.</summary>
    public decimal High24h       { get; set; }
    /// <summary>24-hour low. Zero if unavailable.</summary>
    public decimal Low24h        { get; set; }
}