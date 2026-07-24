using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Records what the server-side bots did (one row per action).</summary>
public sealed class BotOrdersRepository
{
    private readonly Db _db;
    public BotOrdersRepository(Db db) => _db = db;

    public async Task InsertAsync(Guid botId, Guid userId, string side, string asset,
        decimal amount, decimal? price, string status, string? extRef,
        CancellationToken ct = default, string? clientOrderId = null)
    {
        // ON CONFLICT on the client_order_id unique index makes a re-tick after a restart a no-op
        // instead of a duplicate order row (Track 4 §4.5 idempotency).
        const string sql = @"INSERT INTO bot_orders (bot_id, user_id, side, asset, amount, price, status, ext_ref, client_order_id)
                             VALUES (@botId, @userId, @side, @asset, @amount, @price, @status, @extRef, @clientOrderId)
                             ON CONFLICT (client_order_id) DO NOTHING;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { botId, userId, side, asset, amount, price, status, extRef, clientOrderId }, cancellationToken: ct));
    }

    /// <summary>Sum of today's placed live-order notional for a user (drives the daily spend cap).</summary>
    public async Task<decimal> SumTodayPlacedNotionalAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT COALESCE(SUM(amount), 0) FROM bot_orders
                             WHERE user_id = @userId AND status = 'placed'
                               AND created_utc >= date_trunc('day', now());";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    /// <summary>True if an order with this client-order-id was already recorded (idempotency check).</summary>
    public async Task<bool> ExistsClientOrderIdAsync(string clientOrderId, CancellationToken ct = default)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM bot_orders WHERE client_order_id = @clientOrderId);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { clientOrderId }, cancellationToken: ct));
    }
}
