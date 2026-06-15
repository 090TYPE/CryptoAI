using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.Core.Tests;

public class SniperLiveReadinessServiceTests
{
    // A tiny-dry-run buy-filled record that carries every dex-level and chain-level
    // boolean check the readiness gate inspects. Combined with FullSell() it satisfies
    // all nine required checks for a non-solana target.
    private static SniperExecutionAuditRecordViewModel BuyFilled(string chain, string dex) => new()
    {
        ChainId = chain,
        DexId = dex,
        EventType = "buy-filled",
        IsTinyDryRun = true,
        ReceiptParsed = true,
        DecimalsVerified = true,
        SlippageProtected = true,
        BalanceSynchronized = true,
        ApproveWasRequired = true,
        RpcRecoveryObserved = true,
        PositionRestored = true
    };

    private static SniperExecutionAuditRecordViewModel FullSell(string chain, string dex) => new()
    {
        ChainId = chain,
        DexId = dex,
        EventType = "full-sell",
        IsTinyDryRun = true
    };

    private static List<SniperExecutionAuditRecordViewModel> CompleteEvidence(string chain, string dex) =>
        [BuyFilled(chain, dex), FullSell(chain, dex)];

    private static SniperLiveReadyStatusViewModel StatusFor(
        IReadOnlyList<SniperLiveReadyStatusViewModel> statuses, string chainUpper) =>
        statuses.Single(status => status.NetworkLabel == chainUpper);

    [Fact]
    public void BuildStatuses_AlwaysReturnsFourKnownTargets()
    {
        var statuses = new SniperLiveReadinessService().BuildStatuses([]);

        Assert.Equal(4, statuses.Count);
        Assert.Equal(
            new[] { "BSC", "ETHEREUM", "BASE", "SOLANA" },
            statuses.Select(status => status.NetworkLabel).ToArray());
        Assert.Equal(
            new[] { "pancakeswap", "uniswap", "aerodrome", "jupiter" },
            statuses.Select(status => status.DexLabel).ToArray());
    }

    [Fact]
    public void BuildStatuses_NoAudits_AllTargetsNotReady()
    {
        var statuses = new SniperLiveReadinessService().BuildStatuses([]);

        Assert.All(statuses, status => Assert.False(status.IsReady));
        Assert.All(statuses, status => Assert.Equal("evidence missing", status.StatusLabel));
    }

    [Fact]
    public void BuildStatuses_CompleteEvidence_MarksOnlyThatTargetReady()
    {
        var statuses = new SniperLiveReadinessService().BuildStatuses(CompleteEvidence("bsc", "pancakeswap"));

        var bsc = StatusFor(statuses, "BSC");
        Assert.True(bsc.IsReady);
        Assert.Equal("live-ready evidence complete", bsc.StatusLabel);
        // Evidence is scoped per chain/dex - other targets stay unready.
        Assert.False(StatusFor(statuses, "ETHEREUM").IsReady);
        Assert.False(StatusFor(statuses, "SOLANA").IsReady);
    }

    [Theory]
    [InlineData("tinyBuy")]
    [InlineData("fullSell")]
    [InlineData("receipt")]
    [InlineData("decimals")]
    [InlineData("slippage")]
    [InlineData("balanceSync")]
    [InlineData("approve")]
    [InlineData("rpc")]
    [InlineData("restart")]
    public void BuildStatuses_MissingAnyRequiredCheck_NotReady(string drop)
    {
        var buy = BuyFilled("bsc", "pancakeswap");
        var sell = FullSell("bsc", "pancakeswap");
        switch (drop)
        {
            case "tinyBuy": buy.IsTinyDryRun = false; break;
            case "fullSell": sell.IsTinyDryRun = false; break;
            case "receipt": buy.ReceiptParsed = false; break;
            case "decimals": buy.DecimalsVerified = false; break;
            case "slippage": buy.SlippageProtected = false; break;
            case "balanceSync": buy.BalanceSynchronized = false; break;
            case "approve": buy.ApproveWasRequired = false; break;
            case "rpc": buy.RpcRecoveryObserved = false; break;
            case "restart": buy.PositionRestored = false; break;
        }

        var statuses = new SniperLiveReadinessService().BuildStatuses([buy, sell]);

        Assert.False(StatusFor(statuses, "BSC").IsReady);
    }

    [Theory]
    [InlineData("partial-tp")]
    [InlineData("stop-loss")]
    [InlineData("trailing")]
    public void BuildStatuses_OptionalChecksMissing_StillReady(string _)
    {
        // CompleteEvidence omits partial-TP / stop-loss / trailing-stop audits entirely.
        var statuses = new SniperLiveReadinessService().BuildStatuses(CompleteEvidence("bsc", "pancakeswap"));

        Assert.True(StatusFor(statuses, "BSC").IsReady);
    }

    [Fact]
    public void BuildStatuses_NonDryRunBuy_DoesNotCountAsTinyBuy()
    {
        var buy = BuyFilled("bsc", "pancakeswap");
        buy.IsTinyDryRun = false; // a real buy must not satisfy the dry-run gate
        var statuses = new SniperLiveReadinessService().BuildStatuses([buy, FullSell("bsc", "pancakeswap")]);

        Assert.False(StatusFor(statuses, "BSC").IsReady);
    }

    [Fact]
    public void BuildStatuses_Solana_ApproveNotRequired_StillReady()
    {
        // Solana has no ERC20-style approve step, so approvePath is satisfied implicitly.
        var buy = BuyFilled("solana", "jupiter");
        buy.ApproveWasRequired = false;
        var statuses = new SniperLiveReadinessService().BuildStatuses([buy, FullSell("solana", "jupiter")]);

        Assert.True(StatusFor(statuses, "SOLANA").IsReady);
    }

    [Fact]
    public void BuildStatuses_SolanaDexMatchedByJupiterSubstring()
    {
        // Solana audits match on any dex id containing "jupiter" (e.g. versioned aggregator ids).
        var buy = BuyFilled("solana", "jupiter-v6");
        buy.ApproveWasRequired = false;
        var statuses = new SniperLiveReadinessService().BuildStatuses([buy, FullSell("solana", "jupiter-v6")]);

        Assert.True(StatusFor(statuses, "SOLANA").IsReady);
    }

    [Fact]
    public void BuildStatuses_ChainLevelChecksAcceptEvidenceFromAnotherDexOnSameChain()
    {
        // RPC recovery and restart restore are chain-scoped, not dex-scoped: a record on a
        // different bsc dex still counts toward them.
        var buy = BuyFilled("bsc", "pancakeswap");
        buy.RpcRecoveryObserved = false;
        buy.PositionRestored = false;

        var otherDexChainEvidence = new SniperExecutionAuditRecordViewModel
        {
            ChainId = "bsc",
            DexId = "biswap",
            RpcRecoveryObserved = true,
            PositionRestored = true
        };

        var statuses = new SniperLiveReadinessService()
            .BuildStatuses([buy, FullSell("bsc", "pancakeswap"), otherDexChainEvidence]);

        Assert.True(StatusFor(statuses, "BSC").IsReady);
    }

    [Fact]
    public void BuildStatuses_ChainAndDexMatchingIsCaseInsensitive()
    {
        var statuses = new SniperLiveReadinessService().BuildStatuses(CompleteEvidence("BSC", "PancakeSwap"));

        Assert.True(StatusFor(statuses, "BSC").IsReady);
    }
}
