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
/// It asks the model whenever one is reachable (local key, or a server-bound
/// terminal whose server holds the key); otherwise (or on any failure) it falls
/// back to a deterministic offline heuristic so the "AI verdict" is always
/// visible — including in the demo / paper flow with neither. Results are
/// cached per token to avoid repeat calls.
/// </summary>
public sealed class TokenSecurityAiService
{
    private readonly ConcurrentDictionary<string, TokenAiVerdict> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vendor API key. Empty on a server-bound terminal — the server holds the key.</summary>
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    /// <summary>True when a live model will be queried (local key, or a bound server holding the key).</summary>
    public bool UsesLiveModel => ChatClient.CanCallModel(ApiKey);

    /// <summary>User-safe reason the last call fell back to the offline path, or null on success.</summary>
    public string? LastError { get; private set; }

    public async Task<TokenAiVerdict> AssessAsync(
        DexTokenInfo token,
        string? securitySummary = null,
        CancellationToken ct = default)
    {
        // Cleared up front so no path — cache hit, no key, or a live answer — reports a failure
        // left over from an earlier token.
        LastError = null;

        var key = $"{token.ChainId}:{token.TokenAddress}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        TokenAiVerdict verdict;
        if (UsesLiveModel)
        {
            try
            {
                var provider = new TokenSecurityAiProvider(ApiKey, Model);
                var live = await provider.AssessAsync(token, securitySummary, ct).ConfigureAwait(false);
                if (live is null)
                {
                    // Ровно та же логика, что и в ветке с исключением ниже: неразобранный ответ —
                    // такой же временный сбой, как таймаут. Кэшируя расчёт по нему, мы прибивали
                    // оффлайн-вердикт к токену на всю сессию — даже после того, как AI снова
                    // заработал, — и молча: LastError оставался пустым.
                    LastError = "Ответ модели не удалось разобрать — показан собственный расчёт.";
                    return BuildHeuristic(token, securitySummary);
                }
                verdict = live;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Network / API / parse failure — never block the sniper, degrade to heuristic.
                LastError = AiFailure.Describe(ex);
                // Returned WITHOUT caching: a budget/timeout failure would otherwise pin the
                // heuristic to this token for the whole session, even once AI works again.
                return BuildHeuristic(token, securitySummary);
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
