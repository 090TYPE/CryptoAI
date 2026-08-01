using System.Text.Json;
using CryptoAITerminal.Server.Common;
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
    private readonly SettingsStore _settings;
    private readonly ILogger<PortfolioReviewJob> _log;
    private readonly string? _envModel;
    private readonly string? _envDays;
    private readonly string? _envBatch;

    public PortfolioReviewJob(ProviderKeyStore keys, PersonalAiRepository personal, AiProxy ai,
        INotifier notifier, SettingsStore settings, IConfiguration cfg, ILogger<PortfolioReviewJob> log)
    {
        _keys = keys; _personal = personal; _ai = ai; _notifier = notifier; _settings = settings; _log = log;
        _envModel = cfg["AI_DIGEST_MODEL"];
        _envDays = cfg["AI_REVIEW_DAYS"];
        _envBatch = cfg["AI_REVIEW_BATCH"];
    }

    public string Name => "portfolio_review";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        if (!await _settings.GetBoolAsync(SettingKeys.AiEnabled, true, ct)) return 0;
        // Ключ спрашивается у того провайдера, который сейчас обслуживает вызовы: раньше здесь стоял
        // жёсткий "anthropic", и перевод сервера на ChatGPT молча выключал это задание.
        if (!await _ai.HasKeyForServingFamilyAsync(ct)) return 0;

        // Its own key rather than the digest one: this is the knob that turns weekly reviews into
        // daily for the top tier, and it must move without dragging the 31 digests with it.
        var model = await _ai.ModelForAsync(SettingKeys.ReviewModel, AiModelRole.Background, _envModel, ct: ct)
                    ?? SettingKeys.DefaultBackgroundModel;
        var days = await _settings.GetIntAsync(SettingKeys.ReviewDays, SettingsStore.AsInt(_envDays, 7), ct);
        var batch = await _settings.GetIntAsync(SettingKeys.ReviewBatch, SettingsStore.AsInt(_envBatch, 3), ct);

        var sent = 0;
        foreach (var userId in await _personal.GetUsersDueForReviewAsync(days, batch, ct))
        {
            try
            {
                var facts = await _personal.GetUserPortfolioFactsAsync(userId, ct);
                if (facts.Count == 0) continue;

                var res = await _ai.ForwardAnthropicAsync(BuildRequest(facts, model), ct: ct);
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

    private static string BuildRequest(IReadOnlyList<dynamic> facts, string model) => JsonSerializer.Serialize(new
    {
        model,
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
