using System.Security.Cryptography;

namespace CryptoAITerminal.Server.Common;

/// <summary>
/// RFC 6238 TOTP (6-digit, 30s step, HMAC-SHA1) for 2FA. Secret is Base32 (Google Authenticator
/// compatible). No external dependency — BCL only.
/// </summary>
public static class Totp
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>New random Base32 secret (default 20 bytes / 160 bits).</summary>
    public static string GenerateSecret(int bytes = 20) => Base32Encode(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>otpauth:// URI for the authenticator-app QR / manual entry.</summary>
    public static string ProvisioningUri(string secret, string account, string issuer = "CryptoAI")
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
           $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    /// <summary>The current code for a secret (mainly for tests).</summary>
    public static string Compute(string secret, DateTimeOffset? at = null)
        => Compute(Base32Decode(secret), Counter(at ?? DateTimeOffset.UtcNow));

    /// <summary>Verify a user code, allowing ±<paramref name="window"/> steps of clock skew.</summary>
    public static bool Verify(string secret, string code, int window = 1, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim();
        byte[] key;
        try { key = Base32Decode(secret); } catch { return false; }

        var counter = Counter(at ?? DateTimeOffset.UtcNow);
        for (var w = -window; w <= window; w++)
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Compute(key, counter + w)),
                    System.Text.Encoding.ASCII.GetBytes(code)))
                return true;
        return false;
    }

    private static long Counter(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    private static string Compute(byte[] key, long counter)
    {
        var msg = new byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xff); counter >>= 8; }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        var offset = hash[^1] & 0x0f;
        var bin = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    // ── Base32 (RFC 4648, no padding) ─────────────────────────────────────────
    private static string Base32Encode(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string s)
    {
        s = s.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("bad base32");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xff)); bits -= 8; }
        }
        return bytes.ToArray();
    }
}
