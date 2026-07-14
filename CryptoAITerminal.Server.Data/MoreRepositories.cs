using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Token deployer reputation.</summary>
public sealed class DeployerRepository
{
    private readonly Db _db;
    public DeployerRepository(Db db) => _db = db;

    public async Task UpsertAsync(string chain, string token, string deployer,
        int tokensDeployed, int estRugpulls, int walletAgeMonths, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO token_deployer (chain, token_address, deployer_address, tokens_deployed, est_rugpulls, wallet_age_months, updated_utc)
            VALUES (@chain, @token, @deployer, @tokensDeployed, @estRugpulls, @walletAgeMonths, now())
            ON CONFLICT (chain, token_address) DO UPDATE SET
              deployer_address=EXCLUDED.deployer_address, tokens_deployed=EXCLUDED.tokens_deployed,
              est_rugpulls=EXCLUDED.est_rugpulls, wallet_age_months=EXCLUDED.wallet_age_months, updated_utc=now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { chain, token, deployer, tokensDeployed, estRugpulls, walletAgeMonths }, cancellationToken: ct));
    }
}

/// <summary>Holder distribution.</summary>
public sealed class HoldersRepository
{
    private readonly Db _db;
    public HoldersRepository(Db db) => _db = db;

    public async Task UpsertAsync(string chain, string token, long? holderCount,
        decimal? top10, decimal? top1130, decimal? top3150, decimal? rest, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO token_holders (chain, token_address, holder_count, top10_pct, top11_30_pct, top31_50_pct, rest_pct, updated_utc)
            VALUES (@chain, @token, @holderCount, @top10, @top1130, @top3150, @rest, now())
            ON CONFLICT (chain, token_address) DO UPDATE SET
              holder_count=COALESCE(EXCLUDED.holder_count, token_holders.holder_count),
              top10_pct=EXCLUDED.top10_pct, top11_30_pct=EXCLUDED.top11_30_pct,
              top31_50_pct=EXCLUDED.top31_50_pct, rest_pct=EXCLUDED.rest_pct, updated_utc=now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { chain, token, holderCount, top10, top1130, top3150, rest }, cancellationToken: ct));
    }
}

/// <summary>Large ("whale") transfers, deduped by tx hash.</summary>
public sealed class WhaleRepository
{
    private readonly Db _db;
    public WhaleRepository(Db db) => _db = db;

    public async Task<int> InsertIfNewAsync(string txHash, string chain, string? symbol,
        string? from, string? to, decimal amount, decimal usdValue, DateTime ts, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO whale_txs (tx_hash, chain, token_symbol, from_address, to_address, amount, usd_value, ts)
            VALUES (@txHash, @chain, @symbol, @from, @to, @amount, @usdValue, @ts)
            ON CONFLICT (tx_hash) DO NOTHING;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { txHash, chain, symbol, from, to, amount, usdValue, ts }, cancellationToken: ct));
    }
}

/// <summary>On-chain time-series metrics.</summary>
public sealed class OnChainRepository
{
    private readonly Db _db;
    public OnChainRepository(Db db) => _db = db;

    public async Task UpsertAsync(string asset, string metric, DateTime ts, decimal? value, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO onchain_metrics (asset, metric, ts, value)
                             VALUES (@asset, @metric, @ts, @value)
                             ON CONFLICT (asset, metric, ts) DO UPDATE SET value=EXCLUDED.value;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { asset, metric, ts, value }, cancellationToken: ct));
    }
}

/// <summary>Derivatives liquidations.</summary>
public sealed class LiquidationsRepository
{
    private readonly Db _db;
    public LiquidationsRepository(Db db) => _db = db;

    public async Task<int> InsertIfNewAsync(string symbol, string? side, decimal usdValue, DateTime ts, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO liquidations (symbol, side, usd_value, ts)
                             VALUES (@symbol, @side, @usdValue, @ts)
                             ON CONFLICT (symbol, side, ts) DO NOTHING;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { symbol, side, usdValue, ts }, cancellationToken: ct));
    }
}
