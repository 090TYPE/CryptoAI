using System.Security.Cryptography;
using CryptoAITerminal.Backend.Services;
using CryptoAITerminal.LicenseBot;

namespace CryptoAITerminal.Core.Tests;

public class BackendServicesTests
{
    private static (string PrivatePem, string PublicPem) NewKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    [Fact]
    public void Validator_accepts_a_properly_signed_license()
    {
        var (priv, pub) = NewKeyPair();
        var token = LicenseSigner.CreateToken(
            new LicenseInfo("Alice", "Pro", null, null, DateTime.UtcNow), priv);

        var result = new LicenseTokenValidator(pub).Validate(token);

        Assert.True(result.IsValid);
        Assert.Equal("Alice", result.Payload!.Name);
        Assert.Equal("Pro", result.Payload.Edition);
    }

    [Fact]
    public void Validator_flags_expired_license()
    {
        var (priv, pub) = NewKeyPair();
        var token = LicenseSigner.CreateToken(
            new LicenseInfo("Bob", "Pro", DateTime.UtcNow.AddDays(-1), null, DateTime.UtcNow.AddDays(-30)), priv);

        var result = new LicenseTokenValidator(pub).Validate(token);

        Assert.Equal(LicenseCheck.Expired, result.Result);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validator_rejects_token_signed_by_a_different_key()
    {
        var (priv, _) = NewKeyPair();
        var (_, otherPub) = NewKeyPair();
        var token = LicenseSigner.CreateToken(
            new LicenseInfo("Mallory", "Pro", null, null, DateTime.UtcNow), priv);

        var result = new LicenseTokenValidator(otherPub).Validate(token);

        Assert.Equal(LicenseCheck.BadSignature, result.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only.two.parts.here")]
    public void Validator_rejects_malformed_tokens(string token)
    {
        var (_, pub) = NewKeyPair();
        Assert.False(new LicenseTokenValidator(pub).Validate(token).IsValid);
    }

    [Fact]
    public void RateLimiter_allows_up_to_limit_then_blocks_then_recovers()
    {
        var limiter = new SlidingRateLimiter(limit: 2, window: TimeSpan.FromMinutes(1));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(limiter.TryAcquire("alice", t0));
        Assert.True(limiter.TryAcquire("alice", t0.AddSeconds(1)));
        Assert.False(limiter.TryAcquire("alice", t0.AddSeconds(2)));   // over budget

        Assert.True(limiter.TryAcquire("alice", t0.AddSeconds(61)));   // window slid
    }

    [Fact]
    public void RateLimiter_is_isolated_per_key()
    {
        var limiter = new SlidingRateLimiter(limit: 1, window: TimeSpan.FromMinutes(1));
        var t0 = DateTime.UtcNow;

        Assert.True(limiter.TryAcquire("alice", t0));
        Assert.True(limiter.TryAcquire("bob", t0));    // different license unaffected
        Assert.False(limiter.TryAcquire("alice", t0));
    }

    [Fact]
    public void TtlCache_hits_within_ttl_and_expires_after()
    {
        var cache = new TtlCache(TimeSpan.FromSeconds(30));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        cache.Set("k", "payload", t0);

        Assert.True(cache.TryGet("k", t0.AddSeconds(10), out var hit));
        Assert.Equal("payload", hit);
        Assert.False(cache.TryGet("k", t0.AddSeconds(31), out _));
    }
}
