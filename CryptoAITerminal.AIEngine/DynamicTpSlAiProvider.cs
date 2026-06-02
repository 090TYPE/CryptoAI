using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Suggests take-profit / stop-loss levels adapted to current volatility and
/// market structure. One cheap Claude call; hand-rolled HTTP.
/// </summary>
public sealed class DynamicTpSlAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public DynamicTpSlAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<TpSlSuggestion?> SuggestAsync(TpSlContext ctx, CancellationToken ct = default)
    {
        var prompt =
            $"Symbol: {ctx.Symbol}  Side: {ctx.Side}\n" +
            $"Entry: {ctx.EntryPrice:0.######}  Price: {ctx.CurrentPrice:0.######}\n" +
            $"24h range: {ctx.Range24hPct:0.0}%  ATR%: {ctx.AtrPct:0.00}\n" +
            $"24h change: {ctx.Change24hPct:+0.0;-0.0}%  Trend: {ctx.Trend}\n\n" +
            "Suggest TP%, SL% (both positive, as % from entry) and whether to trail. Return the JSON.";

        var payload = new
        {
            model       = _model,
            max_tokens  = 300,
            temperature = 0.2,
            system =
                "You are a risk manager. Set TP/SL adapted to volatility: wider stops in choppy/high-ATR " +
                "markets, tighter in calm ones; keep reward:risk ≥ 1.3. Both values are positive percentages " +
                "from entry. Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"tp_percent\":number,\"sl_percent\":number,\"trailing\":boolean,\"rationale\":string}.",
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
            var tp = Math.Clamp(AiJson.Num(r, "tp_percent", 0m), 0.1m, 100m);
            var sl = Math.Clamp(AiJson.Num(r, "sl_percent", 0m), 0.1m, 100m);
            if (tp <= 0m || sl <= 0m) return null;
            return new TpSlSuggestion(tp, sl, AiJson.Bool(r, "trailing"), AiJson.Str(r, "rationale"), $"Claude {_model}", false);
        }
        catch (JsonException) { return null; }
    }
}

public readonly record struct TpSlContext(
    string Symbol, string Side, decimal EntryPrice, decimal CurrentPrice,
    decimal Range24hPct, decimal AtrPct, decimal Change24hPct, string Trend);

public sealed record TpSlSuggestion(decimal TpPercent, decimal SlPercent, bool Trailing, string Rationale, string Source, bool IsFallback);
