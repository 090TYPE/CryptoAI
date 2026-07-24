using System.Collections.Concurrent;
using System.Threading.Tasks;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Trading;

public sealed class InMemoryOrderJournal : IOrderJournal
{
    public readonly ConcurrentDictionary<string, TradeOrderRow> Rows = new();
    private static string Key(System.Guid u, string cid) => $"{u}|{cid}";

    public Task<bool> ExistsAsync(System.Guid userId, string clientOrderId, System.Threading.CancellationToken ct)
        => Task.FromResult(Rows.ContainsKey(Key(userId, clientOrderId)));

    public Task InsertAsync(TradeOrderRow row, System.Threading.CancellationToken ct)
    { Rows[Key(row.UserId, row.ClientOrderId)] = row; return Task.CompletedTask; }

    public Task MarkPlacedAsync(System.Guid userId, string clientOrderId, string exchangeOrderId, System.Threading.CancellationToken ct)
    {
        var k = Key(userId, clientOrderId);
        if (Rows.TryGetValue(k, out var r)) Rows[k] = r with { ExchangeOrderId = exchangeOrderId, Status = "placed" };
        return Task.CompletedTask;
    }
}
