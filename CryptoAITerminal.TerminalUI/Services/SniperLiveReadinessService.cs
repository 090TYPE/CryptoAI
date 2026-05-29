using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class SniperLiveReadinessService
{
    private static readonly IReadOnlyList<(string ChainId, string DexId)> Targets =
    [
        ("bsc", "pancakeswap"),
        ("ethereum", "uniswap"),
        ("base", "aerodrome"),
        ("solana", "jupiter")
    ];

    public IReadOnlyList<SniperLiveReadyStatusViewModel> BuildStatuses(IEnumerable<SniperExecutionAuditRecordViewModel> auditTrail)
    {
        var audits = auditTrail.ToList();
        return Targets
            .Select(target => BuildStatus(audits, target.ChainId, target.DexId))
            .ToList();
    }

    private static SniperLiveReadyStatusViewModel BuildStatus(
        IReadOnlyList<SniperExecutionAuditRecordViewModel> audits,
        string chainId,
        string dexId)
    {
        var chainAudits = audits
            .Where(audit => string.Equals(audit.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var dexAudits = chainAudits
            .Where(audit => string.Equals(audit.DexId, dexId, StringComparison.OrdinalIgnoreCase) ||
                            (string.Equals(chainId, "solana", StringComparison.OrdinalIgnoreCase) &&
                             audit.DexId.Contains("jupiter", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var tinyBuy = dexAudits.Any(audit => audit.EventType == "buy-filled" && audit.IsTinyDryRun);
        var partialTp = dexAudits.Any(audit => audit.EventType == "partial-sell" && audit.ExitReason.Contains("partial", StringComparison.OrdinalIgnoreCase) && audit.IsTinyDryRun);
        var fullSell = dexAudits.Any(audit => audit.EventType == "full-sell" && audit.IsTinyDryRun);
        var stopLoss = dexAudits.Any(audit => audit.ExitReason.Contains("stop-loss", StringComparison.OrdinalIgnoreCase) && audit.IsTinyDryRun);
        var trailingStop = dexAudits.Any(audit => audit.ExitReason.Contains("trailing-stop", StringComparison.OrdinalIgnoreCase) && audit.IsTinyDryRun);
        var receiptParsing = dexAudits.Any(static audit => audit.ReceiptParsed);
        var decimals = dexAudits.Any(static audit => audit.DecimalsVerified);
        var slippage = dexAudits.Any(static audit => audit.SlippageProtected);
        var balanceSync = dexAudits.Any(static audit => audit.BalanceSynchronized);
        var approvePath = chainId.Equals("solana", StringComparison.OrdinalIgnoreCase) || dexAudits.Any(static audit => audit.ApproveWasRequired);
        var rpcRecovery = chainAudits.Any(static audit => audit.RpcRecoveryObserved);
        var restartRestore = chainAudits.Any(static audit => audit.PositionRestored);

        var requiredChecks = new[]
        {
            tinyBuy,
            fullSell,
            receiptParsing,
            decimals,
            slippage,
            balanceSync,
            approvePath,
            rpcRecovery,
            restartRestore
        };
        var isReady = requiredChecks.All(static status => status);

        return new SniperLiveReadyStatusViewModel
        {
            NetworkLabel = chainId.ToUpperInvariant(),
            DexLabel = dexId,
            StatusLabel = isReady ? "live-ready evidence complete" : "evidence missing",
            Summary =
                $"tiny buy {(tinyBuy ? "ok" : "pending")} | partial TP {(partialTp ? "ok" : "pending")} | full sell {(fullSell ? "ok" : "pending")} | " +
                $"stop-loss {(stopLoss ? "ok" : "pending")} | trailing {(trailingStop ? "ok" : "pending")} | approve {(approvePath ? "ok" : "pending")} | " +
                $"receipt {(receiptParsing ? "ok" : "pending")} | decimals {(decimals ? "ok" : "pending")} | slippage {(slippage ? "ok" : "pending")} | " +
                $"balance sync {(balanceSync ? "ok" : "pending")} | RPC recovery {(rpcRecovery ? "ok" : "pending")} | restart restore {(restartRestore ? "ok" : "pending")}",
            IsReady = isReady
        };
    }
}
