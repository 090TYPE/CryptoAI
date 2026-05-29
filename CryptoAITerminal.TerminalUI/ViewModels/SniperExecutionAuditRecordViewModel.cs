using System;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class SniperExecutionAuditRecordViewModel
{
    public DateTime LoggedAtLocal { get; set; }
    public string EventType { get; set; } = string.Empty;
    public bool IsTinyDryRun { get; set; }
    public string ChainId { get; set; } = string.Empty;
    public string DexId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TokenAddress { get; set; } = string.Empty;
    public string TxHash { get; set; } = string.Empty;
    public string ApproveTxHash { get; set; } = string.Empty;
    public decimal InputNativeAmount { get; set; }
    public decimal OutputNativeAmount { get; set; }
    public decimal InputTokenAmount { get; set; }
    public decimal OutputTokenAmount { get; set; }
    public decimal EntryPriceUsd { get; set; }
    public decimal ExitPriceUsd { get; set; }
    public decimal NetworkFeeNative { get; set; }
    public decimal ApproveFeeNative { get; set; }
    public long? GasUsed { get; set; }
    public string EntryReason { get; set; } = string.Empty;
    public string ExitReason { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool ReceiptParsed { get; set; }
    public bool DecimalsVerified { get; set; }
    public bool SlippageProtected { get; set; }
    public bool BalanceSynchronized { get; set; }
    public bool ApproveWasRequired { get; set; }
    public bool PositionRestored { get; set; }
    public bool RpcRecoveryObserved { get; set; }

    public string AuditTitle => $"{EventType} | {DisplayName}";
    public string MetaLine =>
        $"{ChainId.ToUpperInvariant()} / {DexId} | {(IsTinyDryRun ? "tiny dry-run" : "live")} | {LoggedAtLocal:dd.MM HH:mm:ss}";
    public string TxLine =>
        string.IsNullOrWhiteSpace(TxHash)
            ? "No tx hash"
            : $"Tx {TxHash[..Math.Min(12, TxHash.Length)]}...";
}
