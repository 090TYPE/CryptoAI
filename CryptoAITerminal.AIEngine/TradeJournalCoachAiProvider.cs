using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Reviews a trader's closed-trade statistics and writes coaching feedback:
/// strengths, recurring leaks, and concrete suggestions. The host computes the
/// aggregate stat lines (so raw trades never leave the machine); the provider
/// only interprets them. Hand-rolled HTTP like the other AIEngine providers.
/// </summary>
public sealed class TradeJournalCoachAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public TradeJournalCoachAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<JournalReview?> ReviewAsync(IReadOnlyList<JournalStatLine> stats, CancellationToken ct = default)
    {
        if (stats is null || stats.Count == 0) return null;

        var prompt = "Closed-trade statistics:\n"
            + string.Join('\n', stats.Select(s => $"- {s.Label}: {s.Value}"))
            + "\n\nGive concise, actionable coaching. Return the JSON.";

        var payload = new
        {
            model       = _model,
            max_tokens  = 600,
            temperature = 0.3,
            system =
                "You are a trading-performance coach. From the statistics, identify what the trader " +
                "does well, the recurring leaks (where they lose money), and concrete, specific " +
                "suggestions to improve. Be direct and practical; avoid generic platitudes. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"summary\":string,\"strengths\":[string],\"leaks\":[string],\"suggestions\":[string]}.",
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, DefaultEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Add("x-api-key",         _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API {(int)res.StatusCode}: {body}");

        return ParseResponse(body, _model);
    }

    private static JournalReview? ParseResponse(string body, string model)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var contentArr) ||
            contentArr.ValueKind != JsonValueKind.Array || contentArr.GetArrayLength() == 0)
            return null;

        string text = "";
        foreach (var block in contentArr.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                block.TryGetProperty("text", out var v))
            { text = v.GetString() ?? ""; break; }
        }
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }

        try
        {
            using var parsed = JsonDocument.Parse(text);
            var root = parsed.RootElement;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(summary)) return null;

            return new JournalReview(
                summary.Trim(),
                ReadArray(root, "strengths"),
                ReadArray(root, "leaks"),
                ReadArray(root, "suggestions"),
                $"Claude {model}", false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] ReadArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                var s = e.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
        return list.ToArray();
    }
}

public readonly record struct JournalStatLine(string Label, string Value);

public sealed record JournalReview(
    string Summary,
    string[] Strengths,
    string[] Leaks,
    string[] Suggestions,
    string Source,
    bool IsFallback);
