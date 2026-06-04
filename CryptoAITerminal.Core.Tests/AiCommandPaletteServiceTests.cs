using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// The Ctrl+K command bar's deterministic intent parser: navigation phrases resolve to
/// section keys (the same tokens SelectMainTab accepts), while genuine questions stay
/// questions so they reach the AI copilot.
/// </summary>
public class AiCommandPaletteServiceTests
{
    private readonly AiCommandPaletteService _svc = new();

    [Theory]
    [InlineData("open scanner", "scanner")]
    [InlineData("go to settings", "settings")]
    [InlineData("settings", "settings")]
    [InlineData("liquidations", "liquidation")]
    [InlineData("ai signals", "ai-signals")]
    [InlineData("open gas", "gas")]
    [InlineData("show whales", "whale")]
    [InlineData("покажи киты", "whale")]
    [InlineData("перейти в настройки", "settings")]
    public void Navigation_Phrases_ResolveToSection(string input, string expectedKey)
    {
        var r = _svc.Parse(input);
        Assert.Equal(AiCommandPaletteService.Intent.Navigate, r.Intent);
        Assert.Equal(expectedKey, r.SectionKey);
        Assert.False(string.IsNullOrWhiteSpace(r.SectionLabel));
    }

    [Theory]
    [InlineData("what's my biggest risk right now?")]
    [InlineData("summarize my positions")]
    [InlineData("how is the market looking?")]
    [InlineData("should I buy ETH here?")]
    public void Questions_StayQuestions(string input)
    {
        var r = _svc.Parse(input);
        Assert.Equal(AiCommandPaletteService.Intent.Question, r.Intent);
        Assert.Null(r.SectionKey);
        Assert.Equal(input, r.Query);
    }

    [Fact]
    public void Empty_IsTreatedAsQuestion()
    {
        var r = _svc.Parse("   ");
        Assert.Equal(AiCommandPaletteService.Intent.Question, r.Intent);
        Assert.Equal(string.Empty, r.Query);
    }

    [Fact]
    public void Gas_WordBoundary_DoesNotMatchInsideOtherWords()
    {
        // "gasoline" must not trigger the Gas destination; with no verb and >2 words
        // this is plainly a question.
        var r = _svc.Parse("is gasoline related to crypto somehow");
        Assert.Equal(AiCommandPaletteService.Intent.Question, r.Intent);
    }
}
