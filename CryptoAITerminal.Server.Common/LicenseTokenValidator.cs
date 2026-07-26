using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoAITerminal.Server.Common;

public enum LicenseCheck { Valid, BadFormat, BadSignature, Expired }

/// <summary>
/// The signed licence body. <paramref name="Sub"/> is the immutable subject — the order id or a
/// salted hash of the buyer's telegram id — and is what identifies the customer. It is optional
/// only because tokens issued before the field existed are still in circulation; see
/// <see cref="LicenseIdentity"/> for the fallback.
/// </summary>
public sealed record LicensePayload(
    string Name,
    string Edition,
    DateTime? Expires,
    string? Machine,
    DateTime Issued,
    string? Sub = null);

public sealed record LicenseCheckResult(LicenseCheck Result, LicensePayload? Payload)
{
    public bool IsValid => Result == LicenseCheck.Valid;
}

/// <summary>
/// Turns a verified payload into the one string the server may use as a customer identity —
/// for the user row, the rate-limit partition and the AI budget.
///
/// It must NOT be the display Name: that comes from the buyer's Telegram profile, is not unique,
/// and is editable in seconds, so keying on it puts two customers with the same name on one user
/// row and lets a rename walk into another customer's secrets. It must also not be the raw
/// X-License header: one valid token has many spellings (padding, base64url vs base64,
/// surrounding whitespace) and each spelling would be a separate quota bucket.
///
/// Tokens issued before <c>sub</c> existed still fall back to the trimmed Name, and deliberately
/// to the BARE name — that is exactly the key those customers' user rows already carry, so the
/// fallback keeps them attached to their favourites and secrets instead of silently minting a new
/// account. A subject-bearing token gets a new, prefixed key; those rows are migrated by hand.
/// </summary>
public static class LicenseIdentity
{
    public static string Subject(LicensePayload payload)
    {
        var sub = payload.Sub;
        if (!string.IsNullOrWhiteSpace(sub)) return "sub:" + sub.Trim();

        var name = payload.Name;
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
    }
}

/// <summary>
/// Server-side verification of the same RSA-signed license tokens the desktop app and the
/// LicenseBot use: <c>base64url(payloadJson) "." base64(RSA-SHA256 signature)</c>. The server
/// holds only the public key, so it can gate API calls to paying customers without ever
/// touching the seller's private key. Pure and unit-tested. Shared by Backend and Server.Api.
/// </summary>
public sealed class LicenseTokenValidator
{
    /// <summary>The app's embedded public key. Override in production via LICENSE_PUBLIC_KEY_PEM.</summary>
    public const string DefaultPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAz/ryuGbg6a1omTCVOXF1\n" +
        "G06RsPcG8KY3/iMpmWTAgtyM5ZoZwvBts+rON29kUgtZUsVpjkv6bZESYMXi1/hh\n" +
        "c28Cv6bgvTnRSzRlgnO7c6vLHz3+eRVRgjV/lpWz0bTwtgeXzwujpBdGUPv2IbrP\n" +
        "LixYEOBz1bCpJjK1dgw1XY+cnN9Hnea9gzj9xHXJfSDhp5BSBl31bjC4JDmhkrJz\n" +
        "atUynu+4jo3OGLXT4nrM+hddDAXAjm3GZSSYMVHsPPJ1R2z+bZVjDeDZBZxhjxZ1\n" +
        "4D8+xKr+3ok5ZM8pyD0fcGd6hG5cRlYmepGvN7fs8HmCXPvtEybpKYTD4U33oSFz\n" +
        "LQIDAQAB\n" +
        "-----END PUBLIC KEY-----";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _publicKeyPem;

    public LicenseTokenValidator(string publicKeyPem) => _publicKeyPem = publicKeyPem;

    public LicenseCheckResult Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return new(LicenseCheck.BadFormat, null);

        var parts = token.Trim().Split('.');
        if (parts.Length != 2) return new(LicenseCheck.BadFormat, null);

        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            signature = Convert.FromBase64String(parts[1]);
        }
        catch
        {
            return new(LicenseCheck.BadFormat, null);
        }

        bool sigOk;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_publicKeyPem);
            sigOk = rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return new(LicenseCheck.BadSignature, null);
        }
        if (!sigOk) return new(LicenseCheck.BadSignature, null);

        LicensePayload? info;
        try
        {
            info = JsonSerializer.Deserialize<LicensePayload>(Encoding.UTF8.GetString(payloadBytes), JsonOpts);
        }
        catch
        {
            return new(LicenseCheck.BadFormat, null);
        }
        if (info is null) return new(LicenseCheck.BadFormat, null);

        if (info.Expires is { } exp && exp < DateTime.UtcNow)
            return new(LicenseCheck.Expired, info);

        return new(LicenseCheck.Valid, info);
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
