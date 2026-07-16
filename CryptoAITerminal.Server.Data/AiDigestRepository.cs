using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>A published AI digest (shared by all users).</summary>
public sealed record DigestItem(Guid Id, string Kind, string Title, string? Body, DateTime CreatedUtc);

/// <summary>Shared AI digests + the facts the digest jobs feed to the model.</summary>
public sealed class AiDigestRepository
{
    private readonly Db _db;
    public AiDigestRepository(Db db) => _db = db;

    // ── Digests ───────────────────────────────────────────────────────────────
    public async Task InsertAsync(string kind, string title, string? body, string? model, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO ai_digests (kind, title, body, model) VALUES (@kind, @title, @body, @model);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { kind, title, body, model }, cancellationToken: ct));
    }

    /// <summary>When this digest kind last ran (drives the period/cooldown).</summary>
    public async Task<DateTime?> LastAtAsync(string kind, CancellationToken ct = default)
    {
        const string sql = @"SELECT max(created_utc) FROM ai_digests WHERE kind = @kind;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(sql, new { kind }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DigestItem>> ListRecentAsync(string? kind, int limit, CancellationToken ct = default)
    {
        var sql = @"SELECT id AS Id, kind AS Kind, title AS Title, body AS Body, created_utc AS CreatedUtc
                    FROM ai_digests"
                  + (string.IsNullOrWhiteSpace(kind) ? "" : " WHERE kind = @kind")
                  + " ORDER BY created_utc DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<DigestItem>(new CommandDefinition(sql, new { kind, limit }, cancellationToken: ct))).ToList();
    }

    // ── Facts the jobs feed to the model (all from data we already collect) ────

    /// <summary>Most-followed / most-liquid tokens with their market state.</summary>
    public async Task<IReadOnlyList<dynamic>> GetMarketFactsAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT t.symbol, t.chain, round(s.price_usd, 6) AS price, round(s.chg_1h, 2) AS chg1h,
                   round(s.chg_24h, 2) AS chg24h, round(s.liq_usd, 0) AS liq, round(s.vol24h, 0) AS vol24h
            FROM tracked_tokens t JOIN token_snapshot s USING (chain, token_address)
            WHERE t.is_active AND s.price_usd IS NOT NULL
            ORDER BY t.fav_count DESC, s.vol24h DESC NULLS LAST
            LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Biggest 24h gainers and losers.</summary>
    public async Task<IReadOnlyList<dynamic>> GetTopMoversAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"
            (SELECT t.symbol, t.chain, round(s.chg_24h,2) AS chg24h, round(s.vol24h,0) AS vol24h, round(s.liq_usd,0) AS liq
             FROM tracked_tokens t JOIN token_snapshot s USING (chain, token_address)
             WHERE t.is_active AND s.chg_24h IS NOT NULL ORDER BY s.chg_24h DESC LIMIT @limit)
            UNION ALL
            (SELECT t.symbol, t.chain, round(s.chg_24h,2), round(s.vol24h,0), round(s.liq_usd,0)
             FROM tracked_tokens t JOIN token_snapshot s USING (chain, token_address)
             WHERE t.is_active AND s.chg_24h IS NOT NULL ORDER BY s.chg_24h ASC LIMIT @limit);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<dynamic>> GetNewsHeadlinesAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT source, title, published_utc FROM news
                             ORDER BY COALESCE(published_utc, fetched_utc) DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<dynamic>> GetContextAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT 'sentiment' AS k, metric AS a, value::text AS b, label AS c FROM (
                SELECT DISTINCT ON (metric) metric, value, label FROM sentiment ORDER BY metric, ts DESC) s
            UNION ALL
            SELECT 'gas', chain, round(standard,2)::text, NULL FROM (
                SELECT DISTINCT ON (chain) chain, standard FROM gas_prices ORDER BY chain, ts DESC) g;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }
}
