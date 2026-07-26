using CryptoAITerminal.Server.Common;
using Dapper;

namespace CryptoAITerminal.Server.Data;

public sealed record ProviderKey(string Provider, string ApiKey, bool Enabled, string? Note, DateTime UpdatedUtc);

/// <summary>
/// Runtime-editable API keys for the data providers. Collectors call <see cref="GetAsync"/>
/// on every run, so changing a row (via <see cref="SetAsync"/> or the admin API) takes effect
/// immediately — no restart. Returns null when the provider is disabled or has no key.
///
/// The keys are the server's own paid credentials (Anthropic, Covalent, Birdeye…), so they are
/// protected at rest by <see cref="ColumnCipher"/>. Every process that reads them needs the same
/// <see cref="ColumnCipher.KeyEnvVar"/>; without it a protected row reads as "no key" and the
/// collector no-ops rather than calling upstream with a ciphertext.
/// </summary>
public sealed class ProviderKeyStore
{
    private readonly Db _db;
    private readonly ColumnCipher _columns;

    public ProviderKeyStore(Db db, ColumnCipher? columns = null)
    {
        _db = db;
        _columns = columns ?? ColumnCipher.FromEnvironment();
    }

    /// <summary>The usable key for a provider, or null if missing/disabled/empty.</summary>
    public async Task<string?> GetAsync(string provider, CancellationToken ct = default)
    {
        const string sql = @"SELECT api_key FROM provider_keys
                             WHERE provider = @provider AND enabled AND api_key <> '';";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var stored = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(sql, new { provider }, cancellationToken: ct));
        return await _columns.UnprotectAsync(stored, ct);
    }

    /// <summary>Create or update a provider's key. Sets enabled and bumps updated_utc.</summary>
    public async Task SetAsync(string provider, string apiKey, bool enabled = true, string? note = null, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO provider_keys (provider, api_key, enabled, note, updated_utc)
            VALUES (@provider, @apiKey, @enabled, @note, now())
            ON CONFLICT (provider) DO UPDATE
              SET api_key = EXCLUDED.api_key,
                  enabled = EXCLUDED.enabled,
                  note    = COALESCE(EXCLUDED.note, provider_keys.note),
                  updated_utc = now();";
        var stored = await _columns.ProtectAsync(apiKey, ct);
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { provider, apiKey = stored, enabled, note }, cancellationToken: ct));
    }

    /// <summary>All providers (for the admin screen). api_key is returned masked-length only by the API layer.</summary>
    public async Task<IReadOnlyList<ProviderKey>> ListAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT provider AS Provider, api_key AS ApiKey, enabled AS Enabled,
                                    note AS Note, updated_utc AS UpdatedUtc
                             FROM provider_keys ORDER BY provider;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ProviderKey>(new CommandDefinition(sql, cancellationToken: ct));

        // Undecryptable rows come back empty, which the admin screen already renders as "no key" —
        // better than showing four characters of base64 as if they were the tail of the secret.
        var result = new List<ProviderKey>();
        foreach (var row in rows)
        {
            var key = await _columns.UnprotectAsync(row.ApiKey, ct) ?? string.Empty;
            result.Add(row with { ApiKey = key });
        }
        return result;
    }
}
