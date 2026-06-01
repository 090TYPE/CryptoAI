using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.Services;

public enum LicenseState { Trial, Licensed, Expired }

public enum LicenseValidation { Valid, BadFormat, BadSignature, Expired, WrongMachine }

public sealed record LicenseInfo(
    string Name,
    string Edition,
    DateTime? Expires,
    string? Machine,
    DateTime Issued);

public sealed record LicenseValidationResult(LicenseValidation Result, LicenseInfo? Info)
{
    public bool IsValid => Result == LicenseValidation.Valid;
}

public sealed record LicenseSnapshot(
    LicenseState State,
    string? LicensedTo,
    string? Edition,
    DateTime? Expires,
    int TrialDaysRemaining)
{
    public bool IsLicensed => State == LicenseState.Licensed;
    public bool IsExpired  => State == LicenseState.Expired;
}

/// <summary>
/// Offline license validation. The app embeds an RSA public key; licenses are
/// signed with the matching private key (held by the seller) so validation needs
/// no server. Falls back to a time-limited trial when no license is activated.
///
/// Token format: <c>base64url(payloadJson) "." base64(signature)</c>, signature
/// is RSA-SHA256 over the raw payload JSON bytes.
/// </summary>
public sealed class LicenseService
{
    public const int TrialDays = 14;

    // Embedded public key (PEM). The matching private key is NOT in the app.
    private const string DefaultPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAz/ryuGbg6a1omTCVOXF1\n" +
        "G06RsPcG8KY3/iMpmWTAgtyM5ZoZwvBts+rON29kUgtZUsVpjkv6bZESYMXi1/hh\n" +
        "c28Cv6bgvTnRSzRlgnO7c6vLHz3+eRVRgjV/lpWz0bTwtgeXzwujpBdGUPv2IbrP\n" +
        "LixYEOBz1bCpJjK1dgw1XY+cnN9Hnea9gzj9xHXJfSDhp5BSBl31bjC4JDmhkrJz\n" +
        "atUynu+4jo3OGLXT4nrM+hddDAXAjm3GZSSYMVHsPPJ1R2z+bZVjDeDZBZxhjxZ1\n" +
        "4D8+xKr+3ok5ZM8pyD0fcGd6hG5cRlYmepGvN7fs8HmCXPvtEybpKYTD4U33oSFz\n" +
        "LQIDAQAB\n" +
        "-----END PUBLIC KEY-----";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _publicKeyPem;
    private readonly string _machineId;
    private readonly string _storageDir;

    public LicenseService(string? publicKeyPem = null, string? machineId = null, string? storageDir = null)
    {
        _publicKeyPem = publicKeyPem ?? DefaultPublicKeyPem;
        _machineId    = machineId ?? GetMachineId();
        _storageDir   = storageDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CryptoAITerminal");
    }

    private string LicensePath => Path.Combine(_storageDir, "license.key");
    private string TrialPath   => Path.Combine(_storageDir, ".trial");

    // ── Validation ───────────────────────────────────────────────────────────

    public LicenseValidationResult Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return new(LicenseValidation.BadFormat, null);

        var parts = token.Trim().Split('.');
        if (parts.Length != 2) return new(LicenseValidation.BadFormat, null);

        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            signature    = Convert.FromBase64String(parts[1]);
        }
        catch
        {
            return new(LicenseValidation.BadFormat, null);
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
            return new(LicenseValidation.BadSignature, null);
        }
        if (!sigOk) return new(LicenseValidation.BadSignature, null);

        LicenseInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<LicenseInfo>(Encoding.UTF8.GetString(payloadBytes), JsonOpts);
        }
        catch
        {
            return new(LicenseValidation.BadFormat, null);
        }
        if (info is null) return new(LicenseValidation.BadFormat, null);

        if (!string.IsNullOrWhiteSpace(info.Machine) &&
            !string.Equals(info.Machine, _machineId, StringComparison.OrdinalIgnoreCase))
            return new(LicenseValidation.WrongMachine, info);

        if (info.Expires is { } exp && exp < DateTime.UtcNow)
            return new(LicenseValidation.Expired, info);

        return new(LicenseValidation.Valid, info);
    }

    /// <summary>Validate and, if valid, persist the token for future launches.</summary>
    public bool TryActivate(string token, out string message)
    {
        var result = Validate(token);
        switch (result.Result)
        {
            case LicenseValidation.Valid:
                try
                {
                    Directory.CreateDirectory(_storageDir);
                    File.WriteAllText(LicensePath, token.Trim());
                    message = $"Activated — licensed to {result.Info!.Name}.";
                    return true;
                }
                catch (Exception ex)
                {
                    message = $"Could not save license: {ex.Message}";
                    return false;
                }
            case LicenseValidation.Expired:     message = "This license has expired."; return false;
            case LicenseValidation.WrongMachine:message = "This license is bound to another machine."; return false;
            case LicenseValidation.BadSignature:message = "Invalid license signature."; return false;
            default:                            message = "Invalid license key format."; return false;
        }
    }

    // ── State ────────────────────────────────────────────────────────────────

    public LicenseSnapshot GetSnapshot()
    {
        var stored = ReadStoredLicense();
        if (stored is not null)
        {
            var v = Validate(stored);
            if (v.IsValid)
                return new LicenseSnapshot(LicenseState.Licensed, v.Info!.Name, v.Info.Edition, v.Info.Expires, 0);
            // A previously-valid license that expired → Expired (not back to trial).
            if (v.Result == LicenseValidation.Expired)
                return new LicenseSnapshot(LicenseState.Expired, v.Info?.Name, v.Info?.Edition, v.Info?.Expires, 0);
        }

        var remaining = TrialDaysRemaining();
        return remaining > 0
            ? new LicenseSnapshot(LicenseState.Trial, null, null, null, remaining)
            : new LicenseSnapshot(LicenseState.Expired, null, null, null, 0);
    }

    /// <summary>Days left in the trial; starts the trial clock on first call.</summary>
    public int TrialDaysRemaining()
    {
        var start = ReadOrStartTrial();
        var elapsed = (DateTime.UtcNow - start).TotalDays;
        var left = TrialDays - (int)Math.Floor(elapsed);
        return Math.Max(0, left);
    }

    private DateTime ReadOrStartTrial()
    {
        try
        {
            if (File.Exists(TrialPath))
            {
                var raw = File.ReadAllText(TrialPath).Trim();
                if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    return dt.ToUniversalTime();
            }
            Directory.CreateDirectory(_storageDir);
            var now = DateTime.UtcNow;
            File.WriteAllText(TrialPath, now.ToString("o"));
            return now;
        }
        catch
        {
            // If we can't track the trial, treat as just-started rather than locking out.
            return DateTime.UtcNow;
        }
    }

    private string? ReadStoredLicense()
    {
        try { return File.Exists(LicensePath) ? File.ReadAllText(LicensePath).Trim() : null; }
        catch { return null; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Stable, non-PII machine fingerprint for optional license binding.</summary>
    public static string GetMachineId()
    {
        var raw = Environment.MachineName + "|" + Environment.UserName + "|" + Environment.OSVersion.Platform;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// Seller-side: sign a license payload with the private key (PEM). Not used by
    /// the running app, but kept here so the token format stays in one place.
    /// </summary>
    public static string CreateToken(LicenseInfo info, string privateKeyPem)
    {
        var json = JsonSerializer.Serialize(info, JsonOpts);
        var payloadBytes = Encoding.UTF8.GetBytes(json);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var sig = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Base64UrlEncode(payloadBytes) + "." + Convert.ToBase64String(sig);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
