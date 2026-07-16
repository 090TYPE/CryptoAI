using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Keyed collector: scores each tracked token ONCE with the server's Claude key and caches the
/// verdict for every user (rug risk / narrative). Uses the data we already collect (price,
/// liquidity, volume, holders, security) as grounding — no extra provider calls.
/// No 'anthropic' key → no-op. Cost-bounded: a few tokens per run, re-scored only when stale.
/// </summary>
public sealed class AiScoreCollector : IDataCollector
{
    private readonly ProviderKeyStore _keys;
    private readonly AiScoreRepository _scores;
    private readonly ApiReadRepository _read;
    private readonly AiProxy _ai;
    private readonly ILogger<AiScoreCollector> _log;
    private readonly string _model;
    private readonly int _batch;
    private readonly int _maxAgeHours;

    public AiScoreCollector(ProviderKeyStore keys, AiScoreRepository scores, ApiReadRepository read,
        AiProxy ai, IConfiguration cfg, ILogger<AiScoreCollector> log)
    {
        _keys = keys; _scores = scores; _read = read; _ai = ai; _log = log;
        _model = cfg["AI_SCORE_MODEL"] ?? "claude-3-5-haiku-20241022";
        _batch = int.TryParse(cfg["AI_SCORE_BATCH"], out var b) ? b : 5;
        _maxAgeHours = int.TryParse(cfg["AI_SCORE_MAX_AGE_HOURS"], out var h) ? h : 24;
    }

    public string Name => "ai_score";
    public TimeSpan Interval => TimeSpan.FromMinutes(30);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(await _keys.GetAsync("anthropic", ct)))
            return 0; // no server AI key → stay off

        var scored = 0;
        foreach (var t in await _scores.GetStaleAsync(_maxAgeHours, _batch, ct))
        {
            try
            {
                var detail = await _read.GetTokenDetailAsync(t.Chain, t.TokenAddress, ct);
                if (detail is null) continue;

                var request = BuildRequest(detail);
                var res = await _ai.ForwardAnthropicAsync(request, ct);
                if (res is null) return scored;                 // key vanished mid-run
                if (res.Value.Status != 200)
                {
                    _log.LogWarning("ai_score {Symbol}: upstream {Status}", detail.Symbol, res.Value.Status);
                    continue;
                }

                var verdict = ParseVerdict(res.Value.Body);
                if (verdict is null) continue;

                await _scores.UpsertAsync(t.Chain, t.TokenAddress, verdict.Value.Score,
                    verdict.Value.Verdict, verdict.Value.Summary, _model, ct);
                scored++;
                _log.LogInformation("ai_score {Symbol}: {Score} — {Verdict}", detail.Symbol, verdict.Value.Score, verdict.Value.Verdict);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "ai_score {Token} failed", t.TokenAddress); }
        }
        return scored;
    }

    /// <summary>Anthropic Messages payload grounded in the data we already hold.</summary>
    private string BuildRequest(TokenDetail d)
    {
        var facts = JsonSerializer.Serialize(new
        {
            d.Symbol, d.Name, d.Chain, d.PriceUsd, d.LiqUsd, d.Vol24h, d.Chg24h, d.MarketCap,
            d.HolderCount, d.HoldersTop10Pct, d.IsHoneypot, d.BuyTax, d.SellTax, d.RiskLabel, d.RiskScore,
            d.DeployerTokensDeployed, d.DeployerRugpulls, d.DeployerWalletAgeMonths
        });

        var payload = new
        {
            model = _model,
            max_tokens = 300,
            system = "You are a crypto risk analyst. Judge a DEX token from the given on-chain facts. " +
                     "Reply with ONLY a JSON object: {\"score\":0-100 (higher = safer),\"verdict\":\"<=6 words\",\"summary\":\"1-2 sentences\"}. " +
                     "No markdown, no extra text. Base it strictly on the facts; say so when data is missing.",
            messages = new[] { new { role = "user", content = "Token facts:\n" + facts } }
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Pull the model's JSON out of the Anthropic response envelope.</summary>
    private static (int? Score, string? Verdict, string? Summary)? ParseVerdict(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return null;

            string? text = null;
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                    block.TryGetProperty("text", out var tv))
                { text = tv.GetString(); break; }
            if (string.IsNullOrWhiteSpace(text)) return null;

            text = text.Trim();
            if (text.StartsWith("```"))            // tolerate fenced output
                text = text.Trim('`').TrimStart('j', 's', 'o', 'n').Trim();

            using var v = JsonDocument.Parse(text);
            var root = v.RootElement;
            int? score = root.TryGetProperty("score", out var s) && s.TryGetInt32(out var si) ? si : null;
            return (score,
                    root.TryGetProperty("verdict", out var vd) ? vd.GetString() : null,
                    root.TryGetProperty("summary", out var sm) ? sm.GetString() : null);
        }
        catch { return null; }
    }
}
