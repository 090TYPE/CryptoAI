using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// How far to back off polling a token nobody is looking at. Windows are measured against
/// <c>tracked_tokens.last_read_utc</c>, which the read API stamps on a cache miss.
/// </summary>
/// <param name="HotWindowS">Read this recently → keep the token's own poll_interval_s.</param>
/// <param name="WarmWindowS">Read this recently → <paramref name="WarmIntervalS"/>.</param>
/// <param name="WarmIntervalS">Reschedule for recently-but-not-currently watched tokens.</param>
/// <param name="ColdIntervalS">Reschedule for tokens never read, or not read in a long time.</param>
public sealed record PollDemand(int HotWindowS, int WarmWindowS, int WarmIntervalS, int ColdIntervalS)
{
    /// <summary>Watched → 60s (as configured), open in the last hour → 5 min, otherwise → 15 min.</summary>
    public static readonly PollDemand Default = new(HotWindowS: 300, WarmWindowS: 3600,
                                                    WarmIntervalS: 300, ColdIntervalS: 900);
}

/// <summary>
/// Drives the 24/7 poll registry (<c>tracked_tokens</c>). The claim query atomically
/// selects due tokens AND pushes their next_poll forward, so concurrent workers never
/// grab the same row and a failed fetch naturally backs off to the next interval.
/// </summary>
public sealed class TrackedTokenRepository
{
    private readonly Db _db;

    public TrackedTokenRepository(Db db) => _db = db;

    /// <summary>
    /// Claim up to <paramref name="batch"/> tokens that are due, rescheduling them in the
    /// same statement (CTE + FOR UPDATE SKIP LOCKED). Returns what was claimed.
    /// </summary>
    public Task<IReadOnlyList<DueToken>> ClaimDueAsync(int batch, CancellationToken ct = default)
        => ClaimDueAsync(batch, PollDemand.Default, ct);

