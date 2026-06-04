using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// The single vendor-aware seam for one-shot "system + user → assistant text" calls.
/// Every non-agentic AI provider (market insight, ranking, alerts, options, …) builds
/// its own prompt and parses its own JSON, but the wire call in the middle — which
/// endpoint, which headers, which response shape — lives here and branches on
/// <see cref="AiRuntime.Vendor"/>. That is what lets one toggle in Settings reroute
/// every AI feature between Claude and ChatGPT.
///
/// Hand-rolled HTTP (no vendor SDK) to keep the terminal buildable offline, mirroring
/// the original Anthropic-only providers this replaced.
/// </summary>
public static class ChatClient
{
    private const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const string OpenAiEndpoint = "https://api.openai.com/v1/chat/completions";

    /// <summary>
    /// Sends a single-turn completion and returns the assistant's text (already
    /// extracted from the vendor's response envelope). Throws
    /// <see cref="HttpRequestException"/> on a non-success status so callers can fall
    /// back to their offline heuristic, exactly as the old per-provider code did.
    /// </summary>
    /// <param name="apiKey">Key for the active vendor (callers pass <c>AiRuntime.ActiveApiKey</c> by default).</param>
    /// <param name="model">Model id for the active vendor.</param>
    public static async Task<string> CompleteTextAsync(
        string apiKey,
        string model,
        int maxTokens,
        double? temperature,
        string system,
        string userContent,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));

        var client = http ?? SharedHttp;
        return AiRuntime.Vendor == AiVendor.OpenAi
            ? await OpenAiAsync(client, apiKey, model, maxTokens, temperature, system, userContent, ct).ConfigureAwait(false)
            : await AnthropicAsync(client, apiKey, model, maxTokens, temperature, system, userContent, ct).ConfigureAwait(false);
    }

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ── Anthropic Messages API ────────────────────────────────────────────────
    private static async Task<string> AnthropicAsync(
        HttpClient http, string apiKey, string model, int maxTokens, double? temperature,
        string system, string userContent, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = new[] { new { role = "user", content = userContent } }
        };
        if (temperature is { } t) payload["temperature"] = t;

        using var req = new HttpRequestMessage(HttpMethod.Post, AnthropicEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API {(int)res.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var contentArr) ||
            contentArr.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var block in contentArr.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                block.TryGetProperty("text", out var v))
                return v.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    // ── OpenAI Chat Completions API ───────────────────────────────────────────
    private static async Task<string> OpenAiAsync(
        HttpClient http, string apiKey, string model, int maxTokens, double? temperature,
        string system, string userContent, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent }
            }
        };
        if (temperature is { } t) payload["temperature"] = t;

        using var req = new HttpRequestMessage(HttpMethod.Post, OpenAiEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Add("Authorization", "Bearer " + apiKey);

        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI API {(int)res.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return string.Empty;

        var msg = choices[0];
        if (msg.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        return string.Empty;
    }
}
