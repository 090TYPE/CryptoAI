using Dapper;

namespace CryptoAITerminal.Server.Data;

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
    public async Task<IReadOnlyList<DueToken>> ClaimDueAsync(int batch, CancellationToken ct = default)
    {
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
               SET next_poll_utc = now() + make_interval(secs => t.poll_interval_s)
              FROM due
             WHERE t.chain = due.chain AND t.token_address = due.token_address
            RETURNING t.chain AS Chain, t.token_address AS TokenAddress,
                      t.pool_address AS PoolAddress, t.poll_interval_s AS PollIntervalS;";

        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DueToken>(new CommandDefinition(sql, new { batch }, cancellationToken: ct));
        return rows.ToList();
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
