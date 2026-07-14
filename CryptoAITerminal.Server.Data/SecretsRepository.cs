using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Metadata about a stored secret — never includes the ciphertext or plaintext.</summary>
public sealed record SecretInfo(Guid Id, string Kind, string? Label, string ExchangeOrChain, string? Permissions, DateTime CreatedUtc);

/// <summary>The encrypted material — returned ONLY to the executor node for signing.</summary>
public sealed record SecretCipher(Guid Id, byte[] Ciphertext, byte[] WrappedDek);

/// <summary>
/// Stores envelope-encrypted custodial secrets. The public API writes ciphertext (encrypted
/// before it reaches here) and reads metadata only; decryption material is exposed solely via
/// <see cref="GetCipherAsync"/>, which the isolated executor uses.
/// </summary>
public sealed class SecretsRepository
{
    private readonly Db _db;
    public SecretsRepository(Db db) => _db = db;

    public async Task<Guid> StoreAsync(Guid userId, string kind, string? label, string exchangeOrChain,
        byte[] ciphertext, byte[] wrappedDek, string? permissions, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO secrets (user_id, kind, label, exchange_or_chain, ciphertext, wrapped_dek, permissions)
            VALUES (@userId, @kind, @label, @exchangeOrChain, @ciphertext, @wrappedDek, @permissions)
            RETURNING id;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql,
            new { userId, kind, label, exchangeOrChain, ciphertext, wrappedDek, permissions }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SecretInfo>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT id AS Id, kind AS Kind, label AS Label, exchange_or_chain AS ExchangeOrChain,
                                    permissions AS Permissions, created_utc AS CreatedUtc
                             FROM secrets WHERE user_id = @userId ORDER BY created_utc DESC;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<SecretInfo>(new CommandDefinition(sql, new { userId }, cancellationToken: ct))).ToList();
    }

    public async Task<int> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM secrets WHERE id = @id AND user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
    }

    /// <summary>Executor-only: fetch the encrypted material for signing. Not exposed by the public API.</summary>
    public async Task<SecretCipher?> GetCipherAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = @"SELECT id AS Id, ciphertext AS Ciphertext, wrapped_dek AS WrappedDek
                             FROM secrets WHERE id = @id;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SecretCipher>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    /// <summary>Executor-only: newest matching secret for a user (e.g. wallet_pk for a chain).</summary>
    public async Task<SecretCipher?> FindForUserAsync(Guid userId, string kind, string exchangeOrChain, CancellationToken ct = default)
    {
        const string sql = @"SELECT id AS Id, ciphertext AS Ciphertext, wrapped_dek AS WrappedDek
                             FROM secrets
                             WHERE user_id = @userId AND kind = @kind AND exchange_or_chain = @exchangeOrChain
                             ORDER BY created_utc DESC LIMIT 1;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SecretCipher>(
            new CommandDefinition(sql, new { userId, kind, exchangeOrChain }, cancellationToken: ct));
    }
}

/// <summary>Append-only audit trail for custodial actions.</summary>
public sealed class AuditRepository
{
    private readonly Db _db;
    public AuditRepository(Db db) => _db = db;

    public async Task WriteAsync(Guid? userId, string actor, string action, string? detailJson, string? ip, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO audit_log (user_id, actor, action, detail, ip)
                             VALUES (@userId, @actor, @action, @detailJson::jsonb, @ip::inet);";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, actor, action, detailJson, ip }, cancellationToken: ct));
    }
}
