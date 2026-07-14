using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Static-ish token info (name/symbol/socials/primary pool/market cap).</summary>
public sealed class MetadataRepository
{
    private readonly Db _db;
    public MetadataRepository(Db db) => _db = db;

    public async Task UpsertAsync(
        string chain, string token, string? name, string? symbol,
        string? imageUrl, string? description, string? website, string? twitter, string? telegram,
        string? pairAddress, decimal? marketCap, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO token_metadata
              (chain, token_address, name, symbol, image_url, description, website, twitter, telegram, pair_address, market_cap, updated_utc)
            VALUES (@chain,@token,@name,@symbol,@imageUrl,@description,@website,@twitter,@telegram,@pairAddress,@marketCap, now())
            ON CONFLICT (chain, token_address) DO UPDATE SET
              name=COALESCE(EXCLUDED.name, token_metadata.name),
              symbol=COALESCE(EXCLUDED.symbol, token_metadata.symbol),
              image_url=COALESCE(EXCLUDED.image_url, token_metadata.image_url),
              description=COALESCE(EXCLUDED.description, token_metadata.description),
              website=COALESCE(EXCLUDED.website, token_metadata.website),
              twitter=COALESCE(EXCLUDED.twitter, token_metadata.twitter),
              telegram=COALESCE(EXCLUDED.telegram, token_metadata.telegram),
              pair_address=COALESCE(EXCLUDED.pair_address, token_metadata.pair_address),
              market_cap=COALESCE(EXCLUDED.market_cap, token_metadata.market_cap),
              updated_utc=now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { chain, token, name, symbol, imageUrl, description, website, twitter, telegram, pairAddress, marketCap }, cancellationToken: ct));
    }
}

/// <summary>GoPlus/Honeypot/RugCheck verdicts per token.</summary>
public sealed class SecurityRepository
{
    private readonly Db _db;
    public SecurityRepository(Db db) => _db = db;

    public async Task UpsertAsync(
        string chain, string token, bool? isHoneypot, decimal? buyTax, decimal? sellTax,
        string? riskLabel, int? riskScore, string? detailsJson, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO token_security
              (chain, token_address, is_honeypot, buy_tax, sell_tax, risk_label, risk_score, details, updated_utc)
            VALUES (@chain,@token,@isHoneypot,@buyTax,@sellTax,@riskLabel,@riskScore,@detailsJson::jsonb, now())
            ON CONFLICT (chain, token_address) DO UPDATE SET
              is_honeypot=EXCLUDED.is_honeypot, buy_tax=EXCLUDED.buy_tax, sell_tax=EXCLUDED.sell_tax,
              risk_label=EXCLUDED.risk_label, risk_score=EXCLUDED.risk_score, details=EXCLUDED.details, updated_utc=now();";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { chain, token, isHoneypot, buyTax, sellTax, riskLabel, riskScore, detailsJson }, cancellationToken: ct));
    }
}

/// <summary>Aggregated news feed. De-dupes by URL.</summary>
public sealed class NewsRepository
{
    private readonly Db _db;
    public NewsRepository(Db db) => _db = db;

    /// <summary>Insert if the URL is new. Returns 1 if inserted, 0 if it already existed.</summary>
    public async Task<int> InsertIfNewAsync(
        string source, string title, string url, string? summary, DateTime? publishedUtc, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO news (source, title, url, summary, published_utc)
            VALUES (@source, @title, @url, @summary, @publishedUtc)
            ON CONFLICT (url) DO NOTHING;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { source, title, url, summary, publishedUtc }, cancellationToken: ct));
    }
}

/// <summary>Market sentiment metrics (fear&greed, …).</summary>
public sealed class SentimentRepository
{
    private readonly Db _db;
    public SentimentRepository(Db db) => _db = db;

    public async Task UpsertAsync(string metric, DateTime ts, decimal? value, string? label, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO sentiment (metric, ts, value, label)
            VALUES (@metric, @ts, @value, @label)
            ON CONFLICT (metric, ts) DO UPDATE SET value=EXCLUDED.value, label=EXCLUDED.label;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { metric, ts, value, label }, cancellationToken: ct));
    }
}

/// <summary>Per-collector observability (last run / ok / error / item count).</summary>
public sealed class CollectorRunsRepository
{
    private readonly Db _db;
    public CollectorRunsRepository(Db db) => _db = db;

    public async Task RecordAsync(string name, bool ok, string? error, int items, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO collector_runs (name, last_run_utc, last_ok, last_error, items)
            VALUES (@name, now(), @ok, @error, @items)
            ON CONFLICT (name) DO UPDATE SET
              last_run_utc=now(), last_ok=EXCLUDED.last_ok, last_error=EXCLUDED.last_error, items=EXCLUDED.items;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { name, ok, error, items }, cancellationToken: ct));
    }
}
