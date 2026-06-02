using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Scores a statistical-arbitrage pair for tradeability from its spread statistics.
/// One cheap Claude call; hand-rolled HTTP.
/// </summary>
public sealed class StatArbPairAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public StatArbPairAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<StatArbPairVerdict?> EvaluateAsync(StatArbPairStats s, CancellationToken ct = default)
    {
        var prompt =
            $"Pair: {s.SymbolA} / {s.SymbolB}\n" +
            $"Correlation: {s.Correlation:0.00}\n" +
            $"Current z-score: {s.CurrentZScore:0.00}\n" +
            $"Spread half-life: {s.HalfLifeBars} bars\n" +
            $"Entry/Exit z thresholds: {s.EntryZ:0.0} / {s.ExitZ:0.0}\n\n" +
            "Is this pair tradeable now, and which leg? Return the JSON.";

        var payload = new
        {
            model       = _model,
            max_tokens  = 320,
            temperature = 0.2,
            system =
                "You are a stat-arb quant. A pair is tradeable when it is well-correlated and mean-reverting " +
                "(reasonable half-life) and the z-score is stretched beyond the entry threshold. " +
                "Signal LONG_A_SHORT_B when z is very negative (A cheap vs B), SHORT_A_LONG_B when very positive, " +
                "WAIT when inside thresholds, AVOID when correlation/half-life are poor. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"tradeable\":boolean,\"score\":0..100,\"signal\":\"LONG_A_SHORT_B\"|\"SHORT_A_LONG_B\"|\"WAIT\"|\"AVOID\",\"reason\":string}.",
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, DefaultEndpoint) { Content = JsonContent.Create(payload) };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API {(int)res.StatusCode}: {body}");

        var text = AiJson.ExtractText(body);
        if (text is null) return null;
        try
        {
            using var parsed = JsonDocument.Parse(text);
            var r = parsed.RootElement;
            var reason = AiJson.Str(r, "reason");
            if (string.IsNullOrWhiteSpace(reason)) return null;
            return new StatArbPairVerdict(
                AiJson.Bool(r, "tradeable"),
                (int)Math.Clamp(AiJson.Num(r, "score", 50m), 0m, 100m),
                NormalizeSignal(AiJson.Str(r, "signal")),
                reason, $"Claude {_model}", false);
        }
        catch (JsonException) { return null; }
    }

    private static string NormalizeSignal(string? raw) => (raw ?? "").Trim().ToUpperInvariant() switch
    {
        "LONG_A_SHORT_B" => "LONG_A_SHORT_B",
        "SHORT_A_LONG_B" => "SHORT_A_LONG_B",
        "WAIT"           => "WAIT",
        _                => "AVOID"
    };
}

public readonly record struct StatArbPairStats(
    string SymbolA, string SymbolB, decimal Correlation, decimal CurrentZScore,
    int HalfLifeBars, decimal EntryZ, decimal ExitZ);

public sealed record StatArbPairVerdict(bool Tradeable, int Score, string Signal, string Reason, string Source, bool IsFallback);
