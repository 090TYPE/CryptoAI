using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Suggests target portfolio weights for a risk profile. Claude when a key is set,
/// otherwise a deterministic rule-based allocation (tier assets into majors / alts /
/// cash and weight by profile) so the rebalancer always has an "AI suggest" action.
/// </summary>
public sealed class PortfolioRebalanceAiService
{
    public string ApiKey { get; set; } =
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        ?? Environment.GetEnvironmentVariable("CRYPTOAI_CLAUDE_KEY")
        ?? string.Empty;

    public string Model { get; set; } = "claude-sonnet-4-6";

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    private static readonly HashSet<string> Majors = new(StringComparer.OrdinalIgnoreCase) { "BTC", "ETH", "WBTC", "WETH" };
    private static readonly HashSet<string> Stables = new(StringComparer.OrdinalIgnoreCase) { "USDT", "USDC", "BUSD", "DAI", "TUSD", "FDUSD" };

    public async Task<RebalancePlan> SuggestAsync(
        IReadOnlyList<HoldingRow> holdings,
        string riskProfile,
        CancellationToken ct = default)
    {
        if (holdings is null || holdings.Count == 0)
            return new RebalancePlan([], "No holdings to rebalance.", "Heuristic (offline)", true);

        if (UsesLiveModel)
        {
            try
            {
                var provider = new PortfolioRebalanceAiProvider(ApiKey, Model);
                var plan = await provider.SuggestAsync(holdings, riskProfile, ct).ConfigureAwait(false);
                if (plan is not null && plan.Targets.Count > 0) return Normalize(plan);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        return BuildOffline(holdings, riskProfile);
    }

    /// <summary>
    /// Rule-based split: each profile targets a (majors / alts / cash) mix, then
    /// distributes within each bucket proportionally to current value.
    /// </summary>
    private static RebalancePlan BuildOffline(IReadOnlyList<HoldingRow> holdings, string profile)
    {
        var (majorTarget, altTarget, cashTarget) = profile.Trim().ToLowerInvariant() switch
        {
            "conservative" => (55m, 15m, 30m),
            "aggressive"   => (45m, 50m, 5m),
            _              => (55m, 30m, 15m), // balanced
        };

        var majors = holdings.Where(h => Majors.Contains(h.Symbol)).ToList();
        var stables = holdings.Where(h => Stables.Contains(h.Symbol)).ToList();
        var alts = holdings.Where(h => !Majors.Contains(h.Symbol) && !Stables.Contains(h.Symbol)).ToList();

        // Buckets with no holdings forfeit their target to cash so weights still sum to 100.
        if (majors.Count == 0) { cashTarget += majorTarget; majorTarget = 0m; }
        if (alts.Count == 0)   { cashTarget += altTarget;   altTarget = 0m; }

        var targets = new List<RebalanceTarget>();
        targets.AddRange(Distribute(majors, majorTarget, "core major allocation"));
        targets.AddRange(Distribute(alts, altTarget, "satellite alt exposure"));

        // Cash bucket: prefer an existing stable, else synthesise USDT.
        if (cashTarget > 0m)
        {
            if (stables.Count > 0)
                targets.AddRange(Distribute(stables, cashTarget, "stablecoin buffer"));
            else
                targets.Add(new RebalanceTarget("USDT", Math.Round(cashTarget, 1), "stablecoin buffer (add cash)"));
        }

        var commentary = $"{Capitalize(profile)} profile: ~{majorTarget:0}% majors, {altTarget:0}% alts, {cashTarget:0}% cash.";
        return Normalize(new RebalancePlan(targets, commentary, "Heuristic (offline)", true));
    }

    private static IEnumerable<RebalanceTarget> Distribute(List<HoldingRow> bucket, decimal bucketPct, string reason)
    {
        if (bucket.Count == 0 || bucketPct <= 0m) yield break;
        var total = bucket.Sum(h => h.ValueUsd);
        foreach (var h in bucket)
        {
            var share = total > 0 ? h.ValueUsd / total : 1m / bucket.Count;
            yield return new RebalanceTarget(h.Symbol.ToUpperInvariant(), Math.Round(bucketPct * share, 1), reason);
        }
    }

    /// <summary>Rescale to sum to exactly 100% (defends against model drift / rounding).</summary>
    private static RebalancePlan Normalize(RebalancePlan plan)
    {
        var sum = plan.Targets.Sum(t => t.TargetPct);
        if (sum <= 0m) return plan;
        var scaled = plan.Targets
            .Select(t => t with { TargetPct = Math.Round(t.TargetPct * 100m / sum, 1) })
            .ToList();
        return plan with { Targets = scaled };
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}
