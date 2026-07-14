using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Resolves the app's user row. Identity is the verified license Name (the API middleware
/// checks the RSA signature before calling this); the id is stable per license so favorites
/// attach to the right owner across token renewals. No email/password login yet.
/// </summary>
public sealed class UsersRepository
{
    private readonly Db _db;
    public UsersRepository(Db db) => _db = db;

    public async Task<Guid> GetOrCreateByLicenseAsync(string license, CancellationToken ct = default)
    {
        // License string is stored in the (unique) email column as "license:<name>" until real
        // accounts land. Returns the stable user id.
        const string sql = @"
            INSERT INTO users (email, pw_hash, license_name)
            VALUES (@key, '', @license)
            ON CONFLICT (email) DO UPDATE SET license_name = EXCLUDED.license_name
            RETURNING id;";
        var key = "license:" + license.Trim();
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new { key, license }, cancellationToken: ct));
    }
}
