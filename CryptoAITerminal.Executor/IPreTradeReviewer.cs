using System.Text.Json;
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
/// Asks Claude to sanity-check a bot trade against the market context we collect.
///
/// Availability policy matters here, so it's explicit:
///   • no 'anthropic' key  → the gate is simply off, the trade proceeds
///   • model unreachable   → proceed (fail-open) and log — an Anthropic blip must not freeze
///                           a user's bots... unless AI_PRETRADE_REQUIRED=true, then block
///   • model says reject   → always block
/// </summary>
public sealed class AiPreTradeReviewer : IPreTradeReviewer
{
    private readonly ProviderKeyStore _keys;
    private readonly AiProxy _ai;
    private readonly AiDigestRepository _facts;
    private readonly ILogger<AiPreTradeReviewer> _log;
    private readonly string _model;
    private readonly bool _required;

    public AiPreTradeReviewer(ProviderKeyStore keys, AiProxy ai, AiDigestRepository facts,
        IConfiguration cfg, ILogger<AiPreTradeReviewer> log)
    {
        _keys = keys; _ai = ai; _facts = facts; _log = log;
        _model = cfg["AI_PRETRADE_MODEL"] ?? "claude-haiku-4-5-20251001";
        _required = bool.TryParse(cfg["AI_PRETRADE_REQUIRED"], out var r) && r;
    }

    public async Task<(bool Approved, string? Reason)> ReviewAsync(string side, string asset, decimal amountUsd, CancellationToken ct)
    {
        var key = await _keys.GetAsync("anthropic", ct);
        if (string.IsNullOrWhiteSpace(key))
            return _required ? (false, "AI pre-trade review required but no AI key configured") : (true, null);

        try
        {
            var res = await _ai.ForwardAnthropicAsync(await BuildRequestAsync(side, asset, amountUsd, ct), ct: ct);
            if (res is null || res.Value.Status != 200)
            {
                _log.LogWarning("pre-trade review unavailable (status {Status})", res?.Status);
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

        return JsonSerializer.Serialize(new
        {
            model = _model,
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
