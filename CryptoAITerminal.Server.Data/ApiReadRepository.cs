using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>One entry of a user's watchlist: which token, not what we know about it.</summary>
public sealed record WatchlistTokenId(string Chain, string TokenAddress, string? Symbol);

/// <summary>One collector's last outcome, with how long ago it ran.</summary>
public sealed record CollectorHealth(
    string Name, DateTime? LastRunUtc, bool? LastOk, string? LastError, int? Items, double? MinutesAgo);

/// <summary>Read-side queries the public API serves (token detail, news, sentiment).</summary>
public sealed class ApiReadRepository
{
    private readonly Db _db;
    public ApiReadRepository(Db db) => _db = db;

    public async Task<TokenDetail?> GetTokenDetailAsync(string chain, string token, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT t.chain AS Chain, t.token_address AS TokenAddress,
                   COALESCE(m.symbol, t.symbol) AS Symbol, m.name AS Name,
                   s.price_usd AS PriceUsd, s.liq_usd AS LiqUsd, s.vol24h AS Vol24h,
                   s.chg_5m AS Chg5m, s.chg_1h AS Chg1h, s.chg_24h AS Chg24h, m.market_cap AS MarketCap,
                   m.image_url AS ImageUrl, m.website AS Website, m.twitter AS Twitter, m.telegram AS Telegram,
                   sec.is_honeypot AS IsHoneypot, sec.buy_tax AS BuyTax, sec.sell_tax AS SellTax,
                   sec.risk_label AS RiskLabel, sec.risk_score AS RiskScore,
                   h.holder_count AS HolderCount, h.top10_pct AS HoldersTop10Pct,
                   d.deployer_address AS DeployerAddress, d.tokens_deployed AS DeployerTokensDeployed,
                   d.est_rugpulls AS DeployerRugpulls, d.wallet_age_months AS DeployerWalletAgeMonths,
                   ai.score AS AiScore, ai.verdict AS AiVerdict, ai.summary AS AiSummary
            FROM tracked_tokens t
            LEFT JOIN token_snapshot s   USING (chain, token_address)
            LEFT JOIN token_metadata m   USING (chain, token_address)
            LEFT JOIN token_security sec USING (chain, token_address)
            LEFT JOIN token_holders h    USING (chain, token_address)
            LEFT JOIN token_deployer d   USING (chain, token_address)
            LEFT JOIN token_ai_score ai  USING (chain, token_address)
            WHERE t.chain = @chain AND t.token_address = @token;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TokenDetail>(
            new CommandDefinition(sql, new { chain, token }, cancellationToken: ct));
    }

