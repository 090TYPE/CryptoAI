using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Keyed collector: watches favorited tokens and pings their owners when Claude judges a move
/// worth waking them for. Cost control is two-stage — a deterministic pre-filter (1h move over
/// a threshold, cooldown per token) picks candidates, and only those go to the model.
/// No 'anthropic' key → no-op.
/// </summary>
public sealed class AiAnomalyCollector : IDataCollector
{
    private readonly ProviderKeyStore _keys;
    private readonly AiAnomalyRepository _anomalies;
    private readonly AiProxy _ai;
    private readonly INotifier _notifier;
    private readonly ILogger<AiAnomalyCollector> _log;
    private readonly string _model;
    private readonly decimal _minMovePct;
    private readonly int _cooldownHours;
    private readonly int _batch;

    public AiAnomalyCollector(ProviderKeyStore keys, AiAnomalyRepository anomalies, AiProxy ai,
        INotifier notifier, IConfiguration cfg, ILogger<AiAnomalyCollector> log)
    {
        _keys = keys; _anomalies = anomalies; _ai = ai; _notifier = notifier; _log = log;
        _model = cfg["AI_ANOMALY_MODEL"] ?? "claude-3-5-haiku-20241022";
        _minMovePct = decimal.TryParse(cfg["AI_ANOMALY_MIN_MOVE_PCT"], out var m) ? m : 15m;
        _cooldownHours = int.TryParse(cfg["AI_ANOMALY_COOLDOWN_HOURS"], out var c) ? c : 6;
        _batch = int.TryParse(cfg["AI_ANOMALY_BATCH"], out var b) ? b : 5;
    }

    public string Name => "ai_anomaly";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(await _keys.GetAsync("anthropic", ct)))
            return 0; // no server AI key → stay off

        var fired = 0;
        foreach (var c in await _anomalies.GetCandidatesAsync(_minMovePct, _cooldownHours, _batch, ct))
        {
            try
            {
                var res = await _ai.ForwardAnthropicAsync(BuildRequest(c), ct);
                if (res is null) return fired;
                if (res.Value.Status != 200)
                {
                    _log.LogWarning("ai_anomaly {Symbol}: upstream {Status}", c.Symbol, res.Value.Status);
                    continue;
                }

                var v = Parse(res.Value.Body);
                if (v is null || !v.Value.Anomaly) continue;

                await _anomalies.InsertAsync(c.Chain, c.TokenAddress, c.Symbol,
                    v.Value.Severity, v.Value.Headline, v.Value.Detail, ct);

                foreach (var userId in await _anomalies.GetFavoritedByAsync(c.Chain, c.TokenAddress, ct))
                    await _notifier.SendAsync(userId, $"⚠ {c.Symbol}: {v.Value.Headline}", v.Value.Detail ?? "", "anomaly", ct);

                fired++;
                _log.LogInformation("ai_anomaly {Symbol}: {Severity} — {Headline}", c.Symbol, v.Value.Severity, v.Value.Headline);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "ai_anomaly {Token} failed", c.TokenAddress); }
        }
        return fired;
    }

    private string BuildRequest(AnomalyCandidate c)
    {
        var facts = JsonSerializer.Serialize(new
        {
            c.Symbol, c.Chain, c.PriceUsd, change1hPct = c.Chg1h, change24hPct = c.Chg24h, c.LiqUsd, c.Vol24h
        });

        var payload = new
        {
            model = _model,
            max_tokens = 250,
            system = "You watch DEX tokens a trader follows. Decide if the move is genuinely worth a push " +
                     "notification (real signal, not routine noise). Reply with ONLY JSON: " +
                     "{\"anomaly\":true|false,\"severity\":\"low|medium|high\",\"headline\":\"<=8 words\",\"detail\":\"1-2 sentences\"}. " +
                     "Be conservative: anomaly=false for ordinary volatility.",
            messages = new[] { new { role = "user", content = "Token snapshot:\n" + facts } }
        };
        return JsonSerializer.Serialize(payload);
    }

    private static (bool Anomaly, string? Severity, string? Headline, string? Detail)? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return null;

            string? text = null;
            foreach (var b in content.EnumerateArray())
                if (b.TryGetProperty("type", out var bt) && bt.GetString() == "text" && b.TryGetProperty("text", out var tv))
                { text = tv.GetString(); break; }
            if (string.IsNullOrWhiteSpace(text)) return null;

            text = text.Trim();
            if (text.StartsWith("```")) text = text.Trim('`').TrimStart('j', 's', 'o', 'n').Trim();

            using var v = JsonDocument.Parse(text);
            var r = v.RootElement;
            var anomaly = r.TryGetProperty("anomaly", out var a) && a.ValueKind == JsonValueKind.True;
            return (anomaly,
                    r.TryGetProperty("severity", out var s) ? s.GetString() : null,
                    r.TryGetProperty("headline", out var h) ? h.GetString() : null,
                    r.TryGetProperty("detail", out var d) ? d.GetString() : null);
        }
        catch { return null; }
    }
}
