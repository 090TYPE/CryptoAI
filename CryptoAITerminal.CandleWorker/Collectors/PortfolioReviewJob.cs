using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Personal (not broadcast) AI: reviews each user's own watchlist — concentration, risk, what
/// deserves attention — and drops the write-up in their terminal inbox. Cost-bounded: only users
/// who actually follow tokens, at most a few per run, and only if they haven't been reviewed for
/// AI_REVIEW_DAYS. No 'anthropic' key → no-op.
/// </summary>
public sealed class PortfolioReviewJob : IDataCollector
{
    private readonly ProviderKeyStore _keys;
    private readonly PersonalAiRepository _personal;
    private readonly AiProxy _ai;
    private readonly INotifier _notifier;
    private readonly ILogger<PortfolioReviewJob> _log;
    private readonly string _model;
    private readonly int _days;
    private readonly int _batch;

    public PortfolioReviewJob(ProviderKeyStore keys, PersonalAiRepository personal, AiProxy ai,
        INotifier notifier, IConfiguration cfg, ILogger<PortfolioReviewJob> log)
    {
        _keys = keys; _personal = personal; _ai = ai; _notifier = notifier; _log = log;
        _model = cfg["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001";
        _days = int.TryParse(cfg["AI_REVIEW_DAYS"], out var d) ? d : 7;
        _batch = int.TryParse(cfg["AI_REVIEW_BATCH"], out var b) ? b : 3;
    }

    public string Name => "portfolio_review";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(await _keys.GetAsync("anthropic", ct))) return 0;

        var sent = 0;
        foreach (var userId in await _personal.GetUsersDueForReviewAsync(_days, _batch, ct))
        {
            try
            {
                var facts = await _personal.GetUserPortfolioFactsAsync(userId, ct);
                if (facts.Count == 0) continue;

                var res = await _ai.ForwardAnthropicAsync(BuildRequest(facts), ct);
                if (res is null) return sent;
                if (res.Value.Status != 200)
                {
                    _log.LogWarning("portfolio_review: upstream {Status}", res.Value.Status);
                    continue;
                }

                var parsed = Parse(res.Value.Body);
                if (parsed is null) continue;

                await _notifier.SendAsync(userId, parsed.Value.Title, parsed.Value.Body, "portfolio_review", ct);
                sent++;
                _log.LogInformation("portfolio_review sent to {User}: {Title}", userId, parsed.Value.Title);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "portfolio_review {User} failed", userId); }
        }
        return sent;
    }

    private string BuildRequest(IReadOnlyList<dynamic> facts) => JsonSerializer.Serialize(new
    {
        model = _model,
        max_tokens = 700,
        system = "Review this trader's watchlist: concentration, the riskiest names they follow, what looks " +
                 "structurally weak (thin liquidity, honeypot flags, whale-held supply), and what deserves a look. " +
                 "Be direct and specific. Never invent numbers; say when data is missing. Not financial advice. " +
                 "Reply with ONLY JSON: {\"title\":\"<=10 words\",\"body\":\"markdown, concise\"}.",
        messages = new[] { new { role = "user", content = "Watchlist:\n" + JsonSerializer.Serialize(facts) } }
    });

    private static (string Title, string Body)? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return null;

            string? text = null;
            foreach (var b in content.EnumerateArray())
                if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tv))
                { text = tv.GetString(); break; }
            if (string.IsNullOrWhiteSpace(text)) return null;

            text = text.Trim();
            if (text.StartsWith("```")) text = text.Trim('`').TrimStart('j', 's', 'o', 'n').Trim();

            using var v = JsonDocument.Parse(text);
            var title = v.RootElement.TryGetProperty("title", out var ti) ? ti.GetString() : null;
            var bodyText = v.RootElement.TryGetProperty("body", out var bo) ? bo.GetString() : null;
            return string.IsNullOrWhiteSpace(title) ? null : (title!, bodyText ?? "");
        }
        catch { return null; }
    }
}
