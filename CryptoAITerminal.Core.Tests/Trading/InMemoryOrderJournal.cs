using System.Collections.Concurrent;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Contracts;

namespace CryptoAITerminal.Core.Tests.Trading;

/// <summary>In-memory <see cref="IOrderJournal"/>. The claim is a single
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>, so two concurrent claimers
/// behave exactly like the Postgres INSERT ... ON CONFLICT DO NOTHING: one wins, one loses.</summary>
public sealed class InMemoryOrderJournal : IOrderJournal
{
    public readonly ConcurrentDictionary<string, TradeOrderRow> Rows = new();
    private static string Key(System.Guid u, string cid) => $"{u}|{cid}";

    /// <summary>Atomic — TryAdd is the whole check-and-write, there is no window between them.</summary>
    public Task<bool> TryClaimAsync(TradeOrderRow row, System.Threading.CancellationToken ct)
        => Task.FromResult(Rows.TryAdd(Key(row.UserId, row.ClientOrderId), row));

    public Task<TradeOrderRow?> GetAsync(System.Guid userId, string clientOrderId, System.Threading.CancellationToken ct)
        => Task.FromResult(Rows.TryGetValue(Key(userId, clientOrderId), out var r) ? r : null);

    public Task MarkPlacedAsync(System.Guid userId, string clientOrderId, string exchangeOrderId, System.Threading.CancellationToken ct)
    {
        var k = Key(userId, clientOrderId);
        if (Rows.TryGetValue(k, out var r)) Rows[k] = r with { ExchangeOrderId = exchangeOrderId, Status = "placed" };
        return Task.CompletedTask;
    }

    /// <summary>Never downgrades a 'placed' row (mirrors the SQL `AND status &lt;&gt; 'placed'` guard).</summary>
    public Task MarkRejectedAsync(System.Guid userId, string clientOrderId, string reason, System.Threading.CancellationToken ct)
    {
        var k = Key(userId, clientOrderId);
        if (Rows.TryGetValue(k, out var r) && r.Status != "placed")
            Rows[k] = r with { Status = "rejected", RejectReason = reason };
        return Task.CompletedTask;
    }
}
