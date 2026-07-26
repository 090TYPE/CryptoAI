using CryptoAITerminal.Server.Common;
using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>A user's push channel. <paramref name="Token"/> is the Telegram bot token (null for ntfy).</summary>
public sealed record NotificationChannel(string Kind, string Target, string? Token, bool Enabled);

/// <summary>
/// Per-user notification channel (one per user). The Telegram bot token is a working credential
/// for someone else's bot, so it is protected at rest by <see cref="ColumnCipher"/> rather than
/// sitting in the column in the clear where any dump or replica hands it over.
/// </summary>
public sealed class NotificationRepository
{
    private readonly Db _db;
    private readonly ColumnCipher _columns;

    public NotificationRepository(Db db, ColumnCipher? columns = null)
    {
        _db = db;
        _columns = columns ?? ColumnCipher.FromEnvironment();
    }

    public async Task<NotificationChannel?> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT kind AS Kind, target AS Target, token AS Token, enabled AS Enabled
                             FROM notification_channels WHERE user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<NotificationChannel>(
            new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        if (row is null) return null;

        var token = await _columns.UnprotectAsync(row.Token, ct);
        return row with { Token = token };
    }

    public async Task UpsertAsync(Guid userId, string kind, string target, string? token, bool enabled, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO notification_channels (user_id, kind, target, token, enabled, updated_utc)
            VALUES (@userId, @kind, @target, @token, @enabled, now())
            ON CONFLICT (user_id) DO UPDATE
              SET kind=EXCLUDED.kind, target=EXCLUDED.target, token=EXCLUDED.token,
                  enabled=EXCLUDED.enabled, updated_utc=now();";
        var stored = await _columns.ProtectAsync(token, ct);
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { userId, kind, target, token = stored, enabled }, cancellationToken: ct));
    }
}
