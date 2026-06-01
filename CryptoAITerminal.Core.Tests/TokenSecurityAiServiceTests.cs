using System;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Covers the offline heuristic path of <see cref="TokenSecurityAiService"/>
/// (no API key → deterministic verdict, no network). The live Claude path is
/// not exercised here as it requires an API key and network access.
/// </summary>
public class TokenSecurityAiServiceTests
{
    private static TokenSecurityAiService NewOfflineService() =>
        new() { ApiKey = string.Empty };

    private static DexTokenInfo CleanToken() => new()
    {
        ChainId = "bsc",
        DexId = "pancakeswap",
        PairAddress = "0x1234567890abcdef1234567890abcdef12345678",
        TokenAddress = "0xabcdef1234567890abcdef1234567890abcdef12",
        Symbol = "GOOD",
        Name = "Good Token",
        QuoteSymbol = "WBNB",
        PriceUsd = 1.5m,
        LiquidityUsd = 500_000m,
        Volume24h = 250_000m,
        MarketCap = 2_000_000m,
        PriceChange5m = 1m,
        PriceChange1h = 3m,
        PriceChange24h = 10m,
        ObservedFirstSeenUtc = DateTime.UtcNow.AddHours(-6)
    };

    [Fact]
    public void OfflineService_DoesNotUseLiveModel()
    {
        var svc = NewOfflineService();
        Assert.False(svc.UsesLiveModel);
    }

    [Fact]
    public async Task CleanToken_ProducesFavorableLowRiskVerdict()
    {
        var svc = NewOfflineService();
        var verdict = await svc.AssessAsync(CleanToken());

        Assert.True(verdict.IsFallback);
        Assert.Equal("FAVORABLE", verdict.Verdict);
        Assert.True(verdict.RiskScore < 25);
        Assert.Contains("offline", verdict.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThinLiquidityPumpingToken_IsFlaggedRisky()
    {
        var token = CleanToken();
        token.LiquidityUsd = 10_000m;
        token.PriceChange1h = 200m;
        token.ObservedFirstSeenUtc = DateTime.UtcNow.AddMinutes(-2);

        var verdict = await NewOfflineService().AssessAsync(token);

        Assert.True(verdict.RiskScore >= 45);
        Assert.Contains(verdict.Verdict, new[] { "RISKY", "AVOID" });
        Assert.NotEmpty(verdict.RedFlags);
    }

    [Fact]
    public async Task HoneypotSecuritySummary_PushesVerdictToAvoid()
    {
        var token = CleanToken();
        token.LiquidityUsd = 12_000m;

        var verdict = await NewOfflineService()
            .AssessAsync(token, securitySummary: "🛑 Honeypot ⚠ Mintable");

        Assert.Equal("AVOID", verdict.Verdict);
        Assert.True(verdict.RiskScore >= 70);
    }

    [Fact]
    public async Task Assessment_IsCachedPerToken()
    {
        var svc = NewOfflineService();
        var token = CleanToken();

        var first = await svc.AssessAsync(token);
        var second = await svc.AssessAsync(token);

        Assert.Same(first, second);
    }
}
