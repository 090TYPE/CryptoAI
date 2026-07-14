using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Per-chain gas price samples (gwei).</summary>
public sealed class GasRepository
{
    private readonly Db _db;
    public GasRepository(Db db) => _db = db;

    public async Task InsertAsync(string chain, decimal? fast, decimal? standard, decimal? slow, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO gas_prices (chain, ts, fast, standard, slow)
                             VALUES (@chain, now(), @fast, @standard, @slow)
                             ON CONFLICT (chain, ts) DO NOTHING;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { chain, fast, standard, slow }, cancellationToken: ct));
    }
}
