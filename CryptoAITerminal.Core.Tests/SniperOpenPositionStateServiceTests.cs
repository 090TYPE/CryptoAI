using System;
using System.Collections.Generic;
using System.IO;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.Core.Tests;

public class SniperOpenPositionStateServiceTests
{
    // Disposable scratch path so each test round-trips through a real file and cleans up.
    private sealed class TempPath : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sniper-pos-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    // A candidate carrying distinctive, non-default values in every economically
    // significant field so a dropped field surfaces as a failed assertion.
    private static SniperCandidateViewModel SampleCandidate()
    {
        var token = new DexTokenInfo
        {
            ChainId = "bsc",
            DexId = "pancakeswap",
            TokenAddress = "0xabc123",
            Symbol = "MOON",
            Name = "Moonshot",
            PriceUsd = 0.0042m,
            LiquidityUsd = 125_000m
        };

        return new SniperCandidateViewModel(token, "momentum breakout", passedFilters: true)
        {
            Status = "Open",
            OpenedAtLocal = new DateTime(2026, 6, 15, 10, 30, 0),
            EntryAmountBnb = 0.25m,
            EntryPriceUsd = 0.0040m,
            RiskScore = 73,
            RiskBand = "Medium",
            RiskSummary = "ok-ish",
            RiskFlags = "mint-renounced",
            IsExecutionBlocked = false,
            ExecutionVerdict = "allow",
            ExecutionBlockReason = string.Empty,
            AutoTakeProfitEnabled = true,
            TakeProfitPercent = 40m,
            AutoStopLossEnabled = true,
            StopLossPercent = 15m,
            AutoTrailingStopEnabled = true,
            TrailingStopPercent = 8m,
            PartialTakeProfitEnabled = true,
            PartialTakeProfitTriggerPercent = 25m,
            PartialTakeProfitSellPercent = 50m,
            PartialTakeProfitExecuted = false,
            BreakEvenEnabled = true,
            BreakEvenTriggerPercent = 12m,
            BreakEvenArmed = true,
            BreakEvenTriggered = false,
            PositionSizePercent = 30m,
            PeakPriceUsd = 0.0061m,
            TrackedTokenAmount = 1_500_000m,
            UsesLiveAccounting = true,
            LiveEntryCostNative = 0.25m,
            LiveRealizedProceedsNative = 0.10m,
            LiveEntryTokenAmount = 1_500_000m,
            EntryTxHash = "0xentry",
            LastExitTxHash = "0xexit",
            EntryDexId = "pancakeswap",
            TakeProfitTriggered = false,
            StopLossTriggered = false,
            TrailingStopTriggered = false
        };
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        using var temp = new TempPath();
        var loaded = new SniperOpenPositionStateService().Load(temp.Path);

        Assert.Empty(loaded);
    }

    [Fact]
    public void SaveLoad_EmptyList_LoadReturnsEmpty()
    {
        using var temp = new TempPath();
        var service = new SniperOpenPositionStateService();

        service.Save(temp.Path, []);
        var loaded = service.Load(temp.Path);

        Assert.Empty(loaded);
    }

    [Fact]
    public void SaveLoad_RoundTripsEconomicFields()
    {
        using var temp = new TempPath();
        var service = new SniperOpenPositionStateService();

        service.Save(temp.Path, [SampleCandidate()]);
        var restored = Assert.Single(service.Load(temp.Path));

        Assert.Equal(0.25m, restored.EntryAmountBnb);
        Assert.Equal(0.0040m, restored.EntryPriceUsd);
        Assert.True(restored.UsesLiveAccounting);
        Assert.Equal(0.25m, restored.LiveEntryCostNative);
        Assert.Equal(0.10m, restored.LiveRealizedProceedsNative);
        Assert.Equal(1_500_000m, restored.LiveEntryTokenAmount);
        Assert.Equal(1_500_000m, restored.TrackedTokenAmount);
        Assert.Equal(0.0061m, restored.PeakPriceUsd);
        Assert.Equal(30m, restored.PositionSizePercent);
        Assert.Equal("0xentry", restored.EntryTxHash);
        Assert.Equal("0xexit", restored.LastExitTxHash);
        Assert.Equal("pancakeswap", restored.EntryDexId);
    }

