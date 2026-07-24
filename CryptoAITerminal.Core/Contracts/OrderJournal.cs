using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.Core.Contracts;

/// <summary>One recorded manual order. Status: accepted | placed | rejected.</summary>
public sealed record TradeOrderRow(
    Guid UserId, string Exchange, string ClientOrderId, string? ExchangeOrderId,
    string Symbol, string Side, decimal Quantity, bool ReduceOnly, string Status, string? RejectReason);

/// <summary>Idempotent journal of manual orders, keyed by (UserId, ClientOrderId).
/// Implementations MUST treat InsertAsync as an UPSERT: TradingService writes an
/// "accepted" row and then overwrites it with "rejected" on a failure path.</summary>
public interface IOrderJournal
{
    Task<bool> ExistsAsync(Guid userId, string clientOrderId, CancellationToken ct);
    Task InsertAsync(TradeOrderRow row, CancellationToken ct);
    Task MarkPlacedAsync(Guid userId, string clientOrderId, string exchangeOrderId, CancellationToken ct);
}
