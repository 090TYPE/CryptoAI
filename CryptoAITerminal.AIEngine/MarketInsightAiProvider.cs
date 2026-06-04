using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Domain-agnostic "interpret raw data → one-paragraph narrative + a labelled signal
/// + a few bullets" provider. Reused by whale-flow, on-chain, sentiment and
/// liquidation insight services — the caller supplies the role, the data lines and
/// the allowed signal vocabulary. The vendor call goes through <see cref="ChatClient"/>.
/// </summary>
public sealed class MarketInsightAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public MarketInsightAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    /// <param name="roleSentence">e.g. "You are an on-chain whale-flow analyst."</param>
    /// <param name="dataLines">Pre-formatted, privacy-safe data lines.</param>
    /// <param name="signalVocabulary">Allowed values for the "signal" field, e.g. ["ACCUMULATION","DISTRIBUTION","NEUTRAL"].</param>
    public async Task<InsightResult?> InterpretAsync(
        string roleSentence,
        IReadOnlyList<string> dataLines,
        IReadOnlyList<string> signalVocabulary,
        CancellationToken ct = default)
    {
        if (dataLines is null || dataLines.Count == 0) return null;
        var vocab = string.Join("\"|\"", signalVocabulary);

        var system =
            roleSentence + " " +
            "Read the data and explain what it means in ONE or TWO sentences, give a signal label, " +
            "and list a few concrete observations. " +
            "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
            "Schema: {\"summary\":string,\"signal\":\"" + vocab + "\",\"bullets\":[string]}.";

        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 400, temperature: 0.3,
            system: system,
            userContent: string.Join('\n', dataLines.Take(40)) + "\n\nReturn the JSON.",
            _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model, signalVocabulary);
    }

    private static InsightResult? ParseResponse(string text, string model, IReadOnlyList<string> vocab)
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

            var rawSignal = root.TryGetProperty("signal", out var sg) ? sg.GetString() ?? "" : "";
            var signal = vocab.FirstOrDefault(x => string.Equals(x, rawSignal?.Trim(), StringComparison.OrdinalIgnoreCase))
                         ?? (vocab.Count > 0 ? vocab[^1] : "NEUTRAL");

            var bullets = new List<string>();
            if (root.TryGetProperty("bullets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    var b = e.GetString();
                    if (!string.IsNullOrWhiteSpace(b)) bullets.Add(b.Trim());
                }

            return new InsightResult(summary.Trim(), signal, bullets.ToArray(), $"{AiRuntime.VendorLabel} {model}", false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <param name="Signal">One of the caller-supplied vocabulary values.</param>
public sealed record InsightResult(string Summary, string Signal, string[] Bullets, string Source, bool IsFallback);
