using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class SniperExecutionAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<SniperExecutionAuditRecordViewModel> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return AtomicJsonFile.Read<List<SniperExecutionAuditRecordViewModel>>(path) ?? [];
    }

    public void Save(string path, IEnumerable<SniperExecutionAuditRecordViewModel> records)
    {
        AtomicJsonFile.Write(path, records.ToList(), JsonOptions);
    }

    public SniperExecutionAuditRecordViewModel CreateEntryRecord(
        SniperCandidateViewModel candidate,
        DexBuyExecutionResult result,
        string entryReason,
        decimal tinyDryRunCapNative)
    {
        return new SniperExecutionAuditRecordViewModel
        {
            LoggedAtLocal = DateTime.Now,
            EventType = "buy-filled",
            IsTinyDryRun = (result.SpendAssetAmount > 0m ? result.SpendAssetAmount : result.NativeAmountSpent) > 0m &&
                           (result.SpendAssetAmount > 0m ? result.SpendAssetAmount : result.NativeAmountSpent) <= tinyDryRunCapNative,
            ChainId = candidate.TokenInfo.ChainId,
            DexId = result.DexId ?? candidate.TokenInfo.DexId,
            DisplayName = candidate.DisplayName,
            TokenAddress = candidate.TokenInfo.TokenAddress,
            TxHash = result.TransactionHash,
            InputNativeAmount = result.SpendAssetAmount > 0m ? result.SpendAssetAmount : result.NativeAmountSpent,
            OutputTokenAmount = result.ActualTokenAmountReceived,
            EntryPriceUsd = candidate.EntryPriceUsd,
            NetworkFeeNative = result.NetworkFeeNative,
            GasUsed = result.GasUsed,
            EntryReason = entryReason,
            Notes = result.Narrative,
            ReceiptParsed = result.ReceiptParsed,
            DecimalsVerified = result.DecimalsVerified,
            SlippageProtected = result.SlippageProtected,
            BalanceSynchronized = result.BalanceSynchronized
        };
    }

    public SniperExecutionAuditRecordViewModel CreateExitRecord(
        SniperCandidateViewModel candidate,
        SniperLiveExitExecutionResult result,
        string exitReason,
        decimal tinyDryRunCapNative)
    {
        var sell = result.SellExecution;
        return new SniperExecutionAuditRecordViewModel
        {
            LoggedAtLocal = DateTime.Now,
            EventType = result.Success && candidate.TrackedTokenAmount <= SniperLiveExecutionService.TokenDustThreshold
                ? "full-sell"
                : "partial-sell",
            IsTinyDryRun = candidate.LiveEntryCostNative > 0m && candidate.LiveEntryCostNative <= tinyDryRunCapNative,
            ChainId = candidate.TokenInfo.ChainId,
            DexId = sell?.DexId ?? candidate.TokenInfo.DexId,
            DisplayName = candidate.DisplayName,
            TokenAddress = candidate.TokenInfo.TokenAddress,
            TxHash = result.TransactionHash,
            ApproveTxHash = sell?.ApproveTransactionHash ?? string.Empty,
            InputTokenAmount = sell?.RequestedTokenAmount ?? result.SoldTokenAmount,
            OutputNativeAmount = result.RealizedNativeDelta,
            OutputTokenAmount = result.SoldTokenAmount,
            EntryPriceUsd = candidate.EntryPriceUsd,
            ExitPriceUsd = candidate.CurrentPriceUsd,
            NetworkFeeNative = sell?.NetworkFeeNative ?? 0m,
            ApproveFeeNative = sell?.ApproveFeeNative ?? 0m,
            GasUsed = sell?.GasUsed,
            EntryReason = candidate.Reason,
            ExitReason = exitReason,
            Notes = sell?.Narrative ?? result.FailureReason ?? string.Empty,
            ReceiptParsed = sell?.ReceiptParsed ?? false,
            DecimalsVerified = sell?.DecimalsVerified ?? false,
            SlippageProtected = sell?.SlippageProtected ?? false,
            BalanceSynchronized = sell?.BalanceSynchronized ?? false,
            ApproveWasRequired = sell?.ApproveWasRequired ?? false
        };
    }

    public SniperExecutionAuditRecordViewModel CreateRecoveryRecord(
        SniperCandidateViewModel? candidate,
        string eventType,
        string notes,
        bool rpcRecoveryObserved)
    {
        return new SniperExecutionAuditRecordViewModel
        {
            LoggedAtLocal = DateTime.Now,
            EventType = eventType,
            IsTinyDryRun = false,
            ChainId = candidate?.TokenInfo.ChainId ?? string.Empty,
            DexId = candidate?.TokenInfo.DexId ?? string.Empty,
            DisplayName = candidate?.DisplayName ?? "Sniper session",
            TokenAddress = candidate?.TokenInfo.TokenAddress ?? string.Empty,
            EntryReason = candidate?.Reason ?? string.Empty,
            Notes = notes,
            RpcRecoveryObserved = rpcRecoveryObserved,
            PositionRestored = eventType.Equals("position-restored", StringComparison.OrdinalIgnoreCase)
        };
    }
}
