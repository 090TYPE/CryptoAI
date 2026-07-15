using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Records what the server-side bots did (one row per action).</summary>
public sealed class BotOrdersRepository
{
    private readonly Db _db;
    public BotOrdersRepository(Db db) => _db = db;

    public async Task InsertAsync(Guid botId, Guid userId, string side, string asset,
        decimal amount, decimal? price, string status, string? extRef, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO bot_orders (bot_id, user_id, side, asset, amount, price, status, ext_ref)
                             VALUES (@botId, @userId, @side, @asset, @amount, @price, @status, @extRef);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { botId, userId, side, asset, amount, price, status, extRef }, cancellationToken: ct));
    }
}
