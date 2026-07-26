using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

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

    // Entropy binds the trial stamp to this app: a blob copied from elsewhere will not unprotect.
    private static readonly byte[] TrialEntropy = Encoding.UTF8.GetBytes("CryptoAITerminal.trial.v1");
    private const string TrialRegistryKey   = @"Software\CryptoAITerminal";
    private const string TrialRegistryValue = "tf";

    private readonly string _publicKeyPem;
    private readonly string _machineId;
    private readonly string _storageDir;
    // The registry copy is the real install's second anchor. A caller that supplies its own
    // storage dir (tests, portable runs) stays fully contained in that directory.
    private readonly bool _mirrorToRegistry;

    public LicenseService(string? publicKeyPem = null, string? machineId = null, string? storageDir = null)
    {
        _publicKeyPem = publicKeyPem ?? DefaultPublicKeyPem;
        _machineId    = machineId ?? GetMachineId();
        _mirrorToRegistry = storageDir is null;
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
        // A stamp we cannot read is a stamp someone edited — the honest first run has none at all.
        if (start is null) return 0;

        var elapsed = (DateTime.UtcNow - start.Value).TotalDays;
        return Math.Clamp(TrialDays - (int)Math.Floor(elapsed), 0, TrialDays);
    }

    /// <summary>
    /// Reads the trial start from both stores and takes the oldest one, so deleting either the
    /// file or the registry value does not hand out another 14 days. Returns null when a stamp
    /// exists but is unreadable or forged.
    /// </summary>
    private DateTime? ReadOrStartTrial()
    {
        var (fileSeen, fileStart) = ReadFileStamp();
        var (regSeen,  regStart)  = ReadRegistryStamp();

        var start = fileStart;
        if (regStart is { } reg && (start is null || reg < start)) start = reg;

        if (start is not null)
        {
            // One of the two anchors was wiped — restore it from the surviving one.
            if (!fileSeen || (!regSeen && _mirrorToRegistry)) WriteTrialStamp(start.Value);
            return start;
        }

        if (fileSeen || regSeen) return null;

        var now = DateTime.UtcNow;
        WriteTrialStamp(now);
        return now;
    }

    private (bool Seen, DateTime? Start) ReadFileStamp()
    {
        try
        {
            if (!File.Exists(TrialPath)) return (false, null);
            var raw = File.ReadAllText(TrialPath).Trim();
            // Unprotect fails on the pre-DPAPI plaintext marker; those installs are still honoured.
            return (true, ParseStamp(Unprotect(raw) ?? raw));
        }
        catch
        {
            return (true, null);
        }
    }

    private (bool Seen, DateTime? Start) ReadRegistryStamp()
    {
        if (!_mirrorToRegistry) return (false, null);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TrialRegistryKey);
            if (key?.GetValue(TrialRegistryValue) is not string stored || stored.Length == 0)
                return (false, null);
            return (true, Unprotect(stored) is { } plain ? ParseStamp(plain) : null);
        }
        catch
        {
            // Registry unavailable — fall back to the file rather than locking the user out.
            return (false, null);
        }
    }

    private void WriteTrialStamp(DateTime utc)
    {
        string protectedStamp;
        try { protectedStamp = Protect(utc); }
        catch (Exception ex)
        {
            CrashLog.Write("WARN", "trial stamp could not be protected: " + ex.Message);
            return;
        }

        try
        {
            Directory.CreateDirectory(_storageDir);
            File.WriteAllText(TrialPath, protectedStamp);
        }
        catch (Exception ex)
        {
            CrashLog.Write("WARN", "trial stamp file write failed: " + ex.Message);
        }

        if (!_mirrorToRegistry) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(TrialRegistryKey);
            key?.SetValue(TrialRegistryValue, protectedStamp, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            CrashLog.Write("WARN", "trial stamp registry write failed: " + ex.Message);
        }
    }

    private static DateTime? ParseStamp(string raw)
    {
        if (!DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return null;

        var utc = dt.ToUniversalTime();
        // A start date in the future is not a clock quirk at this magnitude — it buys extra days.
        return utc > DateTime.UtcNow.AddHours(24) ? null : utc;
    }

    private static string Protect(DateTime utc) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(utc.ToString("o")), TrialEntropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string stored)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored), TrialEntropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null;
        }
    }

    private string? ReadStoredLicense()
    {
        try { return File.Exists(LicensePath) ? File.ReadAllText(LicensePath).Trim() : null; }
        catch { return null; }
    }

    /// <summary>The stored signed license token, for the server <c>X-License</c> header. Null on trial.</summary>
    public string? GetToken() => ReadStoredLicense();

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
