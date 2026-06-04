using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Suggests starting parameters for a Grid or DCA bot from current market context.
/// The caller lists the parameter keys it wants values for; the model returns a
/// numeric map plus a rationale. The vendor call goes through <see cref="ChatClient"/>.
/// </summary>
public sealed class BotParameterAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public BotParameterAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
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

        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 400, temperature: 0.2,
            system:
                $"You are a trading-bot configurator. Suggest safe, sensible starting parameters for a {botType} bot " +
                "given the market context (price, volatility, 24h range, trend). " +
                "Provide a numeric value for EVERY requested key. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"params\":{<key>:number,...},\"rationale\":string}.",
            userContent: prompt, _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model, paramKeys);
    }

    private static BotParamSuggestion? ParseResponse(string text, string model, IReadOnlyList<string> keys)
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
            if (!root.TryGetProperty("params", out var pObj) || pObj.ValueKind != JsonValueKind.Object)
                return null;

            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
                if (pObj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
                    map[key] = val.GetDecimal();

            if (map.Count == 0) return null;
            var rationale = root.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";
            return new BotParamSuggestion(map, rationale.Trim(), $"{AiRuntime.VendorLabel} {model}", false);
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
