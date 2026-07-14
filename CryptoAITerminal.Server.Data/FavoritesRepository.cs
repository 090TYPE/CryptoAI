using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Per-user favorites. Writes fire the DB trigger that keeps tracked_tokens.fav_count in sync,
/// so adding/removing a favorite automatically starts/eventually-stops 24/7 tracking.
/// </summary>
public sealed class FavoritesRepository
{
    private readonly Db _db;
    public FavoritesRepository(Db db) => _db = db;

    public async Task AddAsync(Guid userId, string chain, string token, string? symbol, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO user_favorites (user_id, chain, token_address, symbol)
                             VALUES (@userId, @chain, @token, @symbol)
                             ON CONFLICT (user_id, chain, token_address) DO NOTHING;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, chain, token, symbol }, cancellationToken: ct));
    }

    public async Task RemoveAsync(Guid userId, string chain, string token, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM user_favorites
                             WHERE user_id = @userId AND chain = @chain AND token_address = @token;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, chain, token }, cancellationToken: ct));
    }

    /// <summary>Full sync: the client sends its whole watchlist; we add new ones and drop the rest.</summary>
    public async Task ReplaceAllAsync(Guid userId, IReadOnlyList<FavoriteInput> favorites, CancellationToken ct = default)
    {
        var chains = favorites.Select(f => f.Chain).ToArray();
        var tokens = favorites.Select(f => f.TokenAddress).ToArray();

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Remove favorites no longer in the client's list.
        await conn.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM user_favorites uf
            WHERE uf.user_id = @userId
              AND NOT EXISTS (
                SELECT 1 FROM unnest(@chains::text[], @tokens::text[]) AS k(c, t)
                WHERE k.c = uf.chain AND k.t = uf.token_address);",
            new { userId, chains, tokens }, tx, cancellationToken: ct));

        // Add the new ones (existing rows keep their added_utc).
        const string insert = @"INSERT INTO user_favorites (user_id, chain, token_address, symbol)
                                VALUES (@userId, @Chain, @TokenAddress, @Symbol)
                                ON CONFLICT (user_id, chain, token_address) DO NOTHING;";
        foreach (var f in favorites)
            await conn.ExecuteAsync(new CommandDefinition(insert,
                new { userId, f.Chain, f.TokenAddress, f.Symbol }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FavoriteView>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT f.chain AS Chain, f.token_address AS TokenAddress, f.symbol AS Symbol,
                   s.price_usd AS PriceUsd, s.chg_1h AS Chg1h, s.chg_24h AS Chg24h, s.vol24h AS Vol24h
            FROM user_favorites f
            LEFT JOIN token_snapshot s USING (chain, token_address)
            WHERE f.user_id = @userId
            ORDER BY f.added_utc DESC;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FavoriteView>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return rows.ToList();
    }
}
