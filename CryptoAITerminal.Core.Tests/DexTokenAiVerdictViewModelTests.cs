using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.Core.Tests;

public class DexTokenAiVerdictViewModelTests
{
    [Fact]
    public void Initial_HasNoVerdict_AndPendingBadge()
    {
        var vm = new DexTokenAiVerdictViewModel();
        Assert.False(vm.HasVerdict);
        Assert.Equal("PENDING", vm.Badge);
        Assert.False(vm.HasDeepScanNote);
    }

    [Fact]
    public void ApplyVerdict_MapsBadgeScoreAccentAndFlags()
    {
        var vm = new DexTokenAiVerdictViewModel();
        vm.ApplyVerdict(new TokenAiVerdict
        {
            Verdict = "AVOID",
            RiskScore = 82,
            RedFlags = new[] { "honeypot signal", "very thin liquidity" },
            Reason = "High risk profile.",
            Source = "Heuristic (offline)"
        });

        Assert.True(vm.HasVerdict);
        Assert.Equal("AVOID", vm.Badge);
        Assert.Equal("#FF5D73", vm.AccentHex);
        Assert.Equal("82/100", vm.ScoreLabel);
        Assert.Equal("honeypot signal · very thin liquidity", vm.RedFlagsText);
        Assert.Equal("High risk profile.", vm.Reason);
        Assert.Equal("Heuristic (offline)", vm.SourceLabel);
    }

    [Fact]
    public void DeepScanNote_TogglesHasDeepScanNote()
    {
        var vm = new DexTokenAiVerdictViewModel();
        Assert.False(vm.HasDeepScanNote);
        vm.DeepScanNote = "Deep scan unavailable: RugCheck.xyz";
        Assert.True(vm.HasDeepScanNote);
    }

    [Fact]
    public void Reset_ClearsVerdictAndNote()
    {
        var vm = new DexTokenAiVerdictViewModel();
        vm.ApplyVerdict(new TokenAiVerdict { Verdict = "FAVORABLE", RiskScore = 10 });
        vm.DeepScanNote = "x";

        vm.Reset();

        Assert.False(vm.HasVerdict);
        Assert.Equal("PENDING", vm.Badge);
        Assert.Null(vm.DeepScanNote);
        Assert.False(vm.HasDeepScanNote);
    }
}
