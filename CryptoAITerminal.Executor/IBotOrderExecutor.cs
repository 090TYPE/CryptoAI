namespace CryptoAITerminal.Executor;

/// <summary>What a bot strategy wants executed on a given tick.</summary>
public readonly record struct BotIntent(
    Guid    UserId,
    Guid    BotId,
    string  Exchange,      // binance|bybit|okx|kucoin
    string  Market,        // spot|futures
    string  Mode,          // paper|live
    string  Side,          // buy|sell
    string  Asset,         // BTC
    string  Quote,         // USDT
    decimal? NotionalUsd,  // DCA: dollar amount
    decimal? Quantity,     // grid/trailing: base quantity
    string?  ClientOrderId // idempotency key
);

/// <summary>Outcome of an execution attempt, recorded into bot_orders.</summary>
public readonly record struct BotFill(
    string  Status,     // paper|placed|failed|blocked
    string? ExtRef,     // exchange order id
    decimal? AvgPrice,
    decimal? FilledQty,
    string?  Error
);

/// <summary>
/// Places a bot's order. Isolated behind an interface so the money-movement stays swappable.
///
/// ⚠️ The shipped <see cref="StubBotOrderExecutor"/> does NOT trade — it returns a paper fill.
/// The production <c>ExchangeBotOrderExecutor</c> decrypts the user's trade-only exchange key
/// and places the order through the exchange gateway (see Track 4 / Phase 4.1).
/// </summary>
public interface IBotOrderExecutor
{
    Task<BotFill> PlaceAsync(BotIntent intent, CancellationToken ct);
}

/// <summary>Skeleton executor: records a paper fill, moves NO money.</summary>
public sealed class StubBotOrderExecutor : IBotOrderExecutor
{
    public Task<BotFill> PlaceAsync(BotIntent intent, CancellationToken ct)
        => Task.FromResult(new BotFill("paper", "paper-" + Guid.NewGuid().ToString("N")[..12], null, null, null));
}
