using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DexSecuritySummaryTests
{
    [Fact]
    public void Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DexSecuritySummary.Build(null));
    }

    [Fact]
    public void Honeypot_EmitsHoneypotKeyword_AndSource()
    {
        var r = new TokenSecurityResult
        {
            IsHoneypot = true,
            IsOwnershipRenounced = true,
            Source = "GoPlus + Honeypot.is"
        };

        var s = DexSecuritySummary.Build(r);

        Assert.Contains("honeypot", s);
        Assert.Contains("GoPlus + Honeypot.is", s);
    }

    [Fact]
    public void Taxes_AreFormattedInvariant()
    {
        var r = new TokenSecurityResult
        {
            IsOwnershipRenounced = true,
            BuyTaxPercent = 5m,
            SellTaxPercent = 12.5m,
            Source = "GoPlus"
        };

        var s = DexSecuritySummary.Build(r);

        Assert.Contains("buy tax 5%", s);
        Assert.Contains("sell tax 12.5%", s);
    }

    [Fact]
    public void CleanResult_ReportsCleanWithScore()
    {
        var r = new TokenSecurityResult
        {
            IsOwnershipRenounced = true,
            SecurityScore = 88,
            Source = "GoPlus"
        };

        var s = DexSecuritySummary.Build(r);

        Assert.Contains("clean", s);
        Assert.Contains("88/100", s);
    }
}
