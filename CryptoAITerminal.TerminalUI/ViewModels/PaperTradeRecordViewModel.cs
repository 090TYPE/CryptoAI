using System;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class PaperTradeRecordViewModel : ReactiveObject
{
    public string ExecutionMode { get; set; } = "Paper";
    public string ChainId { get; set; } = string.Empty;
    public string DexId { get; set; } = string.Empty;
    public string ExitReason { get; set; } = string.Empty;
    public string EntryReason { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TokenAddress { get; set; } = string.Empty;
    public string EntryTxHash { get; set; } = string.Empty;
    public string ExitTxHash { get; set; } = string.Empty;
    public DateTime OpenedAtLocal { get; set; }
    public DateTime ClosedAtLocal { get; set; }
    public decimal EntryAmountBnb { get; set; }
    public decimal EntryPriceUsd { get; set; }
    public decimal ExitPriceUsd { get; set; }
    public decimal PnlPercent { get; set; }
    public decimal EntryCostNative { get; set; }
    public decimal ExitProceedsNative { get; set; }
    public decimal NetPnlNative { get; set; }
    public decimal EntryTokenAmount { get; set; }
    public decimal ExitTokenAmount { get; set; }
    public decimal NetworkFeeNative { get; set; }
    public decimal ApproveFeeNative { get; set; }
    public long? GasUsed { get; set; }
    public string AccountingNote { get; set; } = string.Empty;
    public decimal RiskScore { get; set; }
    public string RiskBand { get; set; } = string.Empty;

    public string DurationLabel
    {
        get
        {
            var duration = ClosedAtLocal - OpenedAtLocal;
            if (duration.TotalHours >= 1)
            {
                return $"{duration.TotalHours:0.0}h";
            }

            if (duration.TotalMinutes >= 1)
            {
                return $"{duration.TotalMinutes:0}m";
            }

            return $"{Math.Max(0, duration.TotalSeconds):0}s";
        }
    }

    public string PnlLabel => $"{PnlPercent:+0.##;-0.##;0}%";

    public string ChainLabel =>
        string.IsNullOrWhiteSpace(ChainId)
            ? "--"
            : ChainId.ToUpperInvariant();

    public string MetaLabel
    {
        get
        {
            var chainSegment = string.IsNullOrWhiteSpace(ChainId)
                ? string.Empty
                : $" | {ChainLabel}";
            return string.IsNullOrWhiteSpace(ExitReason)
                ? $"{ExecutionMode}{chainSegment}"
                : $"{ExecutionMode}{chainSegment} | {ExitReason}";
        }
    }

    public string PnlAccentHex => PnlPercent switch
    {
        > 0m => "#3DDC84",
        < 0m => "#FF5D73",
        _ => "#8FA3B8"
    };
}
