using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>A token that needs (re)scoring.</summary>
public sealed record ScoreTarget(string Chain, string TokenAddress);

/// <summary>Cached AI verdicts per token — scored once by the server, served to every user.</summary>
public sealed class AiScoreRepository
{
    private readonly Db _db;
    public AiScoreRepository(Db db) => _db = db;

    /// <summary>Active tracked tokens with no score, or a score older than <paramref name="maxAgeHours"/>.</summary>
    public async Task<IReadOnlyList<ScoreTarget>> GetStaleAsync(int maxAgeHours, int limit, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT t.chain AS Chain, t.token_address AS TokenAddress
            FROM tracked_tokens t
            LEFT JOIN token_ai_score s USING (chain, token_address)
            WHERE t.is_active
              AND (s.updated_utc IS NULL OR s.updated_utc < now() - make_interval(hours => @maxAgeHours))
            ORDER BY t.fav_count DESC
            LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ScoreTarget>(new CommandDefinition(sql, new { maxAgeHours, limit }, cancellationToken: ct))).ToList();
    }

    public async Task UpsertAsync(string chain, string token, int? score, string? verdict, string? summary,
        string? model, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO token_ai_score (chain, token_address, score, verdict, summary, model, updated_utc)
            VALUES (@chain, @token, @score, @verdict, @summary, @model, now())
            ON CONFLICT (chain, token_address) DO UPDATE
              SET score=EXCLUDED.score, verdict=EXCLUDED.verdict, summary=EXCLUDED.summary,
                  model=EXCLUDED.model, updated_utc=now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { chain, token, score, verdict, summary, model }, cancellationToken: ct));
    }
}
