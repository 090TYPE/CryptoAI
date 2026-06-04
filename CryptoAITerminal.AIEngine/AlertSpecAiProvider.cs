using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Turns a natural-language alert request ("ping me if BTC breaks 75k" or "tell me
/// when ETH drops 10% in an hour") into a structured price-alert spec. The engine
/// stays UI-agnostic: it returns the condition as a string enum name the host maps
/// onto its own AlertCondition. The vendor call goes through <see cref="ChatClient"/>.
/// </summary>
public sealed class AlertSpecAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public AlertSpecAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    /// <param name="conditionNames">Allowed AlertCondition enum names.</param>
    public async Task<AlertAiSpec?> ParseAsync(
        string instruction,
        IReadOnlyList<string> conditionNames,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return null;

        var system =
            "You convert a trader's plain-English request into ONE price alert. " +
            "Condition MUST be exactly one of [" + string.Join(", ", conditionNames) + "] (case-sensitive). " +
            "Symbol is an exchange pair like BTCUSDT (append USDT if the user names a bare coin). " +
            "Threshold is a number: an absolute price for PriceAbove/PriceBelow, a percent for the " +
            "ChangePercent conditions, or a volume multiplier for VolumeSpike. " +
            "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
            "Schema: {\"symbol\":string,\"condition\":string,\"threshold\":number}.";

        var raw = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 200, temperature: 0.0,
            system: system,
            userContent: "Request: " + instruction + "\n\nReturn the JSON alert.",
            _http, ct).ConfigureAwait(false);

        var text = AiJson.StripFences(raw);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var parsed = JsonDocument.Parse(text);
            var root = parsed.RootElement;
            var symbol = AiJson.Str(root, "symbol").Trim().ToUpperInvariant();
            var condition = AiJson.Str(root, "condition").Trim();
            var threshold = AiJson.Num(root, "threshold");
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(condition)) return null;
            return new AlertAiSpec(symbol, condition, threshold, $"{AiRuntime.VendorLabel} {_model}", false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record AlertAiSpec(string Symbol, string Condition, decimal Threshold, string Source, bool IsFallback);
