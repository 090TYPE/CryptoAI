using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>A persisted active grid order (grid_orders table). Makes a grid bot restart-safe.</summary>
public sealed record GridOrderRow(Guid Id, Guid BotId, int Level, string Side, decimal Price, decimal Qty, string? ExtRef, string Status);

/// <summary>Tracks each grid limit order so the runner can reload open levels after a restart.</summary>
public sealed class GridOrdersRepository
{
    private readonly Db _db;
    public GridOrdersRepository(Db db) => _db = db;

    public async Task AddOpenAsync(Guid botId, Guid userId, int level, string side, decimal price, decimal qty, string? extRef, CancellationToken ct = default)
    {
        // ON CONFLICT guards the unique (bot,level,side) open index — no duplicate on race/restart.
        const string sql = @"INSERT INTO grid_orders (bot_id, user_id, level, side, price, qty, ext_ref)
                             VALUES (@botId, @userId, @level, @side, @price, @qty, @extRef)
                             ON CONFLICT (bot_id, level, side) WHERE status = 'open' DO NOTHING;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { botId, userId, level, side, price, qty, extRef }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<GridOrderRow>> GetOpenAsync(Guid botId, CancellationToken ct = default)
    {
        const string sql = @"SELECT id AS Id, bot_id AS BotId, level AS Level, side AS Side,
                                    price AS Price, qty AS Qty, ext_ref AS ExtRef, status AS Status
                             FROM grid_orders WHERE bot_id = @botId AND status = 'open' ORDER BY level;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<GridOrderRow>(new CommandDefinition(sql, new { botId }, cancellationToken: ct))).ToList();
    }

    public async Task MarkFilledAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = @"UPDATE grid_orders SET status = 'filled', filled_utc = now() WHERE id = @id;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }
}
