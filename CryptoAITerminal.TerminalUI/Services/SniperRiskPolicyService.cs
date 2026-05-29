using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class SniperRiskPolicyService
{
    public SniperRiskSnapshot BuildSnapshot(
        IEnumerable<SniperCandidateViewModel> openLivePositions,
        IEnumerable<PaperTradeRecordViewModel> liveTradeHistory,
        DateTime nowLocal)
    {
        var livePositions = openLivePositions.ToList();
        var history = liveTradeHistory
            .OrderByDescending(static trade => trade.ClosedAtLocal)
            .ToList();

        var dailyLiveLossNative = history
            .Where(trade => trade.ClosedAtLocal.Date == nowLocal.Date && trade.NetPnlNative < 0m)
            .Sum(static trade => Math.Abs(trade.NetPnlNative));

        var exposureByChain = livePositions
            .GroupBy(position => NormalizeChainId(position.TokenInfo.ChainId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(GetOpenExposureNative),
                StringComparer.OrdinalIgnoreCase);

        var totalLiveExposureNative = exposureByChain.Values.Sum();
        var consecutiveLiveLosses = 0;
        foreach (var trade in history)
        {
            if (trade.NetPnlNative < 0m)
            {
                consecutiveLiveLosses++;
                continue;
            }

            break;
        }

        return new SniperRiskSnapshot(
            dailyLiveLossNative,
            totalLiveExposureNative,
            exposureByChain,
            consecutiveLiveLosses);
    }

    public SniperRiskDecision EvaluateEntry(
        bool isLiveMode,
        string? chainId,
        decimal proposedEntryNative,
        IEnumerable<SniperCandidateViewModel> openLivePositions,
        IEnumerable<SniperCandidateViewModel> openPaperPositions,
        IEnumerable<PaperTradeRecordViewModel> liveTradeHistory,
        int sessionBuyCount,
        DateTime? lastBuyUtc,
        DateTime nowLocal,
        DateTime nowUtc,
        SniperRiskLimits limits)
    {
        if (IsCooldownActive(lastBuyUtc, nowUtc, limits.CooldownSeconds, out var cooldownRemainingSeconds))
        {
            return new SniperRiskDecision(
                false,
                $"Cooling down - {cooldownRemainingSeconds}s remaining.",
                BuildSnapshot(openLivePositions, liveTradeHistory, nowLocal));
        }

        var openPositionCount = openLivePositions.Count() + openPaperPositions.Count();
        if (openPositionCount >= limits.MaxSimultaneousPositions)
        {
            return new SniperRiskDecision(
                false,
                $"Open position limit reached ({openPositionCount}/{limits.MaxSimultaneousPositions}).",
                BuildSnapshot(openLivePositions, liveTradeHistory, nowLocal));
        }

        if (sessionBuyCount >= limits.MaxBuysPerSession)
        {
            return new SniperRiskDecision(
                false,
                $"Session buy limit reached ({sessionBuyCount}/{limits.MaxBuysPerSession}).",
                BuildSnapshot(openLivePositions, liveTradeHistory, nowLocal));
        }

        var snapshot = BuildSnapshot(openLivePositions, liveTradeHistory, nowLocal);
        if (!isLiveMode)
        {
            return new SniperRiskDecision(true, "Safety gate passed.", snapshot);
        }

        if (limits.MaxConsecutiveLiveLosses > 0 &&
            snapshot.ConsecutiveLiveLosses >= limits.MaxConsecutiveLiveLosses)
        {
            return new SniperRiskDecision(
                false,
                $"Emergency stop active: {snapshot.ConsecutiveLiveLosses} consecutive live losses reached the cap {limits.MaxConsecutiveLiveLosses}.",
                snapshot);
        }

        if (limits.MaxDailyLiveLossNative > 0m &&
            snapshot.DailyLiveLossNative >= limits.MaxDailyLiveLossNative)
        {
            return new SniperRiskDecision(
                false,
                $"Daily live-loss cap reached ({snapshot.DailyLiveLossNative:0.####}/{limits.MaxDailyLiveLossNative:0.####} native).",
                snapshot);
        }

        var projectedWalletExposureNative = snapshot.TotalLiveExposureNative + Math.Max(0m, proposedEntryNative);
        if (limits.MaxExposurePerWalletNative > 0m &&
            projectedWalletExposureNative > limits.MaxExposurePerWalletNative)
        {
            return new SniperRiskDecision(
                false,
                $"Wallet exposure cap would be exceeded ({projectedWalletExposureNative:0.####}/{limits.MaxExposurePerWalletNative:0.####} native).",
                snapshot);
        }

        if (limits.HardCapTotalLiveExposureNative > 0m &&
            projectedWalletExposureNative > limits.HardCapTotalLiveExposureNative)
        {
            return new SniperRiskDecision(
                false,
                $"Hard live-exposure cap would be exceeded ({projectedWalletExposureNative:0.####}/{limits.HardCapTotalLiveExposureNative:0.####} native).",
                snapshot);
        }

        if (limits.MaxExposurePerChainNative > 0m)
        {
            var projectedChainExposureNative = snapshot.GetChainExposure(chainId) + Math.Max(0m, proposedEntryNative);
            if (projectedChainExposureNative > limits.MaxExposurePerChainNative)
            {
                return new SniperRiskDecision(
                    false,
                    $"Chain exposure cap would be exceeded on {NormalizeChainId(chainId)} ({projectedChainExposureNative:0.####}/{limits.MaxExposurePerChainNative:0.####} native).",
                    snapshot);
            }
        }

        return new SniperRiskDecision(true, "Safety gate passed.", snapshot);
    }

    public bool IsEmergencyStopActive(SniperRiskSnapshot snapshot, SniperRiskLimits limits)
    {
        return limits.MaxConsecutiveLiveLosses > 0 &&
               snapshot.ConsecutiveLiveLosses >= limits.MaxConsecutiveLiveLosses;
    }

    private static bool IsCooldownActive(
        DateTime? lastBuyUtc,
        DateTime nowUtc,
        int cooldownSeconds,
        out int cooldownRemainingSeconds)
    {
        cooldownRemainingSeconds = 0;
        if (lastBuyUtc is null || cooldownSeconds <= 0)
        {
            return false;
        }

        var elapsed = nowUtc - lastBuyUtc.Value;
        cooldownRemainingSeconds = Math.Max(0, cooldownSeconds - (int)Math.Floor(elapsed.TotalSeconds));
        return cooldownRemainingSeconds > 0;
    }

    private static decimal GetOpenExposureNative(SniperCandidateViewModel position)
    {
        if (!position.UsesLiveAccounting)
        {
            return Math.Max(0m, position.EntryAmountBnb);
        }

        return Math.Max(0m, position.LiveEntryCostNative - position.LiveRealizedProceedsNative);
    }

    private static string NormalizeChainId(string? chainId)
    {
        return string.IsNullOrWhiteSpace(chainId)
            ? "unknown"
            : chainId.Trim().ToLowerInvariant();
    }
}

public sealed record SniperRiskLimits(
    int CooldownSeconds,
    int MaxSimultaneousPositions,
    int MaxBuysPerSession,
    decimal MaxDailyLiveLossNative,
    decimal MaxExposurePerChainNative,
    decimal MaxExposurePerWalletNative,
    int MaxConsecutiveLiveLosses,
    decimal HardCapTotalLiveExposureNative);

public sealed record SniperRiskSnapshot(
    decimal DailyLiveLossNative,
    decimal TotalLiveExposureNative,
    IReadOnlyDictionary<string, decimal> ExposureByChain,
    int ConsecutiveLiveLosses)
{
    public decimal GetChainExposure(string? chainId)
    {
        var normalized = string.IsNullOrWhiteSpace(chainId)
            ? "unknown"
            : chainId.Trim().ToLowerInvariant();
        return ExposureByChain.TryGetValue(normalized, out var exposure)
            ? exposure
            : 0m;
    }
}

public sealed record SniperRiskDecision(
    bool CanEnter,
    string Reason,
    SniperRiskSnapshot Snapshot);
