using System;
using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>RFC 6238 TOTP used for 2FA: known vector, round-trips, tolerates skew, rejects wrong.</summary>
public class TotpTests
{
    // RFC 6238 test key "12345678901234567890" in Base32; at T=59s (SHA1) the 8-digit code is
    // 94287082 → the 6-digit truncation is 287082.
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Fact]
    public void Matches_rfc6238_vector()
        => Assert.Equal("287082", Totp.Compute(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(59)));

    [Fact]
    public void Verifies_current_code()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        Assert.True(Totp.Verify(secret, Totp.Compute(secret, now), at: now));
    }

    [Fact]
    public void Accepts_previous_step_within_window()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var prev = Totp.Compute(secret, now.AddSeconds(-30));
        Assert.True(Totp.Verify(secret, prev, window: 1, at: now));
    }

    [Fact]
    public void Rejects_wrong_code()
    {
        var secret = Totp.GenerateSecret();
        Assert.False(Totp.Verify(secret, "12345", at: DateTimeOffset.UtcNow));  // wrong length → never matches
        Assert.False(Totp.Verify(secret, "", at: DateTimeOffset.UtcNow));
    }
}
