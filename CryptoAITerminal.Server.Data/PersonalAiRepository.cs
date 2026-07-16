using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Per-user AI: facts about one user's own watchlist (unlike the shared digests).</summary>
public sealed class PersonalAiRepository
{
    private readonly Db _db;
    public PersonalAiRepository(Db db) => _db = db;

    /// <summary>Users with favorites who haven't had a portfolio review in the last N days.</summary>
    public async Task<IReadOnlyList<Guid>> GetUsersDueForReviewAsync(int days, int limit, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT f.user_id
            FROM user_favorites f
            WHERE NOT EXISTS (
                SELECT 1 FROM inbox i
                WHERE i.user_id = f.user_id AND i.kind = 'portfolio_review'
                  AND i.created_utc > now() - make_interval(days => @days))
            LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { days, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Everything we know about the tokens this user follows.</summary>
    public async Task<IReadOnlyList<dynamic>> GetUserPortfolioFactsAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT f.symbol, f.chain,
                   round(s.price_usd,6) AS price, round(s.chg_24h,2) AS chg24h,
                   round(s.liq_usd,0) AS liq, round(s.vol24h,0) AS vol24h,
                   sec.risk_label, sec.is_honeypot, round(sec.sell_tax,2) AS sell_tax,
                   h.holder_count, round(h.top10_pct,1) AS top10_pct,
                   a.score AS ai_score, a.verdict AS ai_verdict
            FROM user_favorites f
            LEFT JOIN token_snapshot s   USING (chain, token_address)
            LEFT JOIN token_security sec USING (chain, token_address)
            LEFT JOIN token_holders h    USING (chain, token_address)
            LEFT JOIN token_ai_score a   USING (chain, token_address)
            WHERE f.user_id = @userId
            LIMIT 40;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync(new CommandDefinition(sql, new { userId }, cancellationToken: ct))).ToList();
    }
}
