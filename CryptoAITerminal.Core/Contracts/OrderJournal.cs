using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.Core.Contracts;

/// <summary>One recorded manual order. Status: accepted | placed | rejected.</summary>
public sealed record TradeOrderRow(
    Guid UserId, string Exchange, string ClientOrderId, string? ExchangeOrderId,
    string Symbol, string Side, decimal Quantity, bool ReduceOnly, string Status, string? RejectReason);

/// <summary>Idempotent journal of manual orders, keyed by (UserId, ClientOrderId).
/// The claim is the idempotency primitive: exactly one caller may ever win it for a key.</summary>
public interface IOrderJournal
{
    /// <summary>Atomically claim (UserId, ClientOrderId). Returns true when THIS caller won the
    /// claim and may place the order; false when the pair was already claimed. Implementations
    /// MUST make this atomic — a check-then-insert lets two concurrent callers both place.</summary>
    Task<bool> TryClaimAsync(TradeOrderRow row, CancellationToken ct);

    /// <summary>The recorded row for a replayed ClientOrderId, or null.</summary>
    Task<TradeOrderRow?> GetAsync(Guid userId, string clientOrderId, CancellationToken ct);

    /// <summary>Record that the exchange accepted the order. Terminal — must never be downgraded.</summary>
    Task MarkPlacedAsync(Guid userId, string clientOrderId, string exchangeOrderId, CancellationToken ct);

    /// <summary>Record a rejection, but NEVER overwrite a row already marked 'placed'.</summary>
    Task MarkRejectedAsync(Guid userId, string clientOrderId, string reason, CancellationToken ct);
}
