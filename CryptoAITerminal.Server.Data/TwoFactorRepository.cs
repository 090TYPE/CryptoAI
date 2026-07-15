using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>Stored 2FA row: envelope-encrypted TOTP secret + enabled flag.</summary>
public sealed record TwoFactorRow(byte[] Ciphertext, byte[] WrappedDek, bool Enabled);

/// <summary>TOTP 2FA per user.</summary>
public sealed class TwoFactorRepository
{
    private readonly Db _db;
    public TwoFactorRepository(Db db) => _db = db;

    /// <summary>Store a freshly-generated (encrypted) secret, not yet enabled.</summary>
    public async Task UpsertSecretAsync(Guid userId, byte[] ciphertext, byte[] wrappedDek, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO user_2fa (user_id, secret_ciphertext, secret_wrapped_dek, enabled)
            VALUES (@userId, @ciphertext, @wrappedDek, false)
            ON CONFLICT (user_id) DO UPDATE
              SET secret_ciphertext=EXCLUDED.secret_ciphertext, secret_wrapped_dek=EXCLUDED.secret_wrapped_dek, enabled=false;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, ciphertext, wrappedDek }, cancellationToken: ct));
    }

    public async Task<TwoFactorRow?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT secret_ciphertext AS Ciphertext, secret_wrapped_dek AS WrappedDek, enabled AS Enabled
                             FROM user_2fa WHERE user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TwoFactorRow>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }

    public async Task SetEnabledAsync(Guid userId, bool enabled, CancellationToken ct = default)
    {
        const string sql = @"UPDATE user_2fa SET enabled = @enabled WHERE user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, enabled }, cancellationToken: ct));
    }

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT enabled FROM user_2fa WHERE user_id = @userId;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(sql, new { userId }, cancellationToken: ct)) ?? false;
    }
}
