using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>Envelope encryption for custodial secrets: round-trips, and fails closed on a
/// wrong master key or tampered ciphertext.</summary>
public class EnvelopeCipherTests
{
    private static LocalAesEnvelopeCipher NewCipher() =>
        new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task Round_trips_a_secret()
    {
        var cipher = NewCipher();
        const string secret = "sk-binance-APIKEY-9f83h2::secret-part";

        var (ct, wrapped) = await cipher.EncryptAsync(secret);

        Assert.DoesNotContain("binance", System.Text.Encoding.UTF8.GetString(ct));
        Assert.Equal(secret, await cipher.DecryptAsync(ct, wrapped));
    }

    [Fact]
    public async Task Wrong_master_key_cannot_decrypt()
    {
        var a = NewCipher();
        var b = NewCipher(); // different KEK

        var (ct, wrapped) = await a.EncryptAsync("private-key-material");

        await Assert.ThrowsAnyAsync<CryptographicException>(() => b.DecryptAsync(ct, wrapped));
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected()
    {
        var cipher = NewCipher();
        var (ct, wrapped) = await cipher.EncryptAsync("seed phrase words here");

        ct[^1] ^= 0xFF; // flip a bit in the tag

        await Assert.ThrowsAnyAsync<CryptographicException>(() => cipher.DecryptAsync(ct, wrapped));
    }

    [Fact]
    public void Rejects_bad_key_length()
    {
        Assert.Throws<ArgumentException>(() => new LocalAesEnvelopeCipher(new byte[16]));
    }
}
