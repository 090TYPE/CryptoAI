using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Spend for one feature over the reporting window.</summary>
public sealed record FeatureUsage(string? Feature, string? Model, long Calls, long InputTokens, long OutputTokens);

/// <summary>
/// Per-call AI usage. The vendor reports the token counts on every response and the proxy already
/// parses them; until now the only thing done with the number was to decrement a budget, so
/// nothing recorded what the spend was FOR. Every cost figure about this product was an estimate
/// with no way to close the error bar.
/// </summary>
public sealed class AiUsageRepository
{
    private readonly Db _db;

    public AiUsageRepository(Db db) => _db = db;

    /// <summary>
    /// Record one call. Never throws: this is bookkeeping attached to a request the customer has
    /// already been served, and losing a row of accounting is preferable to failing that request.
    /// </summary>
    public async Task LogAsync(string? license, string? feature, string? model, string vendor,
        long inputTokens, long outputTokens, CancellationToken ct = default)
    {
        if (inputTokens <= 0 && outputTokens <= 0) return;

        const string sql = @"
            INSERT INTO ai_usage (license, feature, model, vendor, input_tokens, output_tokens)
            VALUES (@license, @feature, @model, @vendor, @inputTokens, @outputTokens);";
        try
        {
            await using var conn = await _db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql,
                new { license, feature, model, vendor, inputTokens, outputTokens }, cancellationToken: ct));
        }
        catch
        {
            // Deliberately swallowed — see the summary above.
        }
    }

    /// <summary>Spend grouped by feature over the last N days, biggest first.</summary>
    public async Task<IReadOnlyList<FeatureUsage>> ByFeatureAsync(int days = 7, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT feature AS Feature,
                   -- The model can change under a feature mid-window; showing the most recent one
                   -- is more useful than showing several rows for what an operator thinks of as
                   -- one line item.
                   (SELECT model FROM ai_usage m WHERE m.feature IS NOT DISTINCT FROM u.feature
                      ORDER BY at_utc DESC LIMIT 1) AS Model,
                   COUNT(*) AS Calls,
                   SUM(input_tokens) AS InputTokens,
                   SUM(output_tokens) AS OutputTokens
            FROM ai_usage u
            WHERE at_utc >= now() - (@days || ' days')::interval
            GROUP BY feature
            ORDER BY SUM(input_tokens + output_tokens * 5) DESC;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FeatureUsage>(
            new CommandDefinition(sql, new { days = Math.Clamp(days, 1, 90) }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Drop rows past the retention window. Called from the same flusher that persists budgets, so
    /// nobody has to remember to prune a table that grows with every single AI call.
    /// </summary>
    public async Task PruneAsync(int keepDays = 90, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM ai_usage WHERE at_utc < now() - (@keepDays || ' days')::interval;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { keepDays = Math.Clamp(keepDays, 7, 400) }, cancellationToken: ct));
    }
}
