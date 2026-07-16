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

    /// <summary>Tokens that started being tracked recently — the "new listing" radar.</summary>
    public async Task<IReadOnlyList<dynamic>> GetNewTokensAsync(int hours, int limit, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT t.symbol, t.chain, round(s.price_usd,6) AS price, round(s.liq_usd,0) AS liq,
                   round(s.vol24h,0) AS vol24h, round(s.chg_24h,2) AS chg24h,
                   sec.risk_label, sec.is_honeypot, round(sec.buy_tax,2) AS buy_tax, round(sec.sell_tax,2) AS sell_tax,
                   h.holder_count, round(h.top10_pct,1) AS top10_pct, t.added_utc
            FROM tracked_tokens t
            JOIN token_snapshot s USING (chain, token_address)
            LEFT JOIN token_security sec USING (chain, token_address)
            LEFT JOIN token_holders h USING (chain, token_address)
            WHERE t.added_utc > now() - make_interval(hours => @hours)
            ORDER BY t.added_utc DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { hours, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Recent large transfers for the whale interpreter.</summary>
    public async Task<IReadOnlyList<dynamic>> GetWhaleTxsAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT chain, token_symbol, left(from_address,10) AS from_addr, left(to_address,10) AS to_addr,
                                    round(usd_value,0) AS usd_value, ts
                             FROM whale_txs ORDER BY ts DESC NULLS LAST LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>24h gas stats per chain for the gas advisor.</summary>
    public async Task<IReadOnlyList<dynamic>> GetGasStatsAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT g.chain,
                   round(avg(g.standard),3) AS avg24h,
                   round(min(g.standard),3) AS min24h,
                   round(max(g.standard),3) AS max24h,
                   (SELECT round(standard,3) FROM gas_prices l WHERE l.chain = g.chain ORDER BY ts DESC LIMIT 1) AS latest
            FROM gas_prices g
            WHERE g.ts > now() - interval '24 hours'
            GROUP BY g.chain;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    /// <summary>Tokens that look like they just collapsed — rug post-mortem candidates.</summary>
    public async Task<IReadOnlyList<dynamic>> GetCollapsedTokensAsync(decimal dropPct, int limit, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT t.symbol, t.chain, round(s.chg_24h,2) AS chg24h, round(s.liq_usd,0) AS liq,
                   round(s.vol24h,0) AS vol24h, sec.risk_label, sec.is_honeypot,
                   round(sec.sell_tax,2) AS sell_tax, h.holder_count, round(h.top10_pct,1) AS top10_pct,
                   d.est_rugpulls AS deployer_rugpulls, d.tokens_deployed AS deployer_tokens
            FROM tracked_tokens t
            JOIN token_snapshot s USING (chain, token_address)
            LEFT JOIN token_security sec USING (chain, token_address)
            LEFT JOIN token_holders h USING (chain, token_address)
            LEFT JOIN token_deployer d USING (chain, token_address)
            WHERE t.is_active AND s.chg_24h <= @dropPct
            ORDER BY s.chg_24h ASC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { dropPct, limit }, cancellationToken: ct))).ToList();
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
