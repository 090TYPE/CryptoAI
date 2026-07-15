namespace CryptoAITerminal.Server.Data;

/// <summary>A token the worker has claimed and must poll now.</summary>
public sealed record DueToken(string Chain, string TokenAddress, string? PoolAddress, int PollIntervalS);

/// <summary>An active tracked token (any state), for the data collectors.</summary>
public sealed record ActiveToken(string Chain, string TokenAddress, string? PoolAddress);

/// <summary>One OHLCV candle to persist. <paramref name="Ts"/> MUST be UTC (Kind=Utc).</summary>
public sealed record CandleRow(DateTime Ts, decimal O, decimal H, decimal L, decimal C, decimal V);

/// <summary>A user's favorite joined with its latest market snapshot (nulls until first poll).</summary>
public sealed record FavoriteView(
    string Chain,
    string TokenAddress,
    string? Symbol,
    decimal? PriceUsd,
    decimal? Chg1h,
    decimal? Chg24h,
    decimal? Vol24h);

/// <summary>One favorite as sent by the client during a full sync.</summary>
public sealed record FavoriteInput(string Chain, string TokenAddress, string? Symbol);

/// <summary>A news headline for the API.</summary>
public sealed record NewsItem(string Source, string Title, string Url, string? Summary, DateTime? PublishedUtc);

/// <summary>A sentiment reading.</summary>
public sealed record SentimentPoint(string Metric, decimal? Value, string? Label, DateTime Ts);

/// <summary>Everything the app shows about one token: market + metadata + security + holders + deployer.</summary>
public sealed record TokenDetail(
    string Chain, string TokenAddress, string? Symbol, string? Name,
    decimal? PriceUsd, decimal? LiqUsd, decimal? Vol24h,
    decimal? Chg5m, decimal? Chg1h, decimal? Chg24h, decimal? MarketCap,
    string? ImageUrl, string? Website, string? Twitter, string? Telegram,
    bool? IsHoneypot, decimal? BuyTax, decimal? SellTax, string? RiskLabel, int? RiskScore,
    long? HolderCount, decimal? HoldersTop10Pct,
    string? DeployerAddress, int? DeployerTokensDeployed, int? DeployerRugpulls, int? DeployerWalletAgeMonths);

/// <summary>Latest gas price for a chain (gwei).</summary>
public sealed record GasPoint(string Chain, decimal? Standard, DateTime Ts);

/// <summary>Latest on-chain metric reading.</summary>
public sealed record OnChainPoint(string Asset, string Metric, decimal? Value, DateTime Ts);

/// <summary>A large transfer.</summary>
public sealed record WhaleTx(string TxHash, string Chain, string? TokenSymbol, string? FromAddress, string? ToAddress, decimal? UsdValue, DateTime? Ts);

/// <summary>A liquidation bucket.</summary>
public sealed record LiquidationPoint(string Symbol, string? Side, decimal? UsdValue, DateTime Ts);

/// <summary>A withdrawal request as shown to the user.</summary>
public sealed record WithdrawalView(
    Guid Id, string Asset, decimal Amount, string ToAddress, string Status,
    DateTime RequestedUtc, DateTime ExecuteAfterUtc);

/// <summary>An autonomous-strategy config.</summary>
public sealed record BotConfigView(Guid Id, string Strategy, string? ParamsJson, bool Enabled, DateTime UpdatedUtc);

/// <summary>A price alert as shown to the user.</summary>
public sealed record AlertView(
    Guid Id, string Chain, string TokenAddress, string? Symbol, string Condition,
    decimal Threshold, string Status, DateTime CreatedUtc, DateTime? TriggeredUtc, decimal? TriggeredPrice);

/// <summary>An active alert whose condition is currently met (the worker fires it).</summary>
public sealed record DueAlert(Guid Id, Guid UserId, string Chain, string TokenAddress, string? Symbol,
    string Condition, decimal Threshold, decimal Price);
