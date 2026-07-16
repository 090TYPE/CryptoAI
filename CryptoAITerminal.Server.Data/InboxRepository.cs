using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>One in-terminal notification.</summary>
public sealed record InboxItem(Guid Id, string Kind, string Title, string? Message, DateTime CreatedUtc, DateTime? ReadUtc);

/// <summary>In-app inbox — the delivery channel every user gets, no phone setup needed.</summary>
public sealed class InboxRepository
{
    private readonly Db _db;
    public InboxRepository(Db db) => _db = db;

    public async Task InsertAsync(Guid userId, string kind, string title, string? message, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO inbox (user_id, kind, title, message) VALUES (@userId, @kind, @title, @message);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, kind, title, message }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<InboxItem>> ListForUserAsync(Guid userId, bool unreadOnly, int limit, CancellationToken ct = default)
    {
        var sql = @"SELECT id AS Id, kind AS Kind, title AS Title, message AS Message,
                           created_utc AS CreatedUtc, read_utc AS ReadUtc
                    FROM inbox WHERE user_id = @userId"
                  + (unreadOnly ? " AND read_utc IS NULL" : "")
                  + " ORDER BY created_utc DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<InboxItem>(new CommandDefinition(sql, new { userId, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT count(*) FROM inbox WHERE user_id = @userId AND read_utc IS NULL;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    public async Task<int> MarkReadAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        const string sql = @"UPDATE inbox SET read_utc = now() WHERE id = @id AND user_id = @userId AND read_utc IS NULL;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"UPDATE inbox SET read_utc = now() WHERE user_id = @userId AND read_utc IS NULL;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }
}
