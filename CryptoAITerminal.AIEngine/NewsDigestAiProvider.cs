using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Condenses a batch of recent crypto headlines into a one/two-sentence
/// market-pulse digest plus an overall bias. A single model call per refresh
/// (cheap), routed through <see cref="ChatClient"/> to the active vendor.
/// </summary>
public sealed class NewsDigestAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public NewsDigestAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    public async Task<NewsDigest?> SummarizeAsync(
        IReadOnlyList<string> headlines,
        CancellationToken ct = default)
    {
        if (headlines is null || headlines.Count == 0) return null;

        var prompt = "Recent crypto headlines (newest first):\n"
            + string.Join('\n', headlines.Take(25).Select(h => "- " + h))
            + "\n\nSummarize the current market narrative in ONE or TWO sentences and give an overall bias. Return the JSON.";

        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 256, temperature: 0.3,
            system:
                "You are a crypto market analyst. Read the headlines and write a concise market-pulse digest. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"summary\":string,\"bias\":\"BULLISH\"|\"BEARISH\"|\"NEUTRAL\"}.",
            userContent: prompt, _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model);
    }

    private static NewsDigest? ParseResponse(string text, string model)
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

            var biasRaw = root.TryGetProperty("bias", out var b) ? b.GetString() : null;
            var bias = (biasRaw ?? "").Trim().ToUpperInvariant() switch
            {
                "BULLISH" => "BULLISH",
                "BEARISH" => "BEARISH",
                _         => "NEUTRAL"
            };

            return new NewsDigest(summary.Trim(), bias, $"{AiRuntime.VendorLabel} {model}", false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <param name="Bias">BULLISH | BEARISH | NEUTRAL</param>
/// <param name="IsFallback">True when produced by an offline heuristic rather than a model call.</param>
public sealed record NewsDigest(string Summary, string Bias, string Source, bool IsFallback);