    /// <summary>
    /// Batched <see cref="GetTokenDetailAsync"/>: one round trip for a whole watchlist instead of
    /// N single-row reads. Only ever called with the tokens that MISSED the shared cache, so a
    /// warm server passes an empty or near-empty list here.
    /// </summary>
    public async Task<IReadOnlyList<TokenDetail>> GetTokenDetailsAsync(
        IReadOnlyList<WatchlistTokenId> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<TokenDetail>();

        const string sql = @"
            SELECT t.chain AS Chain, t.token_address AS TokenAddress,
                   COALESCE(m.symbol, t.symbol) AS Symbol, m.name AS Name,
                   s.price_usd AS PriceUsd, s.liq_usd AS LiqUsd, s.vol24h AS Vol24h,
                   s.chg_5m AS Chg5m, s.chg_1h AS Chg1h, s.chg_24h AS Chg24h, m.market_cap AS MarketCap,
                   m.image_url AS ImageUrl, m.website AS Website, m.twitter AS Twitter, m.telegram AS Telegram,
                   sec.is_honeypot AS IsHoneypot, sec.buy_tax AS BuyTax, sec.sell_tax AS SellTax,
                   sec.risk_label AS RiskLabel, sec.risk_score AS RiskScore,
                   h.holder_count AS HolderCount, h.top10_pct AS HoldersTop10Pct,
                   d.deployer_address AS DeployerAddress, d.tokens_deployed AS DeployerTokensDeployed,
                   d.est_rugpulls AS DeployerRugpulls, d.wallet_age_months AS DeployerWalletAgeMonths,
                   ai.score AS AiScore, ai.verdict AS AiVerdict, ai.summary AS AiSummary
            FROM tracked_tokens t
            LEFT JOIN token_snapshot s   USING (chain, token_address)
            LEFT JOIN token_metadata m   USING (chain, token_address)
            LEFT JOIN token_security sec USING (chain, token_address)
            LEFT JOIN token_holders h    USING (chain, token_address)
            LEFT JOIN token_deployer d   USING (chain, token_address)
            LEFT JOIN token_ai_score ai  USING (chain, token_address)
            WHERE (t.chain, t.token_address) IN (
                SELECT c, a FROM unnest(@chains::text[], @tokens::text[]) AS u(c, a)
            );";

        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TokenDetail>(new CommandDefinition(sql, new
        {
            chains = ids.Select(i => i.Chain).ToArray(),
            tokens = ids.Select(i => i.TokenAddress).ToArray(),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// The (chain, token) pairs a user has favourited — the ONLY per-user part of the watchlist
    /// tab. Deliberately not cached: it is an index scan over a handful of rows keyed by the
    /// primary key, so caching it would buy nothing and cost an invalidation path on every
    /// favourites write.
    /// </summary>
    public async Task<IReadOnlyList<WatchlistTokenId>> GetWatchlistIdsAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT chain AS Chain, token_address AS TokenAddress, symbol AS Symbol
                             FROM user_favorites
                             WHERE user_id = @userId
                             ORDER BY chain, token_address;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<WatchlistTokenId>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Cheapest possible liveness probe. /health used to answer 200 unconditionally, so a monitor
    /// watching it would report green with Postgres flat on its back.
    /// </summary>
    public async Task<bool> IsDatabaseReachableAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync(ct);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1;", cancellationToken: ct));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// What every collector did last, and how long ago. collector_runs was already written on every
    /// pass and read by nothing, so a collector that quietly died stayed dead until somebody noticed
    /// the data was old.
    /// </summary>
    public async Task<IReadOnlyList<CollectorHealth>> GetCollectorHealthAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT name AS Name, last_run_utc AS LastRunUtc, last_ok AS LastOk,
                                    last_error AS LastError, items AS Items,
                                    -- Cast on the Postgres side: EXTRACT yields numeric, which
                                    -- arrives as decimal and does not convert to the double? this
                                    -- record declares.
                                    (EXTRACT(EPOCH FROM (now() - last_run_utc)) / 60.0)::float8 AS MinutesAgo
                             FROM collector_runs
                             ORDER BY last_ok NULLS FIRST, last_run_utc NULLS FIRST;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CollectorHealth>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Latest gas price per chain.</summary>
    public async Task<IReadOnlyList<GasPoint>> GetGasAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT DISTINCT ON (chain) chain AS Chain, standard AS Standard, ts AS Ts
                             FROM gas_prices ORDER BY chain, ts DESC;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<GasPoint>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    /// <summary>Latest reading of each on-chain metric for an asset.</summary>
    public async Task<IReadOnlyList<OnChainPoint>> GetOnChainAsync(string asset, CancellationToken ct = default)
    {
        const string sql = @"SELECT DISTINCT ON (metric) asset AS Asset, metric AS Metric, value AS Value, ts AS Ts
                             FROM onchain_metrics WHERE asset = @asset ORDER BY metric, ts DESC;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<OnChainPoint>(new CommandDefinition(sql, new { asset }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<WhaleTx>> GetWhalesAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT tx_hash AS TxHash, chain AS Chain, token_symbol AS TokenSymbol,
                                    from_address AS FromAddress, to_address AS ToAddress, usd_value AS UsdValue, ts AS Ts
                             FROM whale_txs ORDER BY ts DESC NULLS LAST LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<WhaleTx>(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<LiquidationPoint>> GetLiquidationsAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT symbol AS Symbol, side AS Side, usd_value AS UsdValue, ts AS Ts
                             FROM liquidations ORDER BY ts DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<LiquidationPoint>(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"SELECT source AS Source, title AS Title, url AS Url,
                                    summary AS Summary, published_utc AS PublishedUtc
                             FROM news
                             ORDER BY COALESCE(published_utc, fetched_utc) DESC
                             LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<NewsItem>(new CommandDefinition(sql, new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Latest reading of each sentiment metric.</summary>
    public async Task<IReadOnlyList<SentimentPoint>> GetLatestSentimentAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT DISTINCT ON (metric)
                                    metric AS Metric, value AS Value, label AS Label, ts AS Ts
                             FROM sentiment
                             ORDER BY metric, ts DESC;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SentimentPoint>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }
}
