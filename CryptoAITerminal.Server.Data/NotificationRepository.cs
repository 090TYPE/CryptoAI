using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>A user's push channel. <paramref name="Token"/> is the Telegram bot token (null for ntfy).</summary>
public sealed record NotificationChannel(string Kind, string Target, string? Token, bool Enabled);

/// <summary>Per-user notification channel (one per user).</summary>
public sealed class NotificationRepository
{
    private readonly Db _db;
    public NotificationRepository(Db db) => _db = db;

    public async Task<NotificationChannel?> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT kind AS Kind, target AS Target, token AS Token, enabled AS Enabled
                             FROM notification_channels WHERE user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<NotificationChannel>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    public async Task UpsertAsync(Guid userId, string kind, string target, string? token, bool enabled, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO notification_channels (user_id, kind, target, token, enabled, updated_utc)
            VALUES (@userId, @kind, @target, @token, @enabled, now())
            ON CONFLICT (user_id) DO UPDATE
              SET kind=EXCLUDED.kind, target=EXCLUDED.target, token=EXCLUDED.token,
                  enabled=EXCLUDED.enabled, updated_utc=now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, kind, target, token, enabled }, cancellationToken: ct));
    }
}
