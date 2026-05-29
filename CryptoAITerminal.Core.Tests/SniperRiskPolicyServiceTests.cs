using System;
using System.Collections.Generic;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class SniperRiskPolicyServiceTests
{
    private static SniperRiskLimits DefaultLimits(
        int cooldown = 0,
        int maxPositions = 5,
        int maxBuys = 20,
        decimal maxDailyLoss = 1m,
        decimal maxChainExposure = 1m,
        decimal maxWalletExposure = 2m,
        int maxConsecutiveLosses = 3,
        decimal hardCap = 5m) =>
        new(cooldown, maxPositions, maxBuys, maxDailyLoss, maxChainExposure,
            maxWalletExposure, maxConsecutiveLosses, hardCap);

    private static SniperCandidateViewModel MakeLivePosition(
        string chainId, decimal entryCost, decimal realizedProceeds = 0m)
    {
        var token = new DexTokenInfo { ChainId = chainId };
        var vm = new SniperCandidateViewModel(token, "test", true)
        {
            IsOpenPosition       = true,
            UsesLiveAccounting   = true,
            LiveEntryCostNative  = entryCost,
            LiveRealizedProceedsNative = realizedProceeds,
        };
        return vm;
    }

    private static SniperCandidateViewModel MakePaperPosition(string chainId, decimal entryAmount)
    {
        var token = new DexTokenInfo { ChainId = chainId };
        var vm = new SniperCandidateViewModel(token, "paper", true)
        {
            IsOpenPosition     = true,
            UsesLiveAccounting = false,
            EntryAmountBnb     = entryAmount,
        };
        return vm;
    }

    private static PaperTradeRecordViewModel MakeTrade(DateTime closedAt, decimal netPnlNative) =>
        new()
        {
            ExecutionMode = "Live",
            ClosedAtLocal = closedAt,
            NetPnlNative  = netPnlNative,
        };

    // ── BuildSnapshot ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildSnapshot_AggregatesExposureByChain()
    {
        var svc = new SniperRiskPolicyService();
        var positions = new[]
        {
            MakeLivePosition("bsc", 0.5m),
            MakeLivePosition("bsc", 0.3m),
            MakeLivePosition("ethereum", 1.0m),
        };

        var snapshot = svc.BuildSnapshot(positions, Array.Empty<PaperTradeRecordViewModel>(), DateTime.Now);

        Assert.Equal(0.8m, snapshot.GetChainExposure("bsc"));
        Assert.Equal(1.0m, snapshot.GetChainExposure("ethereum"));
        Assert.Equal(1.8m, snapshot.TotalLiveExposureNative);
    }

    [Fact]
    public void BuildSnapshot_NetsRealizedProceedsAgainstEntryCost()
    {
        var svc = new SniperRiskPolicyService();
        var pos = MakeLivePosition("bsc", entryCost: 1m, realizedProceeds: 0.4m);

        var snapshot = svc.BuildSnapshot(new[] { pos }, Array.Empty<PaperTradeRecordViewModel>(), DateTime.Now);

        Assert.Equal(0.6m, snapshot.TotalLiveExposureNative);
    }

    [Fact]
    public void BuildSnapshot_OnlyTodayLossesCountInDailyLoss()
    {
        var svc = new SniperRiskPolicyService();
        var today = new DateTime(2026, 5, 25, 10, 0, 0);
        var yesterday = today.AddDays(-1);

        var history = new[]
        {
            MakeTrade(today,     -0.2m),
            MakeTrade(today,     -0.1m),
            MakeTrade(yesterday, -1.0m),   // не сегодня — не учитывается
            MakeTrade(today,      0.5m),   // прибыль — не учитывается
        };

        var snapshot = svc.BuildSnapshot(
            Array.Empty<SniperCandidateViewModel>(), history, today);

        Assert.Equal(0.3m, snapshot.DailyLiveLossNative);
    }

    [Fact]
    public void BuildSnapshot_CountsConsecutiveLossesFromMostRecent()
    {
        var svc = new SniperRiskPolicyService();
        var t0 = new DateTime(2026, 5, 25, 12, 0, 0);

        // Самая свежая первой по ClosedAtLocal:
        var history = new[]
        {
            MakeTrade(t0.AddMinutes(-1), -0.1m),  // самый последний — лосс
            MakeTrade(t0.AddMinutes(-2), -0.2m),  // лосс
            MakeTrade(t0.AddMinutes(-3),  0.3m),  // прибыль — обрывает цепочку
            MakeTrade(t0.AddMinutes(-4), -0.5m),  // не считается
        };

        var snapshot = svc.BuildSnapshot(
            Array.Empty<SniperCandidateViewModel>(), history, t0);

        Assert.Equal(2, snapshot.ConsecutiveLiveLosses);
    }

    [Fact]
    public void BuildSnapshot_BlankChainIdNormalizedToUnknown()
    {
        var svc = new SniperRiskPolicyService();
        var token = new DexTokenInfo { ChainId = "" };
        var pos = new SniperCandidateViewModel(token, "x", true)
        {
            IsOpenPosition = true,
            UsesLiveAccounting = true,
            LiveEntryCostNative = 0.5m,
        };

        var snapshot = svc.BuildSnapshot(new[] { pos }, Array.Empty<PaperTradeRecordViewModel>(), DateTime.Now);

        Assert.Equal(0.5m, snapshot.GetChainExposure("unknown"));
        Assert.Equal(0.5m, snapshot.GetChainExposure(null));
    }

    // ── EvaluateEntry: gating ─────────────────────────────────────────────────

    [Fact]
    public void EvaluateEntry_CooldownBlocksBuy()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);
        var lastBuy = nowUtc.AddSeconds(-3);
        var limits = DefaultLimits(cooldown: 10);

        var decision = svc.EvaluateEntry(
            isLiveMode: true,
            chainId: "bsc",
            proposedEntryNative: 0.1m,
            openLivePositions: Array.Empty<SniperCandidateViewModel>(),
            openPaperPositions: Array.Empty<SniperCandidateViewModel>(),
            liveTradeHistory: Array.Empty<PaperTradeRecordViewModel>(),
            sessionBuyCount: 0,
            lastBuyUtc: lastBuy,
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: limits);

        Assert.False(decision.CanEnter);
        Assert.Contains("Cooling", decision.Reason);
    }

    [Fact]
    public void EvaluateEntry_CooldownNotActiveAfterExpiry()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

        var decision = svc.EvaluateEntry(
            isLiveMode: true,
            chainId: "bsc",
            proposedEntryNative: 0.1m,
            openLivePositions: Array.Empty<SniperCandidateViewModel>(),
            openPaperPositions: Array.Empty<SniperCandidateViewModel>(),
            liveTradeHistory: Array.Empty<PaperTradeRecordViewModel>(),
            sessionBuyCount: 0,
            lastBuyUtc: nowUtc.AddSeconds(-20),
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: DefaultLimits(cooldown: 10));

        Assert.True(decision.CanEnter);
    }

    [Fact]
    public void EvaluateEntry_SimultaneousPositionsCapEnforced()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = DateTime.UtcNow;
        var open = new List<SniperCandidateViewModel>
        {
            MakeLivePosition("bsc", 0.1m),
            MakeLivePosition("bsc", 0.1m),
        };
        var paper = new List<SniperCandidateViewModel>
        {
            MakePaperPosition("ethereum", 0.1m),
        };

        var decision = svc.EvaluateEntry(
            true, "bsc", 0.1m, open, paper,
            Array.Empty<PaperTradeRecordViewModel>(),
            sessionBuyCount: 0,
            lastBuyUtc: null,
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: DefaultLimits(maxPositions: 3));

        Assert.False(decision.CanEnter);
        Assert.Contains("Open position limit", decision.Reason);
    }

    [Fact]
    public void EvaluateEntry_SessionBuyLimitEnforced()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = DateTime.UtcNow;

        var decision = svc.EvaluateEntry(
            true, "bsc", 0.1m,
            Array.Empty<SniperCandidateViewModel>(),
            Array.Empty<SniperCandidateViewModel>(),
            Array.Empty<PaperTradeRecordViewModel>(),
            sessionBuyCount: 5,
            lastBuyUtc: null,
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: DefaultLimits(maxBuys: 5));

        Assert.False(decision.CanEnter);
        Assert.Contains("Session buy limit", decision.Reason);
    }

    [Fact]
    public void EvaluateEntry_PaperModeSkipsLiveOnlyChecks()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);
        var history = new[]
        {
            MakeTrade(nowUtc.ToLocalTime().AddMinutes(-1), -1m),
            MakeTrade(nowUtc.ToLocalTime().AddMinutes(-2), -1m),
            MakeTrade(nowUtc.ToLocalTime().AddMinutes(-3), -1m),
            MakeTrade(nowUtc.ToLocalTime().AddMinutes(-4), -1m),
        };

        var decision = svc.EvaluateEntry(
            isLiveMode: false,
            chainId: "bsc",
            proposedEntryNative: 999m,
            openLivePositions: Array.Empty<SniperCandidateViewModel>(),
            openPaperPositions: Array.Empty<SniperCandidateViewModel>(),
            liveTradeHistory: history,
            sessionBuyCount: 0,
            lastBuyUtc: null,
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: DefaultLimits(maxConsecutiveLosses: 3, maxDailyLoss: 1m, maxWalletExposure: 1m));

        Assert.True(decision.CanEnter);
    }

    [Fact]
    public void EvaluateEntry_ConsecutiveLossesTriggerEmergencyStop()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);
        var history = new[]
        {
            MakeTrade(nowUtc.ToLocalTime().AddMinutes(-1), -0.1m),
            MakeTrade(nowUtc.ToLocalTime().AddMinutes(-2), -0.1m),
            MakeTrade(nowUtc.ToLocalTime().AddMinutes(-3), -0.1m),
        };

        var decision = svc.EvaluateEntry(
            true, "bsc", 0.1m,
            Array.Empty<SniperCandidateViewModel>(),
            Array.Empty<SniperCandidateViewModel>(),
            history,
            0, null, nowUtc.ToLocalTime(), nowUtc,
            DefaultLimits(maxConsecutiveLosses: 3, maxDailyLoss: 10m));

        Assert.False(decision.CanEnter);
        Assert.Contains("Emergency stop", decision.Reason);
        Assert.Equal(3, decision.Snapshot.ConsecutiveLiveLosses);
    }

    [Fact]
    public void EvaluateEntry_DailyLossCapBlocks()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);
        var localNow = nowUtc.ToLocalTime();

        var history = new[]
        {
            MakeTrade(localNow.AddMinutes(-5), -0.6m),
            MakeTrade(localNow.AddMinutes(-2),  0.1m), // прибыль обрывает consecutive-цепочку
        };

        var decision = svc.EvaluateEntry(
            true, "bsc", 0.1m,
            Array.Empty<SniperCandidateViewModel>(),
            Array.Empty<SniperCandidateViewModel>(),
            history,
            0, null, localNow, nowUtc,
            DefaultLimits(maxDailyLoss: 0.5m));

        Assert.False(decision.CanEnter);
        Assert.Contains("Daily live-loss", decision.Reason);
    }

    [Fact]
    public void EvaluateEntry_WalletExposureCapBlocksProjectedSum()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = DateTime.UtcNow;
        var positions = new[] { MakeLivePosition("bsc", 0.7m) };

        var decision = svc.EvaluateEntry(
            true, "bsc",
            proposedEntryNative: 0.5m,
            openLivePositions: positions,
            openPaperPositions: Array.Empty<SniperCandidateViewModel>(),
            liveTradeHistory: Array.Empty<PaperTradeRecordViewModel>(),
            sessionBuyCount: 0,
            lastBuyUtc: null,
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: DefaultLimits(maxWalletExposure: 1.0m));

        Assert.False(decision.CanEnter);
        Assert.Contains("Wallet exposure", decision.Reason);
    }

    [Fact]
    public void EvaluateEntry_HardCapBlocksOverWalletCap()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = DateTime.UtcNow;
        var positions = new[] { MakeLivePosition("bsc", 0.3m) };

        var decision = svc.EvaluateEntry(
            true, "bsc",
            proposedEntryNative: 0.3m,
            openLivePositions: positions,
            openPaperPositions: Array.Empty<SniperCandidateViewModel>(),
            liveTradeHistory: Array.Empty<PaperTradeRecordViewModel>(),
            sessionBuyCount: 0,
            lastBuyUtc: null,
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: DefaultLimits(maxWalletExposure: 10m, hardCap: 0.5m));

        Assert.False(decision.CanEnter);
        Assert.Contains("Hard live-exposure", decision.Reason);
    }

    [Fact]
    public void EvaluateEntry_ChainExposureCapBlocks()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = DateTime.UtcNow;
        var positions = new[]
        {
            MakeLivePosition("bsc", 0.6m),
            MakeLivePosition("ethereum", 0.1m),
        };

        var decision = svc.EvaluateEntry(
            true, "bsc",
            proposedEntryNative: 0.5m,
            openLivePositions: positions,
            openPaperPositions: Array.Empty<SniperCandidateViewModel>(),
            liveTradeHistory: Array.Empty<PaperTradeRecordViewModel>(),
            sessionBuyCount: 0,
            lastBuyUtc: null,
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: DefaultLimits(maxChainExposure: 1.0m, maxWalletExposure: 10m));

        Assert.False(decision.CanEnter);
        Assert.Contains("Chain exposure", decision.Reason);
    }

    [Fact]
    public void EvaluateEntry_HappyPath_PassesSafetyGate()
    {
        var svc = new SniperRiskPolicyService();
        var nowUtc = DateTime.UtcNow;

        var decision = svc.EvaluateEntry(
            true, "bsc",
            proposedEntryNative: 0.1m,
            openLivePositions: Array.Empty<SniperCandidateViewModel>(),
            openPaperPositions: Array.Empty<SniperCandidateViewModel>(),
            liveTradeHistory: Array.Empty<PaperTradeRecordViewModel>(),
            sessionBuyCount: 0,
            lastBuyUtc: null,
            nowLocal: nowUtc.ToLocalTime(),
            nowUtc: nowUtc,
            limits: DefaultLimits());

        Assert.True(decision.CanEnter);
        Assert.Contains("Safety gate", decision.Reason);
    }

    // ── IsEmergencyStopActive ─────────────────────────────────────────────────

    [Fact]
    public void IsEmergencyStopActive_TrueWhenLossesAtOrAboveCap()
    {
        var svc = new SniperRiskPolicyService();
        var snap = new SniperRiskSnapshot(0m, 0m, new Dictionary<string, decimal>(), 3);

        Assert.True(svc.IsEmergencyStopActive(snap, DefaultLimits(maxConsecutiveLosses: 3)));
    }

    [Fact]
    public void IsEmergencyStopActive_FalseWhenCapDisabled()
    {
        var svc = new SniperRiskPolicyService();
        var snap = new SniperRiskSnapshot(0m, 0m, new Dictionary<string, decimal>(), 99);

        Assert.False(svc.IsEmergencyStopActive(snap, DefaultLimits(maxConsecutiveLosses: 0)));
    }
}
