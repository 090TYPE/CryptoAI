using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoAITerminal.Server.Common;

public enum LicenseCheck { Valid, BadFormat, BadSignature, Expired }

public sealed record LicensePayload(
    string Name,
    string Edition,
    DateTime? Expires,
    string? Machine,
    DateTime Issued);

public sealed record LicenseCheckResult(LicenseCheck Result, LicensePayload? Payload)
{
    public bool IsValid => Result == LicenseCheck.Valid;
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
