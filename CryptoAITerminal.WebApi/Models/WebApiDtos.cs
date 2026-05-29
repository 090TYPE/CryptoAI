namespace CryptoAITerminal.WebApi.Models;

// Снимок состояния, который TerminalUI периодически пишет в shared JSON.
// Структуры намеренно простые — DTO для мобильного мониторинга через HTTP.

public sealed class TerminalSnapshot
{
    public DateTime UpdatedUtc { get; set; }
    public List<PositionDto>        Positions   { get; set; } = [];
    public List<SniperCandidateDto> Candidates  { get; set; } = [];
    public PnlSummaryDto            Pnl         { get; set; } = new();
    public List<BalanceDto>         Balances    { get; set; } = [];
}

public sealed class BalanceDto
{
    public string  Exchange   { get; set; } = "";    // Binance | Bybit | OKX
    public string  Market     { get; set; } = "";    // Spot | Futures
    public string  Asset      { get; set; } = "";
    public decimal Amount     { get; set; }
}

public sealed class PositionDto
{
    public string  Source         { get; set; } = "";   // "Binance.Spot", "Bybit.Futures" и т.д.
    public string  Symbol         { get; set; } = "";
    public string  Side           { get; set; } = "";   // Long / Short / Spot
    public decimal Quantity       { get; set; }
    public decimal EntryPrice     { get; set; }
    public decimal MarkPrice      { get; set; }
    public decimal UnrealizedPnl  { get; set; }
    public decimal LeverageOrOne  { get; set; } = 1m;
}

public sealed class SniperCandidateDto
{
    public string  Chain         { get; set; } = "";
    public string  Symbol        { get; set; } = "";
    public string  Name          { get; set; } = "";
    public string  PairAddress   { get; set; } = "";
    public decimal PriceUsd      { get; set; }
    public decimal LiquidityUsd  { get; set; }
    public decimal Volume24h     { get; set; }
    public int     RankScore     { get; set; }
    public string  RankBand      { get; set; } = "";
    public string  RiskBand      { get; set; } = "";
    public bool    IsOpenPosition{ get; set; }
}

public sealed class PnlSummaryDto
{
    public decimal RealizedPnlUsd   { get; set; }
    public decimal UnrealizedPnlUsd { get; set; }
    public decimal TotalPnlUsd      => RealizedPnlUsd + UnrealizedPnlUsd;
    public int     TradesToday      { get; set; }
    public int     OpenPositions    { get; set; }
    public decimal WinRatePercent   { get; set; }
}

// Запрос на постановку маркет-ордера через WebApi → терминал.
public sealed class MarketOrderRequest
{
    public string  Exchange { get; set; } = "Binance"; // Binance | Bybit | OKX
    public string  Market   { get; set; } = "Spot";    // Spot | Futures
    public string  Symbol   { get; set; } = "";
    public string  Side     { get; set; } = "Buy";     // Buy | Sell
    public decimal Quantity { get; set; }
}

public sealed class QueuedOrderRecord
{
    public string  Id          { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime EnqueuedUtc { get; set; } = DateTime.UtcNow;
    public string Action      { get; set; } = "place";   // "place" | "cancel"
    public MarketOrderRequest Order { get; set; } = new();
    public string  OrderIdToCancel { get; set; } = string.Empty;
}

public sealed class CancelOrderRequest
{
    public string Exchange { get; set; } = "Binance";
    public string Market   { get; set; } = "Spot";
    public string OrderId  { get; set; } = "";
}

// ── TradingView Webhook ────────────────────────────────────────────────────────

/// <summary>
/// Payload that TradingView sends when an Alert fires.
/// Configure Alert Message in TradingView as JSON matching this structure.
///
/// Minimal example:
/// { "action": "buy", "symbol": "BTCUSDT", "qty": 0.01 }
///
/// Full example:
/// { "action": "buy", "symbol": "ETHUSDT", "qty": 0.05,
///   "exchange": "Bybit", "market": "Futures", "secret": "my-secret" }
/// </summary>
public sealed class TradingViewAlertDto
{
    /// <summary>buy | sell | close</summary>
    public string Action   { get; set; } = "";

    /// <summary>Trading pair, e.g. BTCUSDT</summary>
    public string Symbol   { get; set; } = "";

    /// <summary>Order quantity</summary>
    public decimal Qty     { get; set; }

    /// <summary>Target exchange: Binance (default) | Bybit | OKX | KuCoin</summary>
    public string Exchange { get; set; } = "Binance";

    /// <summary>Market type: Spot (default) | Futures</summary>
    public string Market   { get; set; } = "Spot";

    /// <summary>
    /// Optional shared secret for request validation.
    /// Set CRYPTOAI_TV_SECRET env var; if set, only matching requests are accepted.
    /// </summary>
    public string Secret   { get; set; } = "";

    /// <summary>Optional human-readable comment from the Pine Script strategy.</summary>
    public string Comment  { get; set; } = "";
}

public sealed class TradingViewWebhookResult
{
    public bool   Accepted    { get; set; }
    public string OrderId     { get; set; } = "";
    public string Message     { get; set; } = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
}
