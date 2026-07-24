using CryptoAITerminal.Core.Contracts;
using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Npgsql-backed <see cref="IOrderJournal"/>. Writes are keyed by (user_id, client_order_id).</summary>
public sealed class OrderJournalRepository : IOrderJournal
{
    private readonly Db _db;
    public OrderJournalRepository(Db db) => _db = db;

    /// <summary>True if this (user, client-order-id) pair was already journaled (idempotency check).</summary>
    public async Task<bool> ExistsAsync(Guid userId, string clientOrderId, CancellationToken ct)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM trade_orders WHERE user_id = @userId AND client_order_id = @clientOrderId);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { userId, clientOrderId }, cancellationToken: ct));
    }

    /// <summary>UPSERT, not a plain INSERT: TradingService writes an "accepted" row and then
    /// overwrites it with "rejected" on a failure path, so a unique violation here would escape
    /// PlaceMarketAsync. An already-known exchange_order_id is never clobbered with NULL.</summary>
    public async Task InsertAsync(TradeOrderRow row, CancellationToken ct)
    {
        const string sql = @"INSERT INTO trade_orders (user_id, exchange, client_order_id, exchange_order_id, symbol, side, quantity, reduce_only, status, reject_reason)
                             VALUES (@UserId, @Exchange, @ClientOrderId, @ExchangeOrderId, @Symbol, @Side, @Quantity, @ReduceOnly, @Status, @RejectReason)
                             ON CONFLICT (user_id, client_order_id) DO UPDATE
                                SET status = @Status,
                                    reject_reason = @RejectReason,
                                    exchange_order_id = COALESCE(EXCLUDED.exchange_order_id, trade_orders.exchange_order_id),
                                    updated_utc = now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));
    }

    /// <summary>Flip an accepted row to placed once the exchange has acknowledged the order.</summary>
    public async Task MarkPlacedAsync(Guid userId, string clientOrderId, string exchangeOrderId, CancellationToken ct)
    {
        const string sql = @"UPDATE trade_orders
                                SET status = 'placed', exchange_order_id = @exchangeOrderId, updated_utc = now()
                              WHERE user_id = @userId AND client_order_id = @clientOrderId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, clientOrderId, exchangeOrderId }, cancellationToken: ct));
    }
}