    /// <summary>
    /// Claim due tokens, rescheduling each by DEMAND rather than by a flat interval: a chart
    /// somebody has open keeps its full cadence, one nobody has touched in an hour backs off.
    /// With a favourites set much larger than the set actually on screen, this is where most
    /// of the upstream quota and most of the candle writes are saved.
    ///
    /// A token with an ACTIVE price alert is never backed off — the AlertCollector reads
    /// <c>token_snapshot</c> every 20s and a stale snapshot means the alert fires late.
    /// </summary>
    public async Task<IReadOnlyList<DueToken>> ClaimDueAsync(int batch, PollDemand demand, CancellationToken ct = default)
    {
        // NULL last_read_utc makes both comparisons NULL, so never-read tokens fall to ELSE (cold).
        const string sql = @"
            WITH due AS (
                SELECT chain, token_address
                FROM tracked_tokens
                WHERE is_active AND next_poll_utc <= now()
                ORDER BY next_poll_utc
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
            )
            UPDATE tracked_tokens t
               SET next_poll_utc = now() + make_interval(secs => CASE
                     WHEN EXISTS (SELECT 1 FROM price_alerts a
                                   WHERE a.chain = t.chain
                                     AND a.token_address = t.token_address
                                     AND a.status = 'active')
                          THEN t.poll_interval_s
                     -- A token favourited moments ago IS demand, but it has no last_read_utc yet,
                     -- so without this it fell straight to the cold tier and waited 15 minutes for
                     -- its first candles — and its very first poll usually finds nothing, because
                     -- the pool address has not been resolved that early.
                     WHEN t.added_utc > now() - make_interval(secs => @hotWindowS)
                          THEN t.poll_interval_s
                     WHEN t.last_read_utc > now() - make_interval(secs => @hotWindowS)
                          THEN t.poll_interval_s
                     WHEN t.last_read_utc > now() - make_interval(secs => @warmWindowS)
                          THEN GREATEST(t.poll_interval_s, @warmIntervalS)
                     ELSE GREATEST(t.poll_interval_s, @coldIntervalS)
                   END)
              FROM due
             WHERE t.chain = due.chain AND t.token_address = due.token_address
            RETURNING t.chain AS Chain, t.token_address AS TokenAddress,
                      t.pool_address AS PoolAddress, t.poll_interval_s AS PollIntervalS;";

        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DueToken>(new CommandDefinition(sql, new
        {
            batch,
            hotWindowS = demand.HotWindowS,
            warmWindowS = demand.WarmWindowS,
            warmIntervalS = demand.WarmIntervalS,
            coldIntervalS = demand.ColdIntervalS,
        }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Stamp that a user actually read this token, which is what keeps it on the fast poll
    /// cadence. Called from the read API on a shared-cache MISS, so it costs at most one
    /// UPDATE per cache TTL per token however many terminals are watching.
    /// </summary>
    public async Task TouchReadAsync(string chain, string token, CancellationToken ct = default)
    {
        const string sql = @"UPDATE tracked_tokens SET last_read_utc = now()
                             WHERE chain = @chain AND token_address = @token AND is_active;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { chain, token }, cancellationToken: ct));
    }

    /// <summary>
    /// Batched <see cref="TouchReadAsync"/> for a whole watchlist. Called with the tokens that
    /// missed the shared cache, so one statement covers the whole page and a warm server does
    /// no work at all.
    /// </summary>
    public async Task TouchReadManyAsync(IReadOnlyList<WatchlistTokenId> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;

        const string sql = @"
            UPDATE tracked_tokens t SET last_read_utc = now()
             FROM unnest(@chains::text[], @tokens::text[]) AS u(c, a)
            WHERE t.chain = u.c AND t.token_address = u.a AND t.is_active;";

        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            chains = ids.Select(i => i.Chain).ToArray(),
            tokens = ids.Select(i => i.TokenAddress).ToArray(),
        }, cancellationToken: ct));
    }

    /// <summary>All active tracked tokens (for the data collectors, grouped by chain by the caller).</summary>
    public async Task<IReadOnlyList<ActiveToken>> ListActiveAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT chain AS Chain, token_address AS TokenAddress, pool_address AS PoolAddress
                             FROM tracked_tokens WHERE is_active;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ActiveToken>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Ensure a token is tracked so the market collector fills its snapshot (for alerts/manual adds).</summary>
    public async Task EnsureTrackedAsync(string chain, string token, string? symbol, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO tracked_tokens (chain, token_address, symbol, source, is_active, next_poll_utc)
            VALUES (@chain, @token, @symbol, 'manual', true, now())
            ON CONFLICT (chain, token_address) DO UPDATE
              SET is_active = true,
                  symbol = COALESCE(NULLIF(EXCLUDED.symbol, ''), tracked_tokens.symbol);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { chain, token, symbol }, cancellationToken: ct));
    }

    /// <summary>Persist the resolved primary pool for a token (done once).</summary>
    public async Task SetPoolAsync(string chain, string token, string poolAddress, CancellationToken ct = default)
    {
        const string sql = @"UPDATE tracked_tokens SET pool_address = @poolAddress
                             WHERE chain = @chain AND token_address = @token;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { chain, token, poolAddress }, cancellationToken: ct));
    }

    /// <summary>Record a successful poll (last_polled / last_trade for liveness tracking).</summary>
    public async Task MarkPolledAsync(string chain, string token, DateTime? lastTradeUtc, CancellationToken ct = default)
    {
        const string sql = @"UPDATE tracked_tokens
                                SET last_polled_utc = now(), last_trade_utc = @lastTradeUtc
                              WHERE chain = @chain AND token_address = @token;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { chain, token, lastTradeUtc }, cancellationToken: ct));
    }

    /// <summary>Stop polling tokens nobody favorites anymore (and that aren't trending). Returns rows deactivated.</summary>
    public async Task<int> DeactivateOrphansAsync(CancellationToken ct = default)
    {
        const string sql = @"UPDATE tracked_tokens SET is_active = false
                             WHERE is_active AND fav_count = 0 AND source NOT IN ('trending', 'manual');";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }
}
