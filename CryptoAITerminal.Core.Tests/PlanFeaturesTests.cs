using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Client-side tier gating. The server is the authority; this only decides what the terminal
/// offers, so the failure that matters is locking a paying customer out of something they bought.
/// </summary>
public class PlanFeaturesTests
{
    [Theory]
    [InlineData("Lite", PlanTier.Lite)]
    [InlineData("lite", PlanTier.Lite)]
    [InlineData(" PRO ", PlanTier.Pro)]
    [InlineData("Max", PlanTier.Max)]
    public void Parses_the_editions_the_bot_issues(string edition, PlanTier expected) =>
        Assert.Equal(expected, PlanFeatures.Parse(edition));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Enterprise")]
    public void An_unknown_edition_gets_everything(string? edition)
    {
        // Fail-open on purpose. An unrecognised edition means a plan was added and this list was
        // not updated; the person who should find that out is us, not a customer locked out of
        // what they paid for. The server still enforces the real limit.
        var tier = PlanFeatures.Parse(edition);
        Assert.True(PlanFeatures.CanUseSignalDesk(tier));
        Assert.True(PlanFeatures.CanUseAgentPaper(tier));
        Assert.True(PlanFeatures.CanUseAgentLive(tier));
    }

    [Fact]
    public void Lite_gets_the_basics_and_not_the_heavy_panels()
    {
        Assert.False(PlanFeatures.CanUseSignalDesk(PlanTier.Lite));
        Assert.False(PlanFeatures.CanUseAgentPaper(PlanTier.Lite));
        Assert.False(PlanFeatures.CanUseAgentLive(PlanTier.Lite));
    }

    [Fact]
    public void Pro_gets_the_panels_and_the_paper_agent_but_not_live()
    {
        Assert.True(PlanFeatures.CanUseSignalDesk(PlanTier.Pro));
        Assert.True(PlanFeatures.CanUseAgentPaper(PlanTier.Pro));
        // The one gate that moves real money stays on the top tier.
        Assert.False(PlanFeatures.CanUseAgentLive(PlanTier.Pro));
    }

    [Fact]
    public void Max_gets_everything()
    {
        Assert.True(PlanFeatures.CanUseSignalDesk(PlanTier.Max));
        Assert.True(PlanFeatures.CanUseAgentPaper(PlanTier.Max));
        Assert.True(PlanFeatures.CanUseAgentLive(PlanTier.Max));
    }

    [Fact]
    public void A_locked_panel_names_the_tier_that_unlocks_it()
    {
        // "Недоступно" without a next step is a dead end; the notice has to say what to buy.
        Assert.Contains("Max", PlanFeatures.LockedNotice("agent_live"));
        Assert.Contains("Pro", PlanFeatures.LockedNotice("signal_desk"));
    }

    [Fact]
    public void Tiers_are_ordered_so_comparisons_hold()
    {
        Assert.True(PlanTier.Lite < PlanTier.Pro);
        Assert.True(PlanTier.Pro < PlanTier.Max);
    }
}
