using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Base for shared AI digests: computed ONCE with the server key, read by every user.
/// Handles the key gate, the period/cooldown, the model call and parsing — a new AI feature is
/// just a subclass that supplies a system prompt and the facts (from data we already collect).
/// No 'anthropic' key → no-op.
/// </summary>
public abstract class AiDigestJob : IDataCollector
{
    private readonly ProviderKeyStore _keys;
    private readonly AiProxy _ai;
    private readonly ILogger _log;
    protected readonly AiDigestRepository Digests;
    protected readonly string Model;

    protected AiDigestJob(ProviderKeyStore keys, AiProxy ai, AiDigestRepository digests, string model, ILogger log)
    {
        _keys = keys; _ai = ai; Digests = digests; Model = model; _log = log;
    }

    /// <summary>Stable id of this digest stream (also the ai_digests.kind).</summary>
    public abstract string Kind { get; }

    /// <summary>How often this digest should be published.</summary>
    public abstract TimeSpan Period { get; }

    /// <summary>What the model is asked to be/do. Must demand JSON {"title","body"}.</summary>
    protected abstract string SystemPrompt { get; }

    /// <summary>The grounding facts; return null when there's nothing worth publishing.</summary>
    protected abstract Task<string?> BuildFactsAsync(CancellationToken ct);

    public string Name => "digest_" + Kind;
    public TimeSpan Interval => TimeSpan.FromMinutes(15); // tick; Period is the real cadence

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(await _keys.GetAsync("anthropic", ct))) return 0;

        var last = await Digests.LastAtAsync(Kind, ct);
        if (last is { } l && DateTime.UtcNow - l < Period) return 0; // not due

        var facts = await BuildFactsAsync(ct);
        if (string.IsNullOrWhiteSpace(facts)) return 0;

        var res = await _ai.ForwardAnthropicAsync(BuildRequest(facts), ct);
        if (res is null) return 0;
        if (res.Value.Status != 200)
        {
            _log.LogWarning("{Kind}: upstream {Status}", Kind, res.Value.Status);
            return 0;
        }

        var parsed = Parse(res.Value.Body);
        if (parsed is null) { _log.LogWarning("{Kind}: unparsable model output", Kind); return 0; }

        await Digests.InsertAsync(Kind, parsed.Value.Title, parsed.Value.Body, Model, ct);
        _log.LogInformation("{Kind} published: {Title}", Kind, parsed.Value.Title);
        return 1;
    }

    private string BuildRequest(string facts) => JsonSerializer.Serialize(new
    {
        model = Model,
        max_tokens = 900,
        system = SystemPrompt + " Reply with ONLY JSON: {\"title\":\"<=10 words\",\"body\":\"markdown, concise\"}. " +
                 "Ground every claim in the given facts; never invent numbers.",
        messages = new[] { new { role = "user", content = facts } }
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

    /// <summary>Helper for subclasses: serialize facts compactly for the prompt.</summary>
    protected static string Json(object o) => JsonSerializer.Serialize(o);
}
