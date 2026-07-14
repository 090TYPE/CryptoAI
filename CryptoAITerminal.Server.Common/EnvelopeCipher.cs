using System.Security.Cryptography;
using System.Text;

namespace CryptoAITerminal.Server.Common;

/// <summary>
/// Envelope encryption for custodial secrets. Each secret gets a fresh random 256-bit data
/// key (DEK); the secret is sealed with the DEK, and the DEK is itself wrapped by a master
/// key (KEK). Storing only the ciphertext + wrapped DEK means a stolen DB/backup is useless
/// without the KEK.
///
/// Two implementations share the same DB blobs: <see cref="LocalAesEnvelopeCipher"/> keeps the
/// KEK in process (dev/tests), <see cref="VaultTransitEnvelopeCipher"/> wraps the DEK through
/// Vault Transit so the KEK never leaves Vault (production, executor node). Async because Vault
/// is a network call.
/// </summary>
public interface IEnvelopeCipher
{
    /// <summary>Seal plaintext. Returns the ciphertext blob and the wrapped data key.</summary>
    Task<(byte[] Ciphertext, byte[] WrappedDek)> EncryptAsync(string plaintext, CancellationToken ct = default);

    /// <summary>Recover plaintext from the ciphertext blob and wrapped data key.</summary>
    Task<string> DecryptAsync(byte[] ciphertext, byte[] wrappedDek, CancellationToken ct = default);
}

/// <summary>AES-256-GCM sealed blob: <c>nonce(12) || ciphertext || tag(16)</c>. Authenticated.</summary>
internal static class AesGcmBlob
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Seal(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ct = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, ct, tag);

        var blob = new byte[NonceSize + ct.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(ct, 0, blob, NonceSize, ct.Length);
        Buffer.BlockCopy(tag, 0, blob, NonceSize + ct.Length, TagSize);
        return blob;
    }

    public static byte[] Open(byte[] key, byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext blob too short.");

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(blob.Length - TagSize, TagSize);
        var ct = blob.AsSpan(NonceSize, blob.Length - NonceSize - TagSize);

        var pt = new byte[ct.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, ct, tag, pt); // throws on tamper / wrong key
        return pt;
    }
}

/// <summary>AES-256-GCM envelope cipher with the KEK in process memory (dev/tests).</summary>
public sealed class LocalAesEnvelopeCipher : IEnvelopeCipher
{
    private readonly byte[] _kek;

    public LocalAesEnvelopeCipher(byte[] kek)
    {
        if (kek is not { Length: 32 })
            throw new ArgumentException("KEK must be 32 bytes (AES-256).", nameof(kek));
        _kek = kek;
    }

    public static LocalAesEnvelopeCipher FromBase64(string base64Kek) =>
        new(Convert.FromBase64String(base64Kek));

    public Task<(byte[] Ciphertext, byte[] WrappedDek)> EncryptAsync(string plaintext, CancellationToken ct = default)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var ciphertext = AesGcmBlob.Seal(dek, Encoding.UTF8.GetBytes(plaintext));
            var wrappedDek = AesGcmBlob.Seal(_kek, dek);
            return Task.FromResult((ciphertext, wrappedDek));
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public Task<string> DecryptAsync(byte[] ciphertext, byte[] wrappedDek, CancellationToken ct = default)
    {
        var dek = AesGcmBlob.Open(_kek, wrappedDek);
        try { return Task.FromResult(Encoding.UTF8.GetString(AesGcmBlob.Open(dek, ciphertext))); }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }
}