    [Fact]
    public void SaveLoad_RoundTripsExitStrategySettings()
    {
        using var temp = new TempPath();
        var service = new SniperOpenPositionStateService();

        service.Save(temp.Path, [SampleCandidate()]);
        var restored = Assert.Single(service.Load(temp.Path));

        Assert.True(restored.AutoTakeProfitEnabled);
        Assert.Equal(40m, restored.TakeProfitPercent);
        Assert.True(restored.AutoStopLossEnabled);
        Assert.Equal(15m, restored.StopLossPercent);
        Assert.True(restored.AutoTrailingStopEnabled);
        Assert.Equal(8m, restored.TrailingStopPercent);
        Assert.True(restored.PartialTakeProfitEnabled);
        Assert.Equal(25m, restored.PartialTakeProfitTriggerPercent);
        Assert.Equal(50m, restored.PartialTakeProfitSellPercent);
        Assert.True(restored.BreakEvenEnabled);
        Assert.Equal(12m, restored.BreakEvenTriggerPercent);
        Assert.True(restored.BreakEvenArmed);
    }

    [Fact]
    public void SaveLoad_RoundTripsTokenInfoAndRisk()
    {
        using var temp = new TempPath();
        var service = new SniperOpenPositionStateService();

        service.Save(temp.Path, [SampleCandidate()]);
        var restored = Assert.Single(service.Load(temp.Path));

        Assert.Equal("bsc", restored.TokenInfo.ChainId);
        Assert.Equal("0xabc123", restored.TokenInfo.TokenAddress);
        Assert.Equal("MOON", restored.TokenInfo.Symbol);
        Assert.Equal(73, restored.RiskScore);
        Assert.Equal("Medium", restored.RiskBand);
        Assert.Equal("mint-renounced", restored.RiskFlags);
        Assert.Equal("momentum breakout", restored.Reason);
        Assert.True(restored.PassedFilters);
    }

    [Fact]
    public void Load_RestoredPositionIsMarkedBoughtAndOpen()
    {
        using var temp = new TempPath();
        var service = new SniperOpenPositionStateService();

        service.Save(temp.Path, [SampleCandidate()]);
        var restored = Assert.Single(service.Load(temp.Path));

        Assert.True(restored.WasBought);
        Assert.True(restored.IsOpenPosition);
    }

    [Fact]
    public void SaveLoad_PreservesMultiplePositionsInOrder()
    {
        using var temp = new TempPath();
        var service = new SniperOpenPositionStateService();

        var first = SampleCandidate();
        first.TokenInfo.Symbol = "AAA";
        var second = SampleCandidate();
        second.TokenInfo.Symbol = "BBB";

        service.Save(temp.Path, [first, second]);
        var loaded = service.Load(temp.Path);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("AAA", loaded[0].TokenInfo.Symbol);
        Assert.Equal("BBB", loaded[1].TokenInfo.Symbol);
    }

    [Fact]
    public void Load_AppliesDefaultsForMinimalSnapshot()
    {
        using var temp = new TempPath();
        // A snapshot persisted before some fields existed (or written with nulls):
        // Load must substitute safe defaults rather than yield null state.
        File.WriteAllText(temp.Path, "[{}]");

        var restored = Assert.Single(new SniperOpenPositionStateService().Load(temp.Path));

        Assert.NotNull(restored.TokenInfo);
        Assert.Equal("Restored position", restored.Status);
        Assert.Equal("Unknown", restored.RiskBand);
        Assert.Equal(string.Empty, restored.EntryTxHash);
        Assert.True(restored.WasBought);
        Assert.True(restored.IsOpenPosition);
    }
}
