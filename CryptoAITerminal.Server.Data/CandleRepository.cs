using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Writes raw 1m candles into the <c>dex_candles</c> hypertable and keeps
/// <c>token_snapshot</c> fresh. Upserts only ever hit recent (uncompressed) chunks,
/// so <c>ON CONFLICT</c> stays valid.
/// </summary>
public sealed class CandleRepository
{
    private readonly Db _db;

    public CandleRepository(Db db) => _db = db;

    /// <summary>Merge a batch of candles for one token. Idempotent per (chain, token, ts).</summary>
    public async Task<int> UpsertCandlesAsync(
        string chain, string token, IEnumerable<CandleRow> candles, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO dex_candles (chain, token_address, ts, o, h, l, c, v)
            VALUES (@chain, @token, @Ts, @O, @H, @L, @C, @V)
            ON CONFLICT (chain, token_address, ts) DO UPDATE
              SET h = GREATEST(dex_candles.h, EXCLUDED.h),
                  l = LEAST   (dex_candles.l, EXCLUDED.l),
                  c = EXCLUDED.c,
                  v = EXCLUDED.v;";

        var rows = candles.ToList();
        if (rows.Count == 0) return 0;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var affected = 0;
        foreach (var r in rows)
        {
            affected += await conn.ExecuteAsync(new CommandDefinition(sql,
                new { chain, token, r.Ts, r.O, r.H, r.L, r.C, r.V }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return affected;
    }

    // Whitelist of timeframe -> table/view. Prevents SQL injection on the tf query param.
    private static readonly IReadOnlyDictionary<string, string> TfTables =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1m"] = "dex_candles",
            ["5m"] = "dex_candles_5m",
            ["15m"] = "dex_candles_15m",
            ["1h"] = "dex_candles_1h",
            ["4h"] = "dex_candles_4h",
            ["1d"] = "dex_candles_1d",
        };

    public static bool IsValidTimeframe(string tf) => TfTables.ContainsKey(tf);

    /// <summary>Read candles for a chart at the given timeframe.</summary>
    public async Task<IReadOnlyList<CandleRow>> GetCandlesAsync(
        string tf, string chain, string token, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (!TfTables.TryGetValue(tf, out var table))
            throw new ArgumentException($"Unknown timeframe '{tf}'.", nameof(tf));

        var sql = $@"SELECT ts AS Ts, o AS O, h AS H, l AS L, c AS C, v AS V
                     FROM {table}
                     WHERE chain = @chain AND token_address = @token AND ts >= @fromUtc AND ts < @toUtc
                     ORDER BY ts;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CandleRow>(new CommandDefinition(sql,
            new { chain, token, fromUtc, toUtc }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Upsert the latest metrics snapshot used by the favorites list.</summary>
    public async Task UpdateSnapshotAsync(
        string chain, string token,
        decimal? priceUsd, decimal? liqUsd, decimal? vol24h,
        decimal? chg5m, decimal? chg1h, decimal? chg24h,
        CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO token_snapshot
                (chain, token_address, price_usd, liq_usd, vol24h, chg_5m, chg_1h, chg_24h, updated_utc)
            VALUES (@chain, @token, @priceUsd, @liqUsd, @vol24h, @chg5m, @chg1h, @chg24h, now())
            ON CONFLICT (chain, token_address) DO UPDATE
              SET price_usd = EXCLUDED.price_usd,
                  liq_usd   = EXCLUDED.liq_usd,
                  vol24h    = EXCLUDED.vol24h,
                  chg_5m    = EXCLUDED.chg_5m,
                  chg_1h    = EXCLUDED.chg_1h,
                  chg_24h   = EXCLUDED.chg_24h,
                  updated_utc = now();";

        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { chain, token, priceUsd, liqUsd, vol24h, chg5m, chg1h, chg24h }, cancellationToken: ct));
    }
}
