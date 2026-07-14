using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoAITerminal.Server.Common;

/// <summary>
/// Production envelope cipher: the secret is still sealed locally with a fresh AES-256 DEK, but
/// the DEK is wrapped/unwrapped through Vault's Transit engine — so the master key (KEK) never
/// leaves Vault, not even into this process. A stolen DB + a compromised app process still can't
/// decrypt without an authenticated call to Vault (which is audited and can be revoked).
///
/// wrapped_dek is stored as Vault's own ciphertext string ("vault:v1:...").
/// </summary>
public sealed class VaultTransitEnvelopeCipher : IEnvelopeCipher
{
    private readonly HttpClient _http;
    private readonly string _keyName;

    public VaultTransitEnvelopeCipher(HttpClient http, string vaultAddr, string vaultToken, string keyName = "cryptoai-kek")
    {
        _http = http;
        _http.BaseAddress = new Uri(vaultAddr.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove("X-Vault-Token");
        _http.DefaultRequestHeaders.Add("X-Vault-Token", vaultToken);
        _keyName = keyName;
    }

    public async Task<(byte[] Ciphertext, byte[] WrappedDek)> EncryptAsync(string plaintext, CancellationToken ct = default)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var ciphertext = AesGcmBlob.Seal(dek, Encoding.UTF8.GetBytes(plaintext));
            var wrapped = await TransitEncryptAsync(Convert.ToBase64String(dek), ct);
            return (ciphertext, Encoding.UTF8.GetBytes(wrapped));
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public async Task<string> DecryptAsync(byte[] ciphertext, byte[] wrappedDek, CancellationToken ct = default)
    {
        var dekB64 = await TransitDecryptAsync(Encoding.UTF8.GetString(wrappedDek), ct);
        var dek = Convert.FromBase64String(dekB64);
        try { return Encoding.UTF8.GetString(AesGcmBlob.Open(dek, ciphertext)); }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    private async Task<string> TransitEncryptAsync(string plaintextB64, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"v1/transit/encrypt/{_keyName}", new { plaintext = plaintextB64 }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data").GetProperty("ciphertext").GetString()!;
    }

    private async Task<string> TransitDecryptAsync(string ciphertext, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"v1/transit/decrypt/{_keyName}", new { ciphertext }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data").GetProperty("plaintext").GetString()!;
    }
}
