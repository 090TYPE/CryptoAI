using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Live-trading kill-switch. Live bot execution is halted if the user's flag is set OR the global
/// row (all-zero UUID) is set. Paper mode is never gated. The executor checks before every live order.
/// </summary>
public sealed class UserFlagsRepository
{
    /// <summary>The all-zero UUID row that halts live trading for everyone.</summary>
    public static readonly Guid GlobalKey = Guid.Empty;

    private readonly Db _db;
    public UserFlagsRepository(Db db) => _db = db;

    public async Task<bool> IsLiveHaltedAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT EXISTS (
                               SELECT 1 FROM user_flags
                               WHERE live_halted AND user_id IN (@userId, @global));";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { userId, global = GlobalKey }, cancellationToken: ct));
    }

    public async Task SetHaltAsync(Guid userId, bool halted, string? reason, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO user_flags (user_id, live_halted, reason, updated_utc)
                             VALUES (@userId, @halted, @reason, now())
                             ON CONFLICT (user_id) DO UPDATE
                               SET live_halted = EXCLUDED.live_halted, reason = EXCLUDED.reason, updated_utc = now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, halted, reason }, cancellationToken: ct));
    }
}
