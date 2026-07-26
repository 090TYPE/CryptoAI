using System.Security.Cryptography;

namespace CryptoAITerminal.Server.Common;

/// <summary>
/// At-rest protection for a single text column that the server itself must be able to read back
/// again — a customer's Telegram bot token, a provider API key. Custodial secrets have their own
/// columns and their own master key (see <see cref="IEnvelopeCipher"/>); this exists because those
/// two columns were plain text in a database whose dumps also travel to backups and replicas.
///
/// The protected value stays in the SAME column, marked <c>enc:v1:&lt;ciphertext&gt;.&lt;wrapped
/// dek&gt;</c> (both base64). A value without the marker is legacy plain text and is returned
/// unchanged, so rows migrate as they are rewritten and no schema change is needed to roll
/// forward — or back.
///
/// Disabled (no key configured) is a valid state and is exactly the behaviour that existed before:
/// values are stored and read verbatim.
/// </summary>
public sealed class ColumnCipher
{
    /// <summary>Env var holding the base64 32-byte key. Deliberately NOT the custodial KEK: an
    /// edge node has to read these columns and must never be able to decrypt a customer's
    /// exchange key.</summary>
    public const string KeyEnvVar = "CRYPTOAI_DATA_KEY_B64";

    private const string Marker = "enc:v1:";

    private readonly IEnvelopeCipher? _cipher;

    public ColumnCipher(IEnvelopeCipher? cipher = null) => _cipher = cipher;

    /// <summary>Build from a base64 32-byte key; a blank key yields a disabled cipher. A key that
    /// is present but unusable fails loudly at startup rather than silently storing plain text.</summary>
    public static ColumnCipher FromBase64Key(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key)) return new ColumnCipher();
        try
        {
            return new ColumnCipher(LocalAesEnvelopeCipher.FromBase64(base64Key));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"{KeyEnvVar} must be a base64-encoded 32-byte key (openssl rand -base64 32).", ex);
        }
    }

    /// <summary>For hosts that resolve a repository straight from the container without
    /// registering a cipher of their own (the executor, the admin CLI).</summary>
    public static ColumnCipher FromEnvironment() =>
        FromBase64Key(Environment.GetEnvironmentVariable(KeyEnvVar));

    public bool Enabled => _cipher is not null;

    public async Task<string?> ProtectAsync(string? plaintext, CancellationToken ct = default)
    {
        var cipher = _cipher;
        if (cipher is null || string.IsNullOrEmpty(plaintext)) return plaintext;
        var (ciphertext, wrappedDek) = await cipher.EncryptAsync(plaintext, ct);
        return Marker + Convert.ToBase64String(ciphertext) + "." + Convert.ToBase64String(wrappedDek);
    }

    /// <summary>
    /// Recover a stored value. A protected value this process holds no usable key for comes back
    /// as null, so a caller that already treats "no key" as "feature off" degrades instead of
    /// handing ciphertext to Telegram or a data provider as if it were the secret.
    /// </summary>
    public async Task<string?> UnprotectAsync(string? stored, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Marker, StringComparison.Ordinal)) return stored;

        var cipher = _cipher;
        if (cipher is null) return null;

        var body = stored[Marker.Length..];
        var dot = body.IndexOf('.');
        if (dot <= 0) return null;

        try
        {
            var ciphertext = Convert.FromBase64String(body[..dot]);
            var wrappedDek = Convert.FromBase64String(body[(dot + 1)..]);
            return await cipher.DecryptAsync(ciphertext, wrappedDek, ct);
        }
        catch (FormatException) { return null; }
        catch (CryptographicException) { return null; }
    }
}
