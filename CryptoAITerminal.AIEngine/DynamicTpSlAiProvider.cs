using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Suggests take-profit / stop-loss levels adapted to current volatility and
/// market structure. One cheap model call routed through <see cref="ChatClient"/>.
/// </summary>
public sealed class DynamicTpSlAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public DynamicTpSlAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    public async Task<TpSlSuggestion?> SuggestAsync(TpSlContext ctx, CancellationToken ct = default)
    {
        var prompt =
            $"Symbol: {ctx.Symbol}  Side: {ctx.Side}\n" +
            $"Entry: {ctx.EntryPrice:0.######}  Price: {ctx.CurrentPrice:0.######}\n" +
            $"24h range: {ctx.Range24hPct:0.0}%  ATR%: {ctx.AtrPct:0.00}\n" +
            $"24h change: {ctx.Change24hPct:+0.0;-0.0}%  Trend: {ctx.Trend}\n\n" +
            "Suggest TP%, SL% (both positive, as % from entry) and whether to trail. Return the JSON.";

        var raw = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 300, temperature: 0.2,
            system:
                "You are a risk manager. Set TP/SL adapted to volatility: wider stops in choppy/high-ATR " +
                "markets, tighter in calm ones; keep reward:risk ≥ 1.3. Both values are positive percentages " +
                "from entry. Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"tp_percent\":number,\"sl_percent\":number,\"trailing\":boolean,\"rationale\":string}.",
            userContent: prompt, _http, ct).ConfigureAwait(false);

        var text = AiJson.StripFences(raw);
        if (text is null) return null;
        try
        {
            using var parsed = JsonDocument.Parse(text);
            var r = parsed.RootElement;
            var tp = Math.Clamp(AiJson.Num(r, "tp_percent", 0m), 0.1m, 100m);
            var sl = Math.Clamp(AiJson.Num(r, "sl_percent", 0m), 0.1m, 100m);
            if (tp <= 0m || sl <= 0m) return null;
            return new TpSlSuggestion(tp, sl, AiJson.Bool(r, "trailing"), AiJson.Str(r, "rationale"), $"{AiRuntime.VendorLabel} {_model}", false);
        }
        catch (JsonException) { return null; }
    }
}

public readonly record struct TpSlContext(
    string Symbol, string Side, decimal EntryPrice, decimal CurrentPrice,
    decimal Range24hPct, decimal AtrPct, decimal Change24hPct, string Trend);

public sealed record TpSlSuggestion(decimal TpPercent, decimal SlPercent, bool Trailing, string Rationale, string Source, bool IsFallback);
