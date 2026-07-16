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

    /// <summary>Per-chain aggregate: where the flow is.</summary>
    public async Task<IReadOnlyList<dynamic>> GetChainAggregatesAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT s.chain, count(*) AS tokens, round(sum(s.vol24h),0) AS vol24h,
                   round(sum(s.liq_usd),0) AS liq, round(avg(s.chg_24h),2) AS avg_chg24h
            FROM token_snapshot s JOIN tracked_tokens t USING (chain, token_address)
            WHERE t.is_active GROUP BY s.chain ORDER BY sum(s.vol24h) DESC NULLS LAST;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    /// <summary>24h realised range per token, straight from the 1m candles.</summary>
    public async Task<IReadOnlyList<dynamic>> GetVolatilityAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT t.symbol, c.chain, round(min(c.l),8) AS low24h, round(max(c.h),8) AS high24h,
                   round((max(c.h) - min(c.l)) / NULLIF(min(c.l),0) * 100, 1) AS range_pct,
                   count(*) AS candles
            FROM dex_candles c JOIN tracked_tokens t USING (chain, token_address)
            WHERE c.ts > now() - interval '24 hours' AND c.l > 0
            GROUP BY t.symbol, c.chain HAVING count(*) > 10
            ORDER BY (max(c.h) - min(c.l)) / NULLIF(min(c.l),0) DESC
            LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Recent on-chain metric series (BTC/ETH active addresses, tx count).</summary>
    public async Task<IReadOnlyList<dynamic>> GetOnchainSeriesAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT asset, metric, round(value,0) AS value, ts FROM onchain_metrics
                             ORDER BY ts DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<dynamic>> GetLiquidationsAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT symbol, side, round(usd_value,0) AS usd_value, ts FROM liquidations
                             ORDER BY ts DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Best / worst AI-scored tokens.</summary>
    public async Task<IReadOnlyList<dynamic>> GetScoredAsync(bool best, int limit, CancellationToken ct = default)
    {
        var sql = @"SELECT t.symbol, a.chain, a.score, a.verdict, a.summary,
                           round(s.price_usd,6) AS price, round(s.liq_usd,0) AS liq, round(s.chg_24h,2) AS chg24h
                    FROM token_ai_score a
                    JOIN tracked_tokens t USING (chain, token_address)
                    LEFT JOIN token_snapshot s USING (chain, token_address)
                    WHERE a.score IS NOT NULL
                    ORDER BY a.score " + (best ? "DESC" : "ASC") + " LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Tokens whose deployer has a rug history.</summary>
    public async Task<IReadOnlyList<dynamic>> GetDeployerRiskyAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT t.symbol, d.chain, left(d.deployer_address,12) AS deployer,
                                    d.tokens_deployed, d.est_rugpulls, d.wallet_age_months
                             FROM token_deployer d JOIN tracked_tokens t USING (chain, token_address)
                             WHERE d.est_rugpulls > 0 ORDER BY d.est_rugpulls DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Tracked tokens that have gone quiet.</summary>
    public async Task<IReadOnlyList<dynamic>> GetQuietTokensAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT t.symbol, s.chain, round(s.vol24h,0) AS vol24h, round(s.liq_usd,0) AS liq,
                                    t.fav_count, t.last_trade_utc
                             FROM tracked_tokens t JOIN token_snapshot s USING (chain, token_address)
                             WHERE t.is_active AND COALESCE(s.vol24h,0) < 1000
                             ORDER BY s.vol24h ASC NULLS FIRST LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Security problems found across tracked tokens.</summary>
    public async Task<IReadOnlyList<dynamic>> GetSecurityIssuesAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT t.symbol, sec.chain, sec.is_honeypot, round(sec.buy_tax,2) AS buy_tax,
                                    round(sec.sell_tax,2) AS sell_tax, sec.risk_label, sec.risk_score
                             FROM token_security sec JOIN tracked_tokens t USING (chain, token_address)
                             WHERE sec.is_honeypot OR sec.buy_tax > 10 OR sec.sell_tax > 10 OR sec.risk_score < 40
                             ORDER BY sec.risk_score ASC NULLS FIRST LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Whale-held supply concentration.</summary>
    public async Task<IReadOnlyList<dynamic>> GetConcentrationAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT t.symbol, h.chain, h.holder_count, round(h.top10_pct,1) AS top10_pct,
                                    round(s.liq_usd,0) AS liq
                             FROM token_holders h JOIN tracked_tokens t USING (chain, token_address)
                             LEFT JOIN token_snapshot s USING (chain, token_address)
                             WHERE h.top10_pct IS NOT NULL ORDER BY h.top10_pct DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Volume-to-liquidity outliers (churn vs depth).</summary>
    public async Task<IReadOnlyList<dynamic>> GetVolumeLiqRatioAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT t.symbol, s.chain, round(s.vol24h,0) AS vol24h, round(s.liq_usd,0) AS liq,
                                    round(s.vol24h / NULLIF(s.liq_usd,0), 2) AS vol_liq_ratio, round(s.chg_24h,2) AS chg24h
                             FROM token_snapshot s JOIN tracked_tokens t USING (chain, token_address)
                             WHERE t.is_active AND s.liq_usd > 0 AND s.vol24h > 0
                             ORDER BY s.vol24h / NULLIF(s.liq_usd,0) DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Small caps that still trade real volume.</summary>
    public async Task<IReadOnlyList<dynamic>> GetMicrocapsAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT t.symbol, m.chain, round(m.market_cap,0) AS mcap, round(s.vol24h,0) AS vol24h,
                                    round(s.liq_usd,0) AS liq, round(s.chg_24h,2) AS chg24h
                             FROM token_metadata m JOIN tracked_tokens t USING (chain, token_address)
                             JOIN token_snapshot s USING (chain, token_address)
                             WHERE m.market_cap > 0 AND m.market_cap < 5000000 AND s.vol24h > 10000
                             ORDER BY s.vol24h DESC LIMIT @limit;";
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
