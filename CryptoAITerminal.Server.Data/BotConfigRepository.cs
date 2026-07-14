using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Per-user autonomous strategy configs the executor runs.</summary>
public sealed class BotConfigRepository
{
    private readonly Db _db;
    public BotConfigRepository(Db db) => _db = db;

    public async Task<Guid> CreateAsync(Guid userId, string strategy, string? paramsJson, bool enabled, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO bot_configs (user_id, strategy, params_json, enabled)
                             VALUES (@userId, @strategy, @paramsJson::jsonb, @enabled)
                             RETURNING id;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql,
            new { userId, strategy, paramsJson, enabled }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<BotConfigView>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT id AS Id, strategy AS Strategy, params_json AS ParamsJson,
                                    enabled AS Enabled, updated_utc AS UpdatedUtc
                             FROM bot_configs WHERE user_id = @userId ORDER BY updated_utc DESC;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<BotConfigView>(new CommandDefinition(sql, new { userId }, cancellationToken: ct))).ToList();
    }

    public async Task<int> SetEnabledAsync(Guid userId, Guid id, bool enabled, CancellationToken ct = default)
    {
        const string sql = @"UPDATE bot_configs SET enabled = @enabled, updated_utc = now()
                             WHERE id = @id AND user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id, userId, enabled }, cancellationToken: ct));
    }

    public async Task<int> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM bot_configs WHERE id = @id AND user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
    }
}
