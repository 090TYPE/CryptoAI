using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Server-side price alerts. The worker evaluates active alerts against token_snapshot 24/7,
/// so a user is notified even with their machine off.
/// </summary>
public sealed class PriceAlertsRepository
{
    private readonly Db _db;
    public PriceAlertsRepository(Db db) => _db = db;

    public async Task<Guid> CreateAsync(Guid userId, string chain, string token, string? symbol,
        string condition, decimal threshold, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO price_alerts (user_id, chain, token_address, symbol, condition, threshold)
                             VALUES (@userId, @chain, @token, @symbol, @condition, @threshold)
                             RETURNING id;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql,
            new { userId, chain, token, symbol, condition, threshold }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AlertView>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT id AS Id, chain AS Chain, token_address AS TokenAddress, symbol AS Symbol,
                                    condition AS Condition, threshold AS Threshold, status AS Status,
                                    created_utc AS CreatedUtc, triggered_utc AS TriggeredUtc, triggered_price AS TriggeredPrice
                             FROM price_alerts WHERE user_id = @userId ORDER BY created_utc DESC LIMIT 200;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AlertView>(new CommandDefinition(sql, new { userId }, cancellationToken: ct))).ToList();
    }

    public async Task<int> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM price_alerts WHERE id = @id AND user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
    }

    /// <summary>Worker: active alerts whose condition is currently met (join to latest snapshot).</summary>
    public async Task<IReadOnlyList<DueAlert>> GetTriggeredAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT a.id AS Id, a.user_id AS UserId, a.chain AS Chain, a.token_address AS TokenAddress,
                   a.symbol AS Symbol, a.condition AS Condition, a.threshold AS Threshold, s.price_usd AS Price
            FROM price_alerts a
            JOIN token_snapshot s USING (chain, token_address)
            WHERE a.status = 'active' AND s.price_usd IS NOT NULL
              AND ( (a.condition = 'above' AND s.price_usd >= a.threshold)
                 OR (a.condition = 'below' AND s.price_usd <= a.threshold) );";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<DueAlert>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task MarkTriggeredAsync(Guid id, decimal price, CancellationToken ct = default)
    {
        const string sql = @"UPDATE price_alerts SET status='triggered', triggered_utc=now(), triggered_price=@price
                             WHERE id = @id AND status = 'active';";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, price }, cancellationToken: ct));
    }
}
