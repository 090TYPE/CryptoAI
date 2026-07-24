using CryptoAITerminal.Core.Contracts;
using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Npgsql-backed <see cref="IOrderJournal"/>. Writes are keyed by (user_id, client_order_id),
/// which is the table's PRIMARY KEY — the database, not the application, arbitrates duplicates.</summary>
public sealed class OrderJournalRepository : IOrderJournal
{
    private readonly Db _db;
    public OrderJournalRepository(Db db) => _db = db;

    /// <summary>Atomic claim. ON CONFLICT DO NOTHING (never DO UPDATE) so the conflict is
    /// observable: rowsAffected is 1 only for the caller that actually inserted the row.
    /// Two concurrent requests with the same ClientOrderId therefore cannot both place.</summary>
    public async Task<bool> TryClaimAsync(TradeOrderRow row, CancellationToken ct)
    {
        const string sql = @"INSERT INTO trade_orders (user_id, exchange, client_order_id, exchange_order_id, symbol, side, quantity, reduce_only, status, reject_reason)
                             VALUES (@UserId, @Exchange, @ClientOrderId, @ExchangeOrderId, @Symbol, @Side, @Quantity, @ReduceOnly, 'accepted', @RejectReason)
                             ON CONFLICT (user_id, client_order_id) DO NOTHING;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));
        return rowsAffected == 1;
    }

    /// <summary>The recorded row for a replayed ClientOrderId, or null. Columns are aliased to the
    /// record's PascalCase properties so Dapper can bind the positional constructor.</summary>
    public async Task<TradeOrderRow?> GetAsync(Guid userId, string clientOrderId, CancellationToken ct)
    {
        const string sql = @"SELECT user_id           AS ""UserId"",
                                    exchange          AS ""Exchange"",
                                    client_order_id   AS ""ClientOrderId"",
                                    exchange_order_id AS ""ExchangeOrderId"",
                                    symbol            AS ""Symbol"",
                                    side              AS ""Side"",
                                    quantity          AS ""Quantity"",
                                    reduce_only       AS ""ReduceOnly"",
                                    status            AS ""Status"",
                                    reject_reason     AS ""RejectReason""
                               FROM trade_orders
                              WHERE user_id = @userId AND client_order_id = @clientOrderId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TradeOrderRow>(
            new CommandDefinition(sql, new { userId, clientOrderId }, cancellationToken: ct));
    }

    /// <summary>Flip the claimed row to placed once the exchange has acknowledged the order.
    /// 'placed' is terminal: nothing downgrades it afterwards.</summary>
    public async Task MarkPlacedAsync(Guid userId, string clientOrderId, string exchangeOrderId, CancellationToken ct)
    {
        const string sql = @"UPDATE trade_orders
                                SET status = 'placed', exchange_order_id = @exchangeOrderId, updated_utc = now()
                              WHERE user_id = @userId AND client_order_id = @clientOrderId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, clientOrderId, exchangeOrderId }, cancellationToken: ct));
    }

    /// <summary>Record a rejection. The `status &lt;&gt; 'placed'` guard is what stops a late
    /// failure from rewriting a live order as rejected.</summary>
    public async Task MarkRejectedAsync(Guid userId, string clientOrderId, string reason, CancellationToken ct)
    {
        const string sql = @"UPDATE trade_orders
                                SET status = 'rejected', reject_reason = @reason, updated_utc = now()
                              WHERE user_id = @userId AND client_order_id = @clientOrderId AND status <> 'placed';";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, clientOrderId, reason }, cancellationToken: ct));
    }
}
