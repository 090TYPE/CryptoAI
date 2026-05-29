using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class SniperOpenPositionStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<SniperCandidateViewModel> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var snapshots = AtomicJsonFile.Read<List<SniperOpenPositionSnapshot>>(path) ?? [];
        return snapshots
            .Select(CreateCandidate)
            .ToList();
    }

    public void Save(string path, IEnumerable<SniperCandidateViewModel> positions)
    {
        var snapshots = positions.Select(CreateSnapshot).ToList();
        AtomicJsonFile.Write(path, snapshots, JsonOptions);
    }

    private static SniperOpenPositionSnapshot CreateSnapshot(SniperCandidateViewModel candidate)
    {
        return new SniperOpenPositionSnapshot
        {
            TokenInfo = candidate.TokenInfo,
            Reason = candidate.Reason,
            PassedFilters = candidate.PassedFilters,
            Status = candidate.Status,
            OpenedAtLocal = candidate.OpenedAtLocal,
            EntryAmountBnb = candidate.EntryAmountBnb,
            EntryPriceUsd = candidate.EntryPriceUsd,
            RiskScore = candidate.RiskScore,
            RiskBand = candidate.RiskBand,
            RiskSummary = candidate.RiskSummary,
            RiskFlags = candidate.RiskFlags,
            IsExecutionBlocked = candidate.IsExecutionBlocked,
            ExecutionVerdict = candidate.ExecutionVerdict,
            ExecutionBlockReason = candidate.ExecutionBlockReason,
            AutoTakeProfitEnabled = candidate.AutoTakeProfitEnabled,
            TakeProfitPercent = candidate.TakeProfitPercent,
            AutoStopLossEnabled = candidate.AutoStopLossEnabled,
            StopLossPercent = candidate.StopLossPercent,
            AutoTrailingStopEnabled = candidate.AutoTrailingStopEnabled,
            TrailingStopPercent = candidate.TrailingStopPercent,
            PartialTakeProfitEnabled = candidate.PartialTakeProfitEnabled,
            PartialTakeProfitTriggerPercent = candidate.PartialTakeProfitTriggerPercent,
            PartialTakeProfitSellPercent = candidate.PartialTakeProfitSellPercent,
            PartialTakeProfitExecuted = candidate.PartialTakeProfitExecuted,
            BreakEvenEnabled = candidate.BreakEvenEnabled,
            BreakEvenTriggerPercent = candidate.BreakEvenTriggerPercent,
            BreakEvenArmed = candidate.BreakEvenArmed,
            BreakEvenTriggered = candidate.BreakEvenTriggered,
            PositionSizePercent = candidate.PositionSizePercent,
            PeakPriceUsd = candidate.PeakPriceUsd,
            TrackedTokenAmount = candidate.TrackedTokenAmount,
            UsesLiveAccounting = candidate.UsesLiveAccounting,
            LiveEntryCostNative = candidate.LiveEntryCostNative,
            LiveRealizedProceedsNative = candidate.LiveRealizedProceedsNative,
            LiveEntryTokenAmount = candidate.LiveEntryTokenAmount,
            EntryTxHash = candidate.EntryTxHash,
            LastExitTxHash = candidate.LastExitTxHash,
            EntryDexId = candidate.EntryDexId,
            TakeProfitTriggered = candidate.TakeProfitTriggered,
            StopLossTriggered = candidate.StopLossTriggered,
            TrailingStopTriggered = candidate.TrailingStopTriggered
        };
    }

    private static SniperCandidateViewModel CreateCandidate(SniperOpenPositionSnapshot snapshot)
    {
        var candidate = new SniperCandidateViewModel(snapshot.TokenInfo ?? new DexTokenInfo(), snapshot.Reason ?? string.Empty, snapshot.PassedFilters)
        {
            Status = snapshot.Status ?? "Restored position",
            WasBought = true,
            IsOpenPosition = true,
            OpenedAtLocal = snapshot.OpenedAtLocal,
            EntryAmountBnb = snapshot.EntryAmountBnb,
            EntryPriceUsd = snapshot.EntryPriceUsd,
            RiskScore = snapshot.RiskScore,
            RiskBand = snapshot.RiskBand ?? "Unknown",
            RiskSummary = snapshot.RiskSummary ?? string.Empty,
            RiskFlags = snapshot.RiskFlags ?? string.Empty,
            IsExecutionBlocked = snapshot.IsExecutionBlocked,
            ExecutionVerdict = snapshot.ExecutionVerdict ?? string.Empty,
            ExecutionBlockReason = snapshot.ExecutionBlockReason ?? string.Empty,
            AutoTakeProfitEnabled = snapshot.AutoTakeProfitEnabled,
            TakeProfitPercent = snapshot.TakeProfitPercent,
            AutoStopLossEnabled = snapshot.AutoStopLossEnabled,
            StopLossPercent = snapshot.StopLossPercent,
            AutoTrailingStopEnabled = snapshot.AutoTrailingStopEnabled,
            TrailingStopPercent = snapshot.TrailingStopPercent,
            PartialTakeProfitEnabled = snapshot.PartialTakeProfitEnabled,
            PartialTakeProfitTriggerPercent = snapshot.PartialTakeProfitTriggerPercent,
            PartialTakeProfitSellPercent = snapshot.PartialTakeProfitSellPercent,
            PartialTakeProfitExecuted = snapshot.PartialTakeProfitExecuted,
            BreakEvenEnabled = snapshot.BreakEvenEnabled,
            BreakEvenTriggerPercent = snapshot.BreakEvenTriggerPercent,
            BreakEvenArmed = snapshot.BreakEvenArmed,
            BreakEvenTriggered = snapshot.BreakEvenTriggered,
            PositionSizePercent = snapshot.PositionSizePercent,
            PeakPriceUsd = snapshot.PeakPriceUsd,
            TrackedTokenAmount = snapshot.TrackedTokenAmount,
            UsesLiveAccounting = snapshot.UsesLiveAccounting,
            LiveEntryCostNative = snapshot.LiveEntryCostNative,
            LiveRealizedProceedsNative = snapshot.LiveRealizedProceedsNative,
            LiveEntryTokenAmount = snapshot.LiveEntryTokenAmount,
            EntryTxHash = snapshot.EntryTxHash ?? string.Empty,
            LastExitTxHash = snapshot.LastExitTxHash ?? string.Empty,
            EntryDexId = snapshot.EntryDexId ?? string.Empty,
            TakeProfitTriggered = snapshot.TakeProfitTriggered,
            StopLossTriggered = snapshot.StopLossTriggered,
            TrailingStopTriggered = snapshot.TrailingStopTriggered
        };
        return candidate;
    }

    private sealed class SniperOpenPositionSnapshot
    {
        public DexTokenInfo? TokenInfo { get; set; }
        public string? Reason { get; set; }
        public bool PassedFilters { get; set; }
        public string? Status { get; set; }
        public DateTime? OpenedAtLocal { get; set; }
        public decimal EntryAmountBnb { get; set; }
        public decimal EntryPriceUsd { get; set; }
        public int RiskScore { get; set; }
        public string? RiskBand { get; set; }
        public string? RiskSummary { get; set; }
        public string? RiskFlags { get; set; }
        public bool IsExecutionBlocked { get; set; }
        public string? ExecutionVerdict { get; set; }
        public string? ExecutionBlockReason { get; set; }
        public bool AutoTakeProfitEnabled { get; set; }
        public decimal TakeProfitPercent { get; set; }
        public bool AutoStopLossEnabled { get; set; }
        public decimal StopLossPercent { get; set; }
        public bool AutoTrailingStopEnabled { get; set; }
        public decimal TrailingStopPercent { get; set; }
        public bool PartialTakeProfitEnabled { get; set; }
        public decimal PartialTakeProfitTriggerPercent { get; set; }
        public decimal PartialTakeProfitSellPercent { get; set; }
        public bool PartialTakeProfitExecuted { get; set; }
        public bool BreakEvenEnabled { get; set; }
        public decimal BreakEvenTriggerPercent { get; set; }
        public bool BreakEvenArmed { get; set; }
        public bool BreakEvenTriggered { get; set; }
        public decimal PositionSizePercent { get; set; }
        public decimal PeakPriceUsd { get; set; }
        public decimal TrackedTokenAmount { get; set; }
        public bool UsesLiveAccounting { get; set; }
        public decimal LiveEntryCostNative { get; set; }
        public decimal LiveRealizedProceedsNative { get; set; }
        public decimal LiveEntryTokenAmount { get; set; }
        public string? EntryTxHash { get; set; }
        public string? LastExitTxHash { get; set; }
        public string? EntryDexId { get; set; }
        public bool TakeProfitTriggered { get; set; }
        public bool StopLossTriggered { get; set; }
        public bool TrailingStopTriggered { get; set; }
    }
}
