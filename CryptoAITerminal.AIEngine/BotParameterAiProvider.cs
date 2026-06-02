using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Suggests starting parameters for a Grid or DCA bot from current market context.
/// The caller lists the parameter keys it wants values for; the model returns a
/// numeric map plus a rationale. Hand-rolled HTTP like the other AIEngine providers.
/// </summary>
public sealed class BotParameterAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public BotParameterAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<BotParamSuggestion?> SuggestAsync(
        string botType,
        IReadOnlyList<string> contextLines,
        IReadOnlyList<string> paramKeys,
        CancellationToken ct = default)
    {
        if (paramKeys is null || paramKeys.Count == 0) return null;

        var keysCsv = string.Join(", ", paramKeys);
        var prompt = $"Bot: {botType}\nMarket context:\n"
            + string.Join('\n', (contextLines ?? []).Take(20).Select(l => "- " + l))
            + $"\n\nSuggest numeric values for these keys: {keysCsv}. Return the JSON.";

        var payload = new
        {
            model       = _model,
            max_tokens  = 400,
            temperature = 0.2,
            system =
                $"You are a trading-bot configurator. Suggest safe, sensible starting parameters for a {botType} bot " +
                "given the market context (price, volatility, 24h range, trend). " +
                "Provide a numeric value for EVERY requested key. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"params\":{<key>:number,...},\"rationale\":string}.",
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

        return ParseResponse(body, _model, paramKeys);
    }

    private static BotParamSuggestion? ParseResponse(string body, string model, IReadOnlyList<string> keys)
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
            if (!root.TryGetProperty("params", out var pObj) || pObj.ValueKind != JsonValueKind.Object)
                return null;

            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
                if (pObj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
                    map[key] = val.GetDecimal();

            if (map.Count == 0) return null;
            var rationale = root.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";
            return new BotParamSuggestion(map, rationale.Trim(), $"Claude {model}", false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record BotParamSuggestion(
    IReadOnlyDictionary<string, decimal> Params,
    string Rationale,
    string Source,
    bool IsFallback);
