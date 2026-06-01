using System;
using System.IO;
using System.Security.Cryptography;
using BotSigner = CryptoAITerminal.LicenseBot.LicenseSigner;
using BotInfo = CryptoAITerminal.LicenseBot.LicenseInfo;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Guards that license keys minted by the Telegram bot (LicenseBot.LicenseSigner)
/// validate against the terminal's verifier (TerminalUI.LicenseService) — i.e.
/// the two token implementations stay byte-compatible.
/// </summary>
public class LicenseBotCompatibilityTests : IDisposable
{
    private const string Machine = "TESTMACHINE01";
    private readonly string _dir;
    private readonly string _publicPem;
    private readonly string _privatePem;

    public LicenseBotCompatibilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "caibot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        using var rsa = RSA.Create(2048);
        _publicPem  = rsa.ExportSubjectPublicKeyInfoPem();
        _privatePem = rsa.ExportPkcs8PrivateKeyPem();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void BotIssuedKey_ValidatesInTerminal()
    {
        var token = BotSigner.CreateToken(
            new BotInfo("Acme Trading", "Pro", DateTime.UtcNow.AddDays(365), null, DateTime.UtcNow),
            _privatePem);

        var svc = new LicenseService(_publicPem, Machine, _dir);
        var result = svc.Validate(token);

        Assert.Equal(LicenseValidation.Valid, result.Result);
        Assert.Equal("Acme Trading", result.Info!.Name);
        Assert.Equal("Pro", result.Info.Edition);
    }

    [Fact]
    public void BotIssuedMachineBoundKey_ValidatesOnThatMachine()
    {
        var token = BotSigner.CreateToken(
            new BotInfo("Bound Co", "Pro", null, Machine, DateTime.UtcNow), _privatePem);

        var svc = new LicenseService(_publicPem, Machine, _dir);
        Assert.Equal(LicenseValidation.Valid, svc.Validate(token).Result);
    }

    [Fact]
    public void BotIssuedExpiredKey_IsRejectedByTerminal()
    {
        var token = BotSigner.CreateToken(
            new BotInfo("Late Co", "Pro", DateTime.UtcNow.AddDays(-1), null, DateTime.UtcNow), _privatePem);

        var svc = new LicenseService(_publicPem, Machine, _dir);
        Assert.Equal(LicenseValidation.Expired, svc.Validate(token).Result);
    }
}
