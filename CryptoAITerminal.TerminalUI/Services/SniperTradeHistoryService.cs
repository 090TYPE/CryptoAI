using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class SniperTradeHistoryService
{
    private static readonly JsonSerializerOptions StorageJsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<PaperTradeRecordViewModel> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return AtomicJsonFile.Read<List<PaperTradeRecordViewModel>>(path) ?? [];
    }

    public void Save(string path, IEnumerable<PaperTradeRecordViewModel> records)
    {
        AtomicJsonFile.Write(path, records.ToList(), StorageJsonOptions);
    }

    public PaperTradeRecordViewModel? CreatePaperRecord(SniperCandidateViewModel candidate)
    {
        if (candidate.OpenedAtLocal is null || candidate.EntryPriceUsd <= 0m)
        {
            return null;
        }

        return new PaperTradeRecordViewModel
        {
            ExecutionMode = "Paper",
            ChainId = candidate.TokenInfo.ChainId,
            DexId = candidate.TokenInfo.DexId,
            ExitReason = candidate.Status,
            EntryReason = candidate.Reason,
            DisplayName = candidate.DisplayName,
            TokenAddress = candidate.TokenInfo.TokenAddress,
            OpenedAtLocal = candidate.OpenedAtLocal.Value,
            ClosedAtLocal = DateTime.Now,
            EntryAmountBnb = candidate.EntryAmountBnb,
            EntryPriceUsd = candidate.EntryPriceUsd,
            ExitPriceUsd = candidate.CurrentPriceUsd,
            PnlPercent = candidate.PaperPnlPercent,
            RiskScore = candidate.RiskScore,
            RiskBand = candidate.RiskBand
        };
    }

    public PaperTradeRecordViewModel? CreateLiveRecord(SniperCandidateViewModel candidate, string exitReason)
    {
        if (candidate.OpenedAtLocal is null || candidate.EntryPriceUsd <= 0m)
        {
            return null;
        }

        var exitedTokenAmount = Math.Max(0m, candidate.LiveEntryTokenAmount - candidate.TrackedTokenAmount);
        var netPnlNative = candidate.LiveRealizedProceedsNative - candidate.LiveEntryCostNative;

        return new PaperTradeRecordViewModel
        {
            ExecutionMode = "Live",
            ChainId = candidate.TokenInfo.ChainId,
            DexId = string.IsNullOrWhiteSpace(candidate.EntryDexId) ? candidate.TokenInfo.DexId : candidate.EntryDexId,
            ExitReason = exitReason,
            EntryReason = candidate.Reason,
            DisplayName = candidate.DisplayName,
            TokenAddress = candidate.TokenInfo.TokenAddress,
            EntryTxHash = candidate.EntryTxHash,
            ExitTxHash = candidate.LastExitTxHash,
            OpenedAtLocal = candidate.OpenedAtLocal.Value,
            ClosedAtLocal = DateTime.Now,
            EntryAmountBnb = candidate.EntryAmountBnb,
            EntryPriceUsd = candidate.EntryPriceUsd,
            ExitPriceUsd = candidate.CurrentPriceUsd,
            PnlPercent = candidate.ClosedLivePnlPercent,
            EntryCostNative = candidate.LiveEntryCostNative,
            ExitProceedsNative = candidate.LiveRealizedProceedsNative,
            NetPnlNative = netPnlNative,
            EntryTokenAmount = candidate.LiveEntryTokenAmount,
            ExitTokenAmount = exitedTokenAmount,
            AccountingNote = "Live PnL is based on realized wallet deltas, tracked token balances, and confirmed fills.",
            RiskScore = candidate.RiskScore,
            RiskBand = candidate.RiskBand
        };
    }
}
