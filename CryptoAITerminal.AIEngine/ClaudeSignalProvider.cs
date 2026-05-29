using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Compact, dependency-free Anthropic Messages API client used by
/// <see cref="ClaudeStrategy"/> to score market context.
///
/// We hand-roll the HTTP call instead of pulling Anthropic.SDK so the
/// terminal stays buildable offline and we don't add another transitive
/// NuGet graph. The payload follows api.anthropic.com /v1/messages.
/// </summary>
public sealed class ClaudeSignalProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public ClaudeSignalProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<ClaudeSignal?> GetSignalAsync(string symbol,
        IReadOnlyList<ClaudeCandle> recentCandles,
        CancellationToken ct = default)
    {
        if (recentCandles is null || recentCandles.Count == 0) return null;

        var prompt = BuildPrompt(symbol, recentCandles);

        var payload = new
        {
            model      = _model,
            max_tokens = 256,
            // Keep determinism reasonable — we want a verdict, not creative writing.
            temperature = 0.2,
            system =
                "You are a quantitative crypto trading assistant. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"signal\":\"buy\"|\"sell\"|\"hold\",\"confidence\":0..1,\"reason\":string}.",
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

        return ParseResponse(body);
    }

    private static string BuildPrompt(string symbol, IReadOnlyList<ClaudeCandle> candles)
    {
        var lines = candles
            .TakeLast(20)
            .Select(c => $"{c.Timestamp:HH:mm}  O:{c.Open:0.######} H:{c.High:0.######} L:{c.Low:0.######} C:{c.Close:0.######} V:{c.Volume:0.##}");

        return $"Symbol: {symbol}\nLast {Math.Min(candles.Count, 20)} candles (UTC):\n"
             + string.Join('\n', lines)
             + "\n\nReturn the JSON verdict.";
    }

    private static ClaudeSignal? ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var contentArr) ||
            contentArr.ValueKind != JsonValueKind.Array || contentArr.GetArrayLength() == 0)
            return null;

        // Anthropic returns content blocks; the model is instructed to send JSON in text[0].
        string text = "";
        foreach (var block in contentArr.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                block.TryGetProperty("text", out var v))
            {
                text = v.GetString() ?? "";
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Defensive: the model may still wrap JSON in markdown fences.
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }

        try
        {
            using var parsed = JsonDocument.Parse(text);
            var root = parsed.RootElement;
            var signal = root.TryGetProperty("signal", out var s) ? s.GetString() : null;
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetDecimal() : 0m;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(signal)) return null;

            var normalized = signal.Trim().ToUpperInvariant() switch
            {
                "BUY"  => "BUY",
                "SELL" => "SELL",
                _      => "HOLD"
            };
            return new ClaudeSignal(normalized, Math.Clamp(confidence, 0m, 1m), reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public readonly record struct ClaudeCandle(
    DateTime Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record ClaudeSignal(string Signal, decimal Confidence, string Reason);
