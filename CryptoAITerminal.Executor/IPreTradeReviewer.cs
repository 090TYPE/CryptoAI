using System.Text.Json;
using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.Executor;

/// <summary>Second opinion on a bot trade before it is placed.</summary>
public interface IPreTradeReviewer
{
    Task<(bool Approved, string? Reason)> ReviewAsync(string side, string asset, decimal amountUsd, CancellationToken ct);
}

/// <summary>
/// Asks the model to sanity-check a bot trade against the market context we collect.
///
/// Availability policy matters here, so it's explicit:
///   • no provider key     → the gate is simply off, the trade proceeds
///   • model unreachable   → proceed (fail-open) and log — a provider blip must not freeze
///                           a user's bots... unless AI_PRETRADE_REQUIRED=true, then block
///   • model says reject   → always block
///
/// Семейство здесь не выбирается и не проверяется: этим занимается <see cref="AiProxy"/>, и
/// решение у него одно на сервер. Раньше класс сам спрашивал ключ <c>anthropic</c> и подставлял
/// имя claude-модели — то есть на выбранном ChatGPT гейт либо считал себя ненастроенным и
/// пропускал сделки, либо уходил с чужим именем модели и получал 404.
/// </summary>
public sealed class AiPreTradeReviewer : IPreTradeReviewer
{
    private readonly AiProxy _ai;
    private readonly AiDigestRepository _facts;
    private readonly ILogger<AiPreTradeReviewer> _log;
    private readonly string? _envModel;
    private readonly bool _required;

    public AiPreTradeReviewer(AiProxy ai, AiDigestRepository facts,
        IConfiguration cfg, ILogger<AiPreTradeReviewer> log)
    {
        _ai = ai; _facts = facts; _log = log;
        _envModel = cfg["AI_PRETRADE_MODEL"];
        _required = bool.TryParse(cfg["AI_PRETRADE_REQUIRED"], out var r) && r;
    }

    public async Task<(bool Approved, string? Reason)> ReviewAsync(string side, string asset, decimal amountUsd, CancellationToken ct)
    {
        try
        {
            var res = await _ai.ForwardAnthropicAsync(await BuildRequestAsync(side, asset, amountUsd, ct), ct: ct);
            // null — ключа провайдера нет вовсе. Это «гейт выключен», а не «провайдер упал», и
            // отличать одно от другого надо здесь: без ключа сообщение про сбой сети увело бы
            // диагностику в сторону.
            if (res is null)
                return _required ? (false, "AI pre-trade review required but no AI key configured") : (true, null);

            if (res.Value.Status != 200)
            {
                _log.LogWarning("pre-trade review unavailable (status {Status})", res.Value.Status);
                return _required ? (false, "AI pre-trade review unavailable") : (true, null);
            }

            var verdict = Parse(res.Value.Body);
            if (verdict is null)
            {
                _log.LogWarning("pre-trade review returned unparsable output");
                return _required ? (false, "AI pre-trade review unparsable") : (true, null);
            }
            return (verdict.Value.Approve, verdict.Value.Reason);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "pre-trade review failed");
            return _required ? (false, "AI pre-trade review errored") : (true, null);
        }
    }

    private async Task<string> BuildRequestAsync(string side, string asset, decimal amountUsd, CancellationToken ct)
    {
        var context = JsonSerializer.Serialize(new
        {
            trade = new { side, asset, amountUsd },
            marketContext = await _facts.GetContextAsync(ct),
            market = await _facts.GetMarketFactsAsync(10, ct),
            headlines = await _facts.GetNewsHeadlinesAsync(10, ct)
        });

        // Модель подбирает сервер под выбранное семейство; переменная окружения остаётся последним
        // словом только для claude-стороны, где её и писали при развёртывании.
        var model = await _ai.ModelForAsync(SettingKeys.PretradeModel, AiModelRole.Background, _envModel, ct: ct)
                    ?? SettingKeys.DefaultBackgroundModel;

        return JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 200,
            system = "You are a risk check on an automated trade that is about to execute. Approve unless the context " +
                     "shows a concrete reason not to (clear danger, obviously broken market). Default to approve when " +
                     "the context is merely thin — the user configured this bot deliberately. " +
                     "Reply with ONLY JSON: {\"approve\":true|false,\"reason\":\"<=20 words\"}.",
            messages = new[] { new { role = "user", content = context } }
        });
    }

    private static (bool Approve, string? Reason)? Parse(string body)
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
            if (!v.RootElement.TryGetProperty("approve", out var a)) return null;
            var approve = a.ValueKind == JsonValueKind.True;
            var reason = v.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return (approve, reason);
        }
        catch { return null; }
    }
}
