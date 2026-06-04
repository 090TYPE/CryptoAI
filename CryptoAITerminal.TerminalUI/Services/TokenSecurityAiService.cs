using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Produces an <see cref="TokenAiVerdict"/> for a sniper candidate.
///
/// When an Anthropic API key is configured it asks Claude; otherwise (or on
/// any failure) it falls back to a deterministic offline heuristic so the
/// "AI verdict" is always visible — including in the demo / paper flow
/// without any keys. Results are cached per token to avoid repeat calls.
/// </summary>
public sealed class TokenSecurityAiService
{
    private readonly ConcurrentDictionary<string, TokenAiVerdict> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Anthropic API key. Empty → heuristic-only mode.</summary>
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    /// <summary>True when a live model will be queried (key present).</summary>
    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<TokenAiVerdict> AssessAsync(
        DexTokenInfo token,
        string? securitySummary = null,
        CancellationToken ct = default)
    {
        var key = $"{token.ChainId}:{token.TokenAddress}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        TokenAiVerdict verdict;
        if (UsesLiveModel)
        {
            try
            {
                var provider = new TokenSecurityAiProvider(ApiKey, Model);
                verdict = await provider.AssessAsync(token, securitySummary, ct).ConfigureAwait(false)
                          ?? BuildHeuristic(token, securitySummary);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Network / API / parse failure — never block the sniper, degrade to heuristic.
                verdict = BuildHeuristic(token, securitySummary);
            }
        }
        else
        {
            verdict = BuildHeuristic(token, securitySummary);
        }

        _cache[key] = verdict;
        return verdict;
    }

    /// <summary>Drop a cached verdict so the next assessment re-runs (e.g. on re-arm).</summary>
    public void Invalidate(DexTokenInfo token) =>
        _cache.TryRemove($"{token.ChainId}:{token.TokenAddress}", out _);

    // ── Deterministic offline heuristic ──────────────────────────────────────
    // Mirrors the spirit of the rule-based risk engine but returns a verdict the
    // UI can show as "AI (offline)". Higher score = riskier.

    private static TokenAiVerdict BuildHeuristic(DexTokenInfo t, string? securitySummary)
    {
        var score = 0;
        var flags = new List<string>();

        if (t.LiquidityUsd <= 0m)
        {
            score += 25;
            flags.Add("no liquidity data");
        }
        else if (t.LiquidityUsd < 25000m)
        {
            score += 22;
            flags.Add("very thin liquidity");
        }
        else if (t.LiquidityUsd < 80000m)
        {
            score += 10;
            flags.Add("thin liquidity");
        }

        if (t.MarketCap > 0m && t.LiquidityUsd > 0m && t.MarketCap / t.LiquidityUsd > 20m)
        {
            score += 14;
            flags.Add("market cap >> liquidity");
        }

        if (t.Volume24h > 0m && t.LiquidityUsd > 0m && t.Volume24h / t.LiquidityUsd > 12m)
        {
            score += 12;
            flags.Add("overheated volume/liquidity");
        }

        if (t.PriceChange1h > 150m)
        {
            score += 14;
            flags.Add("explosive 1h pump");
        }
        else if (t.PriceChange5m > 45m)
        {
            score += 10;
            flags.Add("sharp 5m spike");
        }

        if (t.ObservedFirstSeenUtc != DateTime.MinValue)
        {
            var ageMin = (DateTime.UtcNow - t.ObservedFirstSeenUtc).TotalMinutes;
            if (ageMin <= 5)
            {
                score += 12;
                flags.Add("freshly deployed pair");
            }
        }

        if (string.IsNullOrWhiteSpace(t.PairAddress) || string.IsNullOrWhiteSpace(t.TokenAddress))
        {
            score += 16;
            flags.Add("incomplete on-chain metadata");
        }

        if (!string.IsNullOrWhiteSpace(securitySummary))
        {
            var s = securitySummary.ToLowerInvariant();
            if (s.Contains("honeypot")) { score += 30; flags.Add("honeypot signal"); }
            if (s.Contains("mintable")) { score += 12; flags.Add("mintable supply"); }
            if (s.Contains("blacklist")) { score += 10; flags.Add("blacklist function"); }
        }

        score = Math.Clamp(score, 0, 100);

        var verdict = score switch
        {
            >= 70 => "AVOID",
            >= 45 => "RISKY",
            >= 25 => "NEUTRAL",
            _     => "FAVORABLE"
        };

        var reason = flags.Count == 0
            ? "Offline heuristic sees a relatively clean profile; no live model key configured."
            : $"Offline heuristic flags: {string.Join(", ", flags)}.";

        return new TokenAiVerdict
        {
            RiskScore  = score,
            Verdict    = verdict,
            RedFlags   = flags.ToArray(),
            Reason     = reason,
            Source     = "Heuristic (offline)",
            IsFallback = true
        };
    }
}
