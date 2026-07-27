using CryptoAITerminal.Server.Common;
using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Durable side of the per-licence daily AI budget. The live counter stays in memory — the hot
/// path must not wait on a database — and this loads it at startup and writes it back behind.
/// </summary>
public sealed class AiBudgetRepository
{
    private readonly Db _db;

    public AiBudgetRepository(Db db) => _db = db;

    /// <summary>Today's totals, to seed the in-memory counters after a restart.</summary>
    public async Task<IReadOnlyList<AiBudget.Entry>> LoadAsync(DateOnly day, CancellationToken ct = default)
    {
        const string sql = "SELECT license, day, tokens FROM ai_budget_daily WHERE day = @day;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string License, DateTime Day, long Tokens)>(
            new CommandDefinition(sql, new { day }, cancellationToken: ct));
        return rows.Select(r => new AiBudget.Entry(r.License, DateOnly.FromDateTime(r.Day), r.Tokens)).ToList();
    }

    /// <summary>
    /// Write the current totals back.
    ///
    /// GREATEST rather than a plain assignment: two API processes behind the same database each
    /// hold their own in-memory counter, and a straight write would let the one that flushed last
    /// erase the other's spend. Taking the larger value never hands allowance back, which is the
    /// direction that matters — undercounting is what costs money.
    /// </summary>
    public async Task FlushAsync(IReadOnlyList<AiBudget.Entry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        const string sql = @"
            INSERT INTO ai_budget_daily (license, day, tokens, updated_utc)
            VALUES (@License, @Day, @Tokens, now())
            ON CONFLICT (license, day) DO UPDATE
              SET tokens = GREATEST(ai_budget_daily.tokens, EXCLUDED.tokens),
                  updated_utc = now();";

        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, entries, cancellationToken: ct));
    }

    /// <summary>Biggest spenders for a day, for the admin panel.</summary>
    public async Task<IReadOnlyList<(string License, long Tokens)>> TopSpendersAsync(DateOnly day, int limit = 20, CancellationToken ct = default)
    {
        const string sql = @"SELECT license, tokens FROM ai_budget_daily
                             WHERE day = @day ORDER BY tokens DESC LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string License, long Tokens)>(
            new CommandDefinition(sql, new { day, limit = Math.Clamp(limit, 1, 200) }, cancellationToken: ct));
        return rows.ToList();
    }
}
