using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Reviews a trader's closed-trade statistics and writes coaching feedback:
/// strengths, recurring leaks, and concrete suggestions. The host computes the
/// aggregate stat lines (so raw trades never leave the machine); the provider
/// only interprets them. The vendor call goes through <see cref="ChatClient"/>.
/// </summary>
public sealed class TradeJournalCoachAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public TradeJournalCoachAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    public async Task<JournalReview?> ReviewAsync(IReadOnlyList<JournalStatLine> stats, CancellationToken ct = default)
    {
        if (stats is null || stats.Count == 0) return null;

        var prompt = "Closed-trade statistics:\n"
            + string.Join('\n', stats.Select(s => $"- {s.Label}: {s.Value}"))
            + "\n\nGive concise, actionable coaching. Return the JSON.";

        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 600, temperature: 0.3,
            system:
                "You are a trading-performance coach. From the statistics, identify what the trader " +
                "does well, the recurring leaks (where they lose money), and concrete, specific " +
                "suggestions to improve. Be direct and practical; avoid generic platitudes. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"summary\":string,\"strengths\":[string],\"leaks\":[string],\"suggestions\":[string]}.",
            userContent: prompt, _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model);
    }

    private static JournalReview? ParseResponse(string text, string model)
    {
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
                $"{AiRuntime.VendorLabel} {model}", false);
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
