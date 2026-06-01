using System;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Covers the offline digest path of <see cref="NewsAiSummaryService"/>
/// (no API key → deterministic summary, no network).
/// </summary>
public class NewsAiSummaryServiceTests
{
    private static NewsAiSummaryService Offline() => new() { ApiKey = string.Empty };

    private static readonly string[] Headlines =
    {
        "Bitcoin ETF sees record inflows",
        "Ethereum upgrade ships on schedule",
        "Altcoins rally as sentiment improves"
    };

    [Fact]
    public void OfflineService_DoesNotUseLiveModel()
    {
        Assert.False(Offline().UsesLiveModel);
    }

    [Fact]
    public async Task NoHeadlines_ReturnsNeutralPlaceholder()
    {
        var digest = await Offline().SummarizeAsync([], 0, 0, 0);

        Assert.True(digest.IsFallback);
        Assert.Equal("NEUTRAL", digest.Bias);
        Assert.Contains("No fresh headlines", digest.Summary);
    }

    [Fact]
    public async Task BullishMajority_ProducesBullishBias()
    {
        var digest = await Offline().SummarizeAsync(Headlines, bullish: 7, bearish: 1, neutral: 2);

        Assert.Equal("BULLISH", digest.Bias);
        Assert.Contains("lean bullish", digest.Summary);
        Assert.Contains("Top:", digest.Summary);
    }

    [Fact]
    public async Task BearishMajority_ProducesBearishBias()
    {
        var digest = await Offline().SummarizeAsync(Headlines, bullish: 1, bearish: 8, neutral: 1);

        Assert.Equal("BEARISH", digest.Bias);
        Assert.Contains("lean bearish", digest.Summary);
    }

    [Fact]
    public async Task BalancedCounts_ProduceNeutralBias()
    {
        var digest = await Offline().SummarizeAsync(Headlines, bullish: 3, bearish: 3, neutral: 3);

        Assert.Equal("NEUTRAL", digest.Bias);
        Assert.Contains("Heuristic", digest.Source);
    }
}
