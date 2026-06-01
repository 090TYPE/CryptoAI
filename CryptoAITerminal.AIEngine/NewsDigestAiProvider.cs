using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Condenses a batch of recent crypto headlines into a one/two-sentence
/// market-pulse digest plus an overall bias. A single Claude call per refresh
/// (cheap), mirroring <see cref="ClaudeSignalProvider"/>'s hand-rolled HTTP.
/// </summary>
public sealed class NewsDigestAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public NewsDigestAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<NewsDigest?> SummarizeAsync(
        IReadOnlyList<string> headlines,
        CancellationToken ct = default)
    {
        if (headlines is null || headlines.Count == 0) return null;

        var prompt = "Recent crypto headlines (newest first):\n"
            + string.Join('\n', headlines.Take(25).Select(h => "- " + h))
            + "\n\nSummarize the current market narrative in ONE or TWO sentences and give an overall bias. Return the JSON.";

        var payload = new
        {
            model       = _model,
            max_tokens  = 256,
            temperature = 0.3,
            system =
                "You are a crypto market analyst. Read the headlines and write a concise market-pulse digest. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"summary\":string,\"bias\":\"BULLISH\"|\"BEARISH\"|\"NEUTRAL\"}.",
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
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

    private static NewsDigest? ParseResponse(string body, string model)
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
            {
                text = v.GetString() ?? "";
                break;
            }
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

            var biasRaw = root.TryGetProperty("bias", out var b) ? b.GetString() : null;
            var bias = (biasRaw ?? "").Trim().ToUpperInvariant() switch
            {
                "BULLISH" => "BULLISH",
                "BEARISH" => "BEARISH",
                _         => "NEUTRAL"
            };

            return new NewsDigest(summary.Trim(), bias, $"Claude {model}", false);
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
