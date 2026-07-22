using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>A trailing bot's managed position + its live TP/SL/trailing state (tsl_positions).</summary>
public sealed record TslPositionRow(
    Guid BotId, Guid UserId, string Symbol, bool IsLong, decimal Entry, bool Futures,
    decimal Peak, decimal Sl, decimal RemainingQty, decimal TpPercent, bool PartialDone, bool Closed);

/// <summary>One managed position per trailing bot; upserted every tick so restarts resume cleanly.</summary>
public sealed class TslPositionsRepository
{
    private readonly Db _db;
    public TslPositionsRepository(Db db) => _db = db;

    public async Task<TslPositionRow?> LoadAsync(Guid botId, CancellationToken ct = default)
    {
        const string sql = @"SELECT bot_id AS BotId, user_id AS UserId, symbol AS Symbol, is_long AS IsLong,
                                    entry AS Entry, futures AS Futures, peak AS Peak, sl AS Sl,
                                    remaining_qty AS RemainingQty, tp_percent AS TpPercent,
                                    partial_done AS PartialDone, closed AS Closed
                             FROM tsl_positions WHERE bot_id = @botId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TslPositionRow>(new CommandDefinition(sql, new { botId }, cancellationToken: ct));
    }

    public async Task UpsertAsync(TslPositionRow p, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO tsl_positions
                               (bot_id, user_id, symbol, is_long, entry, futures,
                                peak, sl, remaining_qty, tp_percent, partial_done, closed, updated_utc)
                             VALUES
                               (@BotId, @UserId, @Symbol, @IsLong, @Entry, @Futures,
                                @Peak, @Sl, @RemainingQty, @TpPercent, @PartialDone, @Closed, now())
                             ON CONFLICT (bot_id) DO UPDATE SET
                                symbol = EXCLUDED.symbol, is_long = EXCLUDED.is_long, entry = EXCLUDED.entry,
                                futures = EXCLUDED.futures, peak = EXCLUDED.peak, sl = EXCLUDED.sl,
                                remaining_qty = EXCLUDED.remaining_qty, tp_percent = EXCLUDED.tp_percent,
                                partial_done = EXCLUDED.partial_done, closed = EXCLUDED.closed, updated_utc = now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct));
    }
}
