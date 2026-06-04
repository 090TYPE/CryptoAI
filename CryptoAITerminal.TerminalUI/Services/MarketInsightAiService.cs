using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Reusable "interpret raw data → narrative + signal" service backing the whale-flow,
/// on-chain, sentiment and liquidation insight panels. Callers (which own the domain
/// data) supply pre-formatted lines, the allowed signal vocabulary, and a deterministic
/// offline fallback — so every panel shows an AI read, with or without an API key.
/// </summary>
public sealed class MarketInsightAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<InsightResult> InterpretAsync(
        string roleSentence,
        IReadOnlyList<string> dataLines,
        IReadOnlyList<string> signalVocabulary,
        Func<InsightResult> offline,
        CancellationToken ct = default)
    {
        if (dataLines is null || dataLines.Count == 0)
            return offline();

        if (UsesLiveModel)
        {
            try
            {
                var provider = new MarketInsightAiProvider(ApiKey, Model);
                var result = await provider.InterpretAsync(roleSentence, dataLines, signalVocabulary, ct).ConfigureAwait(false);
                if (result is not null) return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        return offline();
    }
}

/// <summary>
/// Pure, deterministic offline heuristics for the insight panels — also unit-tested.
/// Kept separate from the domain ViewModels so they can be reused and verified.
/// </summary>
public static class InsightHeuristics
{
    /// <summary>Whale flow: net USD onto exchanges (inflow) vs off (outflow).</summary>
    public static InsightResult WhaleFlow(decimal inflowUsd, decimal outflowUsd, int transferCount)
    {
        if (transferCount == 0)
            return new InsightResult("No notable whale transfers in the window.", "NEUTRAL", [], "Heuristic (offline)", true);

        var net = inflowUsd - outflowUsd;                       // +ve = net to exchanges
        var total = inflowUsd + outflowUsd;
        var skew = total > 0 ? net / total : 0m;                // -1..+1

        var signal = skew > 0.25m ? "DISTRIBUTION"              // coins moving TO exchanges → sell pressure
                   : skew < -0.25m ? "ACCUMULATION"             // moving OFF → holding
                   : "NEUTRAL";

        var summary = signal switch
        {
            "DISTRIBUTION" => $"Whales moved a net ${Money(net)} ONTO exchanges across {transferCount} transfers — potential sell pressure.",
            "ACCUMULATION" => $"Whales pulled a net ${Money(-net)} OFF exchanges across {transferCount} transfers — accumulation / holding.",
            _              => $"Whale flows are balanced across {transferCount} transfers (net ${Money(net)})."
        };

        var bullets = new List<string>
        {
            $"Inflow to exchanges: ${Money(inflowUsd)}",
            $"Outflow from exchanges: ${Money(outflowUsd)}",
            $"Net: ${Money(net)} ({skew:+0.0%;-0.0%} skew)"
        };
        return new InsightResult(summary, signal, bullets.ToArray(), "Heuristic (offline)", true);
    }

    private static string Money(decimal v)
    {
        var a = Math.Abs(v);
        return a >= 1_000_000m ? $"{v / 1_000_000m:0.##}M"
             : a >= 1_000m ? $"{v / 1_000m:0.#}K"
             : $"{v:0}";
    }
}
