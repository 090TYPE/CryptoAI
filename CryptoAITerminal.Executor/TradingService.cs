using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.Executor;

/// <summary>One recorded manual order. Status: accepted | placed | rejected.</summary>
public sealed record TradeOrderRow(
    System.Guid UserId, string Exchange, string ClientOrderId, string? ExchangeOrderId,
    string Symbol, string Side, decimal Quantity, bool ReduceOnly, string Status, string? RejectReason);

/// <summary>Idempotent journal of manual orders. Writes are keyed by (UserId, ClientOrderId).</summary>
public interface IOrderJournal
{
    Task<bool> ExistsAsync(System.Guid userId, string clientOrderId, CancellationToken ct);
    Task InsertAsync(TradeOrderRow row, CancellationToken ct);
    Task MarkPlacedAsync(System.Guid userId, string clientOrderId, string exchangeOrderId, CancellationToken ct);
}
