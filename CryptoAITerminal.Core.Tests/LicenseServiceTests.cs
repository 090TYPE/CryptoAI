using System;
using System.IO;
using System.Security.Cryptography;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Covers offline license validation, trial tracking and activation persistence
/// using an ephemeral RSA keypair and a temp storage dir (no real files touched).
/// </summary>
public class LicenseServiceTests : IDisposable
{
    private const string Machine = "TESTMACHINE01";
    private readonly string _dir;
    private readonly string _publicPem;
    private readonly string _privatePem;

    public LicenseServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "caitest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        using var rsa = RSA.Create(2048);
        _publicPem  = rsa.ExportSubjectPublicKeyInfoPem();
        _privatePem = rsa.ExportPkcs8PrivateKeyPem();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private LicenseService NewService() => new(_publicPem, Machine, _dir);

    private string Sign(LicenseInfo info) => LicenseService.CreateToken(info, _privatePem);

    private static LicenseInfo Info(DateTime? expires = null, string? machine = null) =>
        new("Acme Trading", "Pro", expires, machine, DateTime.UtcNow);

    [Fact]
    public void ValidToken_Validates()
    {
        var token = Sign(Info());
        var result = NewService().Validate(token);
        Assert.Equal(LicenseValidation.Valid, result.Result);
        Assert.Equal("Acme Trading", result.Info!.Name);
    }

    [Fact]
    public void ExpiredToken_IsExpired()
    {
        var token = Sign(Info(expires: DateTime.UtcNow.AddDays(-1)));
        Assert.Equal(LicenseValidation.Expired, NewService().Validate(token).Result);
    }

    [Fact]
    public void FutureExpiry_IsValid()
    {
        var token = Sign(Info(expires: DateTime.UtcNow.AddDays(365)));
        Assert.Equal(LicenseValidation.Valid, NewService().Validate(token).Result);
    }

    [Fact]
    public void WrongMachineBinding_IsRejected()
    {
        var token = Sign(Info(machine: "SOMEOTHERMACHINE"));
        Assert.Equal(LicenseValidation.WrongMachine, NewService().Validate(token).Result);
    }

    [Fact]
    public void MatchingMachineBinding_IsValid()
    {
        var token = Sign(Info(machine: Machine));
        Assert.Equal(LicenseValidation.Valid, NewService().Validate(token).Result);
    }

    [Fact]
    public void TamperedPayload_FailsSignature()
    {
        var token = Sign(Info());
        var parts = token.Split('.');
        // Flip a character in the payload segment.
        var tampered = parts[0][..^2] + (parts[0][^1] == 'A' ? "BB" : "AA") + "." + parts[1];
        var result = NewService().Validate(tampered);
        Assert.True(result.Result is LicenseValidation.BadSignature or LicenseValidation.BadFormat);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only.one.too.many")]
    public void Malformed_IsBadFormat(string token)
    {
        Assert.Equal(LicenseValidation.BadFormat, NewService().Validate(token).Result);
    }

    [Fact]
    public void WrongPublicKey_FailsSignature()
    {
        var token = Sign(Info());
        using var other = RSA.Create(2048);
        var svc = new LicenseService(other.ExportSubjectPublicKeyInfoPem(), Machine, _dir);
        Assert.Equal(LicenseValidation.BadSignature, svc.Validate(token).Result);
    }

    [Fact]
    public void FreshInstall_IsTrialWithFullDays()
    {
        var snap = NewService().GetSnapshot();
        Assert.Equal(LicenseState.Trial, snap.State);
        Assert.InRange(snap.TrialDaysRemaining, LicenseService.TrialDays - 1, LicenseService.TrialDays);
    }

    [Fact]
    public void Activate_PersistsAndSnapshotIsLicensed()
    {
        var svc = NewService();
        var ok = svc.TryActivate(Sign(Info()), out _);
        Assert.True(ok);

        // A fresh service over the same dir should see the stored license.
        var snap = NewService().GetSnapshot();
        Assert.Equal(LicenseState.Licensed, snap.State);
        Assert.Equal("Acme Trading", snap.LicensedTo);
    }

    [Fact]
    public void Activate_RejectsExpiredToken()
    {
        var ok = NewService().TryActivate(Sign(Info(expires: DateTime.UtcNow.AddDays(-5))), out var msg);
        Assert.False(ok);
        Assert.Contains("expired", msg, StringComparison.OrdinalIgnoreCase);
    }

    // Guards that the embedded public key matches the seller's signing key.
    // Token signed offline for "Demo Customer", Pro, no expiry, no machine binding.
    private const string EmbeddedKeySampleToken =
        "eyJuYW1lIjoiRGVtbyBDdXN0b21lciIsImVkaXRpb24iOiJQcm8iLCJleHBpcmVzIjpudWxsLCJtYWNoaW5lIjpudWxsLCJpc3N1ZWQiOiIyMDI2LTA2LTAxVDAwOjAwOjAwWiJ9.EKTBHBhD1ZUPbz9ZaGmyoBljmkJWB1cmH4J5IuRRurrCyj/Yf9Us68ILjO2ritV6OvqsfTkNRX46H0D3dnxzsoCXoyDkx8jeoT2xqiVF7D1NygVnR8/u8pWw2ndUa3oPmrWMIzE3LuGlBDUD5DZpyJjuIQXZwbMrK3QB2ke/wLBSqKq1LXqNXnVgf5AwRXHt/sh153CRN2Unhafe0fPRqEM/JX0K2bAWm77UtPQxK864QldUoY6+AUIAgyD0kkmuJvsWXbTw0ijZPIgeFBcv7FGN7sOMHX+Fu8F43kpjasXehK1mBsdwZ5IP5o6xtinNoRnw80MZ0EK2ZnvkU0Af2g==";

    [Fact]
    public void EmbeddedPublicKey_ValidatesSellerSignedToken()
    {
        // Use the default (embedded) public key; only override machine/dir.
        var svc = new LicenseService(publicKeyPem: null, machineId: Machine, storageDir: _dir);
        var result = svc.Validate(EmbeddedKeySampleToken);
        Assert.Equal(LicenseValidation.Valid, result.Result);
        Assert.Equal("Demo Customer", result.Info!.Name);
    }

    [Fact]
    public void ExpiredTrial_SnapshotIsExpired()
    {
        // Seed the trial marker 30 days in the past.
        File.WriteAllText(Path.Combine(_dir, ".trial"), DateTime.UtcNow.AddDays(-30).ToString("o"));
        var snap = NewService().GetSnapshot();
        Assert.Equal(LicenseState.Expired, snap.State);
    }
}
