using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Resolves the app's user row. Identity is the licence SUBJECT computed by
/// <c>LicenseIdentity.Subject</c> from the RSA-verified payload — never the display name, which
/// the buyer edits at will and which two customers can share. The display name is stored
/// alongside for support, and is not part of the key. No email/password login yet.
/// </summary>
public sealed class UsersRepository
{
    private readonly Db _db;
    public UsersRepository(Db db) => _db = db;

    /// <param name="subject">Namespaced licence subject, e.g. <c>sub:&lt;order id&gt;</c>.</param>
    /// <param name="licenseName">Display name from the payload. Stored, never keyed on.</param>
    public async Task<Guid> GetOrCreateByLicenseAsync(string subject, string licenseName, CancellationToken ct = default)
    {
        // The subject is stored in the (unique) email column as "license:<subject>" until real
        // accounts land. Returns the stable user id.
        const string sql = @"
            INSERT INTO users (email, pw_hash, license_name)
            VALUES (@key, '', @licenseName)
            ON CONFLICT (email) DO UPDATE SET license_name = EXCLUDED.license_name
            RETURNING id;";
        var key = "license:" + subject.Trim();
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new { key, licenseName }, cancellationToken: ct));
    }
}
