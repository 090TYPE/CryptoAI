using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public partial class SniperViewModel
{
    private SniperRiskLimits BuildRiskLimits()
    {
        return new SniperRiskLimits(
            CooldownSeconds,
            MaxSimultaneousPositions,
            MaxBuysPerSession,
            MaxDailyLiveLossNative,
            MaxExposurePerChainNative,
            MaxExposurePerWalletNative,
            MaxConsecutiveLiveLosses,
            HardCapTotalLiveExposureNative);
    }

    private SniperRiskSnapshot BuildRiskSnapshot()
    {
        return _riskPolicyService.BuildSnapshot(OpenPositions, LiveTradeHistory, DateTime.Now);
    }

    private void ApplyEmergencyRiskStopIfNeeded(bool pushLogEntry)
    {
        var snapshot = BuildRiskSnapshot();
        if (!_riskPolicyService.IsEmergencyStopActive(snapshot, BuildRiskLimits()))
        {
            return;
        }

        if (AutoBuyEnabled)
        {
            AutoBuyEnabled = false;
        }

        StatusMessage = $"Emergency risk stop is active after {snapshot.ConsecutiveLiveLosses} consecutive live losses. Review the live journal before re-arming.";
        if (pushLogEntry)
        {
            PushLog(StatusMessage, false);
        }

        RaiseSafetyProperties();
    }

    private string GetRiskNativeSymbol()
    {
        return _walletWorkspace.ActiveDexGateway?.NativeSymbol ?? "native";
    }

    private string FormatRiskAmount(decimal amount)
    {
        return $"{amount:0.####} {GetRiskNativeSymbol()}";
    }

    private string FormatRiskLimit(decimal limit)
    {
        return limit > 0m
            ? $"{limit:0.####} {GetRiskNativeSymbol()}"
            : "off";
    }

    private static string FormatRiskCountLimit(int limit)
    {
        return limit > 0 ? limit.ToString() : "off";
    }

    private static string FormatPaperTradeStat(PaperTradeRecordViewModel? trade)
    {
        if (trade is null)
        {
            return "--";
        }

        return $"{trade.DisplayName} {trade.PnlLabel}";
    }

    private static List<string> ParseFragments(string source)
    {
        return source
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.ToLowerInvariant())
            .ToList();
    }

    private HashSet<string> GetEnabledChainIds()
    {
        var configured = ParseFragments(EnabledChainsText)
            .Where(chain => ChainProfiles.ContainsKey(chain))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return configured.Count > 0
            ? configured
            : ChainProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<SniperChainProfile> GetEnabledChainProfiles()
    {
        return GetEnabledChainIds()
            .Select(GetChainProfile)
            .ToList();
    }

    private static SniperChainProfile GetChainProfile(string? chainId)
    {
        if (!string.IsNullOrWhiteSpace(chainId) && ChainProfiles.TryGetValue(chainId, out var profile))
        {
            return profile;
        }

        return ChainProfiles["bsc"];
    }

    private StrategyDecision EvaluateStrategy(DexTokenInfo token)
    {
        var ageMinutes = GetObservedAgeMinutes(token);
        return SelectedStrategyMode switch
        {
            "Launch Sniper" => ageMinutes <= LaunchMaxPairAgeMinutes
                ? new StrategyDecision(true, "Launch Sniper", $"Launch profile: age {ageMinutes:0.#}m within launch window <= {LaunchMaxPairAgeMinutes:0.#}m.", string.Empty, 20)
                : new StrategyDecision(false, "Launch Sniper", "Launch profile missed.", $"Launch strategy requires pair age <= {LaunchMaxPairAgeMinutes:0.#}m, current age {ageMinutes:0.#}m.", 0),
            "Momentum Continuation" => ageMinutes >= WarmPairMinAgeMinutes && token.PriceChange5m >= Math.Max(1m, MinMomentum5m) && token.PriceChange1h > 5m
                ? new StrategyDecision(true, "Momentum Continuation", $"Momentum profile: age {ageMinutes:0.#}m, 5m {token.PriceChange5m:0.##}%, 1h {token.PriceChange1h:0.##}%.", string.Empty, 16)
                : new StrategyDecision(false, "Momentum Continuation", "Momentum profile not confirmed.", $"Momentum strategy needs age >= {WarmPairMinAgeMinutes:0.#}m, 5m >= {Math.Max(1m, MinMomentum5m):0.##}%, 1h > 5%.", 0),
            "Reversal / Reclaim" => ageMinutes >= WarmPairMinAgeMinutes && token.PriceChange1h >= 8m && token.PriceChange5m is >= -15m and <= 4m
                ? new StrategyDecision(true, "Reversal / Reclaim", $"Reversal profile: age {ageMinutes:0.#}m, 5m {token.PriceChange5m:0.##}%, 1h {token.PriceChange1h:0.##}%.", string.Empty, 14)
                : new StrategyDecision(false, "Reversal / Reclaim", "Reversal profile not confirmed.", $"Reversal strategy needs age >= {WarmPairMinAgeMinutes:0.#}m, 1h >= 8%, and 5m between -15% and +4%.", 0),
            _ => DetectMixedStrategy(token, ageMinutes)
        };
    }

    private StrategyDecision DetectMixedStrategy(DexTokenInfo token, decimal ageMinutes)
    {
        if (ageMinutes <= LaunchMaxPairAgeMinutes)
        {
            return new StrategyDecision(true, "Launch Sniper", $"Mixed mode favored launch profile at {ageMinutes:0.#}m age.", string.Empty, 18);
        }

        if (ageMinutes >= WarmPairMinAgeMinutes && token.PriceChange5m >= Math.Max(1m, MinMomentum5m) && token.PriceChange1h > 5m)
        {
            return new StrategyDecision(true, "Momentum Continuation", $"Mixed mode favored continuation profile: 5m {token.PriceChange5m:0.##}%, 1h {token.PriceChange1h:0.##}%.", string.Empty, 14);
        }

        if (ageMinutes >= WarmPairMinAgeMinutes && token.PriceChange1h >= 8m && token.PriceChange5m is >= -15m and <= 4m)
        {
            return new StrategyDecision(true, "Reversal / Reclaim", $"Mixed mode favored reclaim profile with controlled 5m pullback.", string.Empty, 12);
        }

        return new StrategyDecision(true, "Mixed", "Mixed mode kept the pair eligible without a hard strategy block.", string.Empty, 8);
    }

    private bool PassesMomentumGate(DexTokenInfo token, out string reason)
    {
        switch (SelectedStrategyMode)
        {
            case "Launch Sniper":
                if (token.PriceChange5m < -8m)
                {
                    reason = $"Launch momentum gate rejected the pair: 5m momentum {token.PriceChange5m:N2}% is below -8%.";
                    return false;
                }

                reason = string.Empty;
                return true;
            case "Reversal / Reclaim":
                if (token.PriceChange5m < -20m || token.PriceChange5m > 8m)
                {
                    reason = $"Reversal momentum gate rejected the pair: 5m momentum {token.PriceChange5m:N2}% is outside the reclaim band.";
                    return false;
                }

                reason = string.Empty;
                return true;
            default:
                if (token.PriceChange5m < MinMomentum5m)
                {
                    reason = $"5m momentum {token.PriceChange5m:N2}% is below {MinMomentum5m:N2}%.";
                    return false;
                }

                reason = string.Empty;
                return true;
        }
    }

    private decimal GetObservedAgeMinutes(DexTokenInfo token)
    {
        if (token.ObservedFirstSeenUtc == DateTime.MinValue)
        {
            return 0m;
        }

        return Math.Max(0m, (decimal)(DateTime.UtcNow - token.ObservedFirstSeenUtc).TotalMinutes);
    }

    private bool IsVeryFresh(DexTokenInfo token)
    {
        return GetObservedAgeMinutes(token) <= Math.Max(1m, LaunchMaxPairAgeMinutes);
    }

    private static string BuildObservedAgeLabel(decimal ageMinutes)
    {
        return ageMinutes <= 0m ? "Age: new in this session" : $"Age: {ageMinutes:0.#}m observed";
    }

    private static int GetDexQualityScore(string? dexId)
    {
        if (!string.IsNullOrWhiteSpace(dexId) && DexQualityScores.TryGetValue(dexId.Trim(), out var score))
        {
            return score;
        }

        return 40;
    }

    private static string BuildDexQualityLabel(int score, string? dexId)
    {
        var tier = score switch
        {
            >= 85 => "high-quality venue",
            >= 70 => "established venue",
            >= 55 => "emerging venue",
            _ => "weak venue"
        };

        return $"DEX {score}/100 - {tier}{(string.IsNullOrWhiteSpace(dexId) ? string.Empty : $" ({dexId})")}";
    }

    private static string? GetMatchingFragment(DexTokenInfo token, IReadOnlyList<string> fragments)
    {
        if (fragments.Count == 0)
        {
            return null;
        }

        var haystack = $"{token.Symbol} {token.Name}".ToLowerInvariant();
        return fragments.FirstOrDefault(word => haystack.Contains(word, StringComparison.Ordinal));
    }

    private int StrategyPriority(DexTokenInfo token)
    {
        return EvaluateStrategy(token).PriorityBonus + (token.WatchlistMatched ? 15 : 0);
    }

    private void UpdateLatestRisk(SniperCandidateViewModel candidate)
    {
        LatestRiskVerdictTitle = $"{candidate.DisplayName} - {candidate.RiskBand}";
        LatestRiskNarrative = candidate.RiskSummary;
        LatestRiskFlags = candidate.RiskFlags;
        LatestRiskAccentHex = candidate.RiskAccentHex;
        LatestRiskScoreLabel = candidate.RiskBadgeText;
    }

    private void UpdateLatestStructure(RiskEvaluation risk)
    {
        LatestStructureVerdict = risk.StructureBlocked
            ? "Structure guard blocked this pair"
            : "Structure guard passed";
        LatestStructureNarrative = risk.StructureSummary;
        LatestStructureAccentHex = risk.StructureBlocked ? "#FF6B6B" : "#1FE0B3";
    }

    private void UpdateLatestExecution(SniperCandidateViewModel candidate)
    {
        LatestExecutionVerdict = candidate.ExecutionVerdict;
        LatestExecutionReason = candidate.ExecutionBlockReason;
        LatestExecutionAccentHex = candidate.ExecutionAccentHex;
    }

    private void ApplyExecutionGuard(SniperCandidateViewModel candidate, RiskEvaluation risk)
    {
        var execution = EvaluateExecution(candidate.TokenInfo, risk);
        candidate.SimulatedBuyTaxPercent = execution.BuyTaxPercent;
        candidate.SimulatedSellTaxPercent = execution.SellTaxPercent;
        candidate.IsSuspectedHoneypot = execution.SuspectedHoneypot;
        candidate.IsExecutionBlocked = execution.Blocked;
        candidate.ExecutionVerdict = execution.Blocked
            ? $"Execution blocked - {candidate.DisplayName}"
            : $"Execution clear - {candidate.DisplayName}";
        candidate.ExecutionBlockReason = execution.Reason;

        if (execution.Blocked)
        {
            candidate.Status = "Blocked by execution guard";
        }
    }

    private ExecutionEvaluation EvaluateExecution(DexTokenInfo token, RiskEvaluation risk)
    {
        if (IsCexToken(token))
        {
            if (!EnableExecutionGuard)
            {
                return new ExecutionEvaluation(false, false, 0m, 0m, "Execution guard is disabled for this session.");
            }

            if (risk.StructureBlocked)
            {
                return new ExecutionEvaluation(true, false, 0m, 0m, $"Blocked: {risk.StructureSummary}");
            }

            return new ExecutionEvaluation(false, false, 0m, 0m, "Execution guard passed. CEX venue uses centralized order flow without DEX tax or honeypot heuristics.");
        }

        var buyTax = 2m;
        var sellTax = 2m;

        if (risk.Score >= 30)
        {
            buyTax += 2m;
            sellTax += 2m;
        }

        if (token.PriceChange5m > 35m)
        {
            buyTax += 1.5m;
            sellTax += 2.5m;
        }

        if (token.PriceChange1h > 120m)
        {
            sellTax += 3m;
        }

        if (token.LiquidityUsd < 40000m)
        {
            sellTax += 2m;
        }

        if (token.Volume24h > 0m && token.LiquidityUsd > 0m)
        {
            var ratio = token.Volume24h / token.LiquidityUsd;
            if (ratio > 12m)
            {
                buyTax += 1.5m;
                sellTax += 2.5m;
            }
        }

        var identity = $"{token.Symbol} {token.Name}".ToLowerInvariant();
        if (identity.Contains("tax", StringComparison.Ordinal) ||
            identity.Contains("fee", StringComparison.Ordinal))
        {
            buyTax += 2m;
            sellTax += 3m;
        }

        var suspectedHoneypot =
            risk.Score >= 70 ||
            (token.LiquidityUsd > 0m && token.MarketCap > 0m && (token.MarketCap / token.LiquidityUsd) > 45m) ||
            (token.PriceChange5m > 60m && token.PriceChange1h > 180m) ||
            sellTax >= 18m;

        buyTax = Math.Min(30m, buyTax);
        sellTax = Math.Min(35m, sellTax);

        if (!EnableExecutionGuard)
        {
            return new ExecutionEvaluation(false, suspectedHoneypot, buyTax, sellTax, "Execution guard is disabled for this session.");
        }

        if (BlockSuspectedHoneypots && suspectedHoneypot)
        {
            return new ExecutionEvaluation(true, true, buyTax, sellTax, "Blocked: token matches local honeypot heuristics.");
        }

        if (risk.StructureBlocked)
        {
            return new ExecutionEvaluation(true, suspectedHoneypot, buyTax, sellTax, $"Blocked: {risk.StructureSummary}");
        }

        if (buyTax > MaxSimulatedBuyTaxPercent)
        {
            return new ExecutionEvaluation(true, suspectedHoneypot, buyTax, sellTax, $"Blocked: simulated buy tax {buyTax:0.#}% exceeds cap {MaxSimulatedBuyTaxPercent:0.#}%.");
        }

        if (sellTax > MaxSimulatedSellTaxPercent)
        {
            return new ExecutionEvaluation(true, suspectedHoneypot, buyTax, sellTax, $"Blocked: simulated sell tax {sellTax:0.#}% exceeds cap {MaxSimulatedSellTaxPercent:0.#}%.");
        }

        return new ExecutionEvaluation(false, suspectedHoneypot, buyTax, sellTax, "Execution guard passed. No local honeypot or tax block was triggered.");
    }

    private RiskEvaluation EvaluateRisk(DexTokenInfo token)
    {
        if (IsCexToken(token))
        {
            return EvaluateCexRisk(token);
        }

        var score = 0;
        var flags = new List<string>();
        var severeStructureFlags = new List<string>();

        var volumeToLiquidityRatio = token.LiquidityUsd > 0 ? token.Volume24h / token.LiquidityUsd : 0m;
        var marketCapToLiquidityRatio = token.LiquidityUsd > 0 ? token.MarketCap / token.LiquidityUsd : 0m;
        var symbolLength = token.Symbol?.Length ?? 0;
        var nameLength = token.Name?.Length ?? 0;
        var lowerIdentity = $"{token.Symbol} {token.Name}".ToLowerInvariant();
        var pairAddressLength = token.PairAddress?.Length ?? 0;
        var tokenAddressLength = token.TokenAddress?.Length ?? 0;
        var dexTrusted = TrustedDexIds.Contains(token.DexId);
        var dexQualityScore = GetDexQualityScore(token.DexId);
        var dexQualityLabel = BuildDexQualityLabel(dexQualityScore, token.DexId);
        var observedAgeMinutes = GetObservedAgeMinutes(token);
        var observedAgeLabel = BuildObservedAgeLabel(observedAgeMinutes);
        var strategyDecision = EvaluateStrategy(token);
        var watchlistLabel = token.WatchlistMatched
            ? token.WatchlistMatchText
            : "Watchlist priority: no fragment match.";
        var ownershipSignalLabel = token.OwnershipSignalStatus;
        var marketCapMissing = token.MarketCap <= 0m;
        var symbolHasDigits = (token.Symbol ?? string.Empty).Any(char.IsDigit);
        var fragmentedName = (token.Name ?? string.Empty).Count(static ch => ch is '-' or '_' or '.' or '/' or '\\') >= 3;
        var extremeLiquidityToMcap = token.MarketCap > 0m && token.LiquidityUsd > 0m && (token.LiquidityUsd / token.MarketCap) > 0.85m;

        if (token.LiquidityUsd < 40000m)
        {
            score += 18;
            flags.Add("thin liquidity");
        }
        else if (token.LiquidityUsd < 80000m)
        {
            score += 8;
            flags.Add("medium-thin liquidity");
        }

        if (volumeToLiquidityRatio > MaxVolumeToLiquidityRatio && MaxVolumeToLiquidityRatio > 0)
        {
            score += 18;
            flags.Add($"volume/liquidity x{volumeToLiquidityRatio:N1}");
        }
        else if (volumeToLiquidityRatio > 10m)
        {
            score += 9;
            flags.Add($"heated volume/liquidity x{volumeToLiquidityRatio:N1}");
        }

        if (marketCapToLiquidityRatio > MaxMarketCapToLiquidityRatio && MaxMarketCapToLiquidityRatio > 0)
        {
            score += 18;
            flags.Add($"marketcap/liquidity x{marketCapToLiquidityRatio:N1}");
        }
        else if (marketCapToLiquidityRatio > 14m)
        {
            score += 9;
            flags.Add($"stretched mcap/liquidity x{marketCapToLiquidityRatio:N1}");
        }

        if (token.PriceChange5m > 45m)
        {
            score += 10;
            flags.Add("sharp 5m pump");
        }

        if (token.PriceChange1h > 160m)
        {
            score += 12;
            flags.Add("explosive 1h pump");
        }

        if (token.PriceChange24h > 600m)
        {
            score += 12;
            flags.Add("parabolic 24h move");
        }

        if (symbolLength <= 2 || nameLength <= 3)
        {
            score += 8;
            flags.Add("low-context ticker");
        }

        if (symbolLength > 12 || nameLength > 28)
        {
            score += 6;
            flags.Add("odd token naming");
        }

        var suspiciousFragments = new[]
        {
            "safe", "moon", "100x", "pump", "gem", "vip", "rich", "cash", "free", "rocket"
        };
        var fragmentHits = suspiciousFragments.Count(fragment => lowerIdentity.Contains(fragment, StringComparison.Ordinal));
        if (fragmentHits > 0)
        {
            score += Math.Min(16, fragmentHits * 4);
            flags.Add("promo-style branding");
        }

        if (string.IsNullOrWhiteSpace(token.Url) || string.IsNullOrWhiteSpace(token.PairAddress))
        {
            score += 8;
            flags.Add("weak metadata");
        }

        if (pairAddressLength < 20)
        {
            score += 16;
            flags.Add("pair address missing/short");
            severeStructureFlags.Add("pair structure incomplete");
        }

        if (tokenAddressLength < 20)
        {
            score += 18;
            flags.Add("token address missing/short");
            severeStructureFlags.Add("token address missing");
        }

        if (!dexTrusted)
        {
            score += dexQualityScore < 55 ? 14 : 8;
            flags.Add(dexQualityScore < 55 ? "weak dex venue" : "emerging dex venue");
        }

        if (dexQualityScore < 40)
        {
            score += 12;
            flags.Add("low dex quality score");
        }
        else if (dexQualityScore < 70)
        {
            score += 6;
            flags.Add("mid-tier dex quality");
        }

        if (marketCapMissing)
        {
            score += 10;
            flags.Add("market cap unavailable");
        }

        if (extremeLiquidityToMcap)
        {
            score += 12;
            flags.Add("liquidity unusually close to market cap");
        }

        if (symbolHasDigits)
        {
            score += 6;
            flags.Add("digit-heavy symbol");
        }

        if (fragmentedName)
        {
            score += 8;
            flags.Add("fragmented token naming");
        }

        if (token.PriceUsd <= 0m || token.PriceNative <= 0m)
        {
            score += 18;
            flags.Add("price feed incomplete");
            severeStructureFlags.Add("price feed incomplete");
        }

        if (observedAgeMinutes > 0m && observedAgeMinutes <= LaunchMaxPairAgeMinutes)
        {
            flags.Add($"very fresh pair {observedAgeMinutes:0.#}m");
        }
        else if (observedAgeMinutes >= WarmPairMinAgeMinutes)
        {
            flags.Add($"warmed pair {observedAgeMinutes:0.#}m");
        }

        if (token.WatchlistMatched)
        {
            score = Math.Max(0, score - 6);
            flags.Add("watchlist priority");
        }

        score = Math.Min(100, score);

        var band = score switch
        {
            <= 20 => "Low",
            <= 45 => "Guarded",
            <= 70 => "High",
            _ => "Extreme"
        };

        var summary = flags.Count == 0
            ? "Risk engine sees a relatively clean launch profile."
            : $"Risk engine flags: {string.Join(", ", flags)}.";

        var structureBlocked = severeStructureFlags.Count > 0;
        var structureSummary = structureBlocked
            ? $"Structure guard: {string.Join(", ", severeStructureFlags)}."
            : "Structure guard: no hard structural block.";

        return new RiskEvaluation(
            score,
            band,
            summary,
            flags.Count == 0 ? "No major heuristic flags" : string.Join(" | ", flags),
            structureBlocked,
            structureSummary,
            strategyDecision.Label,
            strategyDecision.Summary,
            observedAgeMinutes,
            observedAgeLabel,
            dexQualityScore,
            dexQualityLabel,
            watchlistLabel,
            ownershipSignalLabel,
            strategyDecision.PriorityBonus + (token.WatchlistMatched ? 25 : 0) + Math.Max(0, dexQualityScore - 50));
    }

    private RiskEvaluation EvaluateCexRisk(DexTokenInfo token)
    {
        var score = 0;
        var flags = new List<string>();
        var observedAgeMinutes = GetObservedAgeMinutes(token);
        var observedAgeLabel = BuildObservedAgeLabel(observedAgeMinutes);
        var strategyDecision = EvaluateStrategy(token);
        var watchlistLabel = token.WatchlistMatched
            ? token.WatchlistMatchText
            : "Watchlist priority: no fragment match.";
        var volumeToLiquidityRatio = token.LiquidityUsd > 0 ? token.Volume24h / token.LiquidityUsd : 0m;

        if (token.PriceUsd <= 0m)
        {
            score += 35;
            flags.Add("price feed incomplete");
        }

        if (token.Volume24h < Math.Max(50000m, MinVolume24hUsd))
        {
            score += 18;
            flags.Add("light centralized volume");
        }

        if (token.LiquidityUsd < Math.Max(100000m, MinLiquidityUsd))
        {
            score += 14;
            flags.Add("thin cex book proxy");
        }

        if (Math.Abs(token.PriceChange5m) > 6m)
        {
            score += 8;
            flags.Add("fast 5m extension");
        }

        if (Math.Abs(token.PriceChange1h) > 18m)
        {
            score += 10;
            flags.Add("strong 1h extension");
        }

        if (volumeToLiquidityRatio > 16m)
        {
            score += 8;
            flags.Add($"overheated volume/liquidity x{volumeToLiquidityRatio:0.#}");
        }

        if (token.WatchlistMatched)
        {
            score = Math.Max(0, score - 4);
            flags.Add("watchlist priority");
        }

        score = Math.Min(100, score);
        var band = score switch
        {
            <= 20 => "Low",
            <= 40 => "Guarded",
            <= 65 => "High",
            _ => "Extreme"
        };

        var summary = flags.Count == 0
            ? "CEX venue profile looks clean for a centralized momentum scan."
            : $"CEX risk flags: {string.Join(", ", flags)}.";
        var dexQualityLabel = token.ChainId.Equals("cex-futures", StringComparison.OrdinalIgnoreCase)
            ? "CEX 95/100 - Binance USD-M futures"
            : "CEX 94/100 - Binance spot";

        return new RiskEvaluation(
            score,
            band,
            summary,
            flags.Count == 0 ? "No major heuristic flags" : string.Join(" | ", flags),
            token.PriceUsd <= 0m,
            token.PriceUsd <= 0m ? "Structure guard: price feed incomplete." : "Structure guard: centralized market feed is healthy.",
            strategyDecision.Label,
            strategyDecision.Summary,
            observedAgeMinutes,
            observedAgeLabel,
            token.ChainId.Equals("cex-futures", StringComparison.OrdinalIgnoreCase) ? 95 : 94,
            dexQualityLabel,
            watchlistLabel,
            "CEX venue: no on-chain ownership or LP-lock risk applies.",
            strategyDecision.PriorityBonus + (token.WatchlistMatched ? 20 : 0) + (token.ChainId.Equals("cex-futures", StringComparison.OrdinalIgnoreCase) ? 18 : 14));
    }

    private sealed record RiskEvaluation(
        int Score,
        string Band,
        string Summary,
        string Flags,
        bool StructureBlocked,
        string StructureSummary,
        string StrategyLabel,
        string StrategySummary,
        decimal ObservedAgeMinutes,
        string ObservedAgeLabel,
        int DexQualityScore,
        string DexQualityLabel,
        string WatchlistLabel,
        string OwnershipSignalLabel,
        int PriorityScore);
    private sealed record StrategyDecision(bool Passed, string Label, string Summary, string Reason, int PriorityBonus);
    private sealed record ExecutionEvaluation(bool Blocked, bool SuspectedHoneypot, decimal BuyTaxPercent, decimal SellTaxPercent, string Reason);

}
