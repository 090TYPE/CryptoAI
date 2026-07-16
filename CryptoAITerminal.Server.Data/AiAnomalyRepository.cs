using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>A favorited token that moved enough to be worth an AI look.</summary>
public sealed record AnomalyCandidate(
    string Chain, string TokenAddress, string? Symbol,
    decimal? PriceUsd, decimal? Chg1h, decimal? Chg24h, decimal? LiqUsd, decimal? Vol24h);

/// <summary>AI anomaly alerts on favorited tokens.</summary>
public sealed class AiAnomalyRepository
{
    private readonly Db _db;
    public AiAnomalyRepository(Db db) => _db = db;

    /// <summary>
    /// Deterministic pre-filter: favorited tokens whose 1h move clears the threshold and that
    /// weren't alerted within the cooldown. Keeps the AI spend bounded.
    /// </summary>
    public async Task<IReadOnlyList<AnomalyCandidate>> GetCandidatesAsync(
        decimal minMovePct, int cooldownHours, int limit, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT s.chain AS Chain, s.token_address AS TokenAddress, t.symbol AS Symbol,
                   s.price_usd AS PriceUsd, s.chg_1h AS Chg1h, s.chg_24h AS Chg24h,
                   s.liq_usd AS LiqUsd, s.vol24h AS Vol24h
            FROM token_snapshot s
            JOIN tracked_tokens t USING (chain, token_address)
            WHERE t.fav_count > 0
              AND abs(COALESCE(s.chg_1h, 0)) >= @minMovePct
              AND NOT EXISTS (
                    SELECT 1 FROM ai_anomalies a
                    WHERE a.chain = s.chain AND a.token_address = s.token_address
                      AND a.created_utc > now() - make_interval(hours => @cooldownHours))
            ORDER BY abs(COALESCE(s.chg_1h, 0)) DESC
            LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AnomalyCandidate>(
            new CommandDefinition(sql, new { minMovePct, cooldownHours, limit }, cancellationToken: ct))).ToList();
    }

    public async Task InsertAsync(string chain, string token, string? symbol, string? severity,
        string? headline, string? detail, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO ai_anomalies (chain, token_address, symbol, severity, headline, detail)
                             VALUES (@chain, @token, @symbol, @severity, @headline, @detail);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { chain, token, symbol, severity, headline, detail }, cancellationToken: ct));
    }

    /// <summary>Everyone who favorited this token (they get the push).</summary>
    public async Task<IReadOnlyList<Guid>> GetFavoritedByAsync(string chain, string token, CancellationToken ct = default)
    {
        const string sql = @"SELECT user_id FROM user_favorites WHERE chain = @chain AND token_address = @token;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { chain, token }, cancellationToken: ct))).ToList();
    }
}
