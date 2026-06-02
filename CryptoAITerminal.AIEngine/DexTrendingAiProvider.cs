using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Ranks trending DEX tokens for momentum vs rug risk. One cheap Claude call;
/// hand-rolled HTTP like the other AIEngine providers.
/// </summary>
public sealed class DexTrendingAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public DexTrendingAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<DexTrendingResult?> RankAsync(IReadOnlyList<DexTrendRow> rows, int topN = 5, CancellationToken ct = default)
    {
        if (rows is null || rows.Count == 0) return null;
        topN = Math.Clamp(topN, 1, 10);

        var prompt = $"Trending DEX tokens:\n" + string.Join('\n', rows.Take(40).Select(r =>
            $"{r.Symbol} ({r.Address})  px:${r.PriceUsd:0.######}  5m:{r.Change5m:+0.0;-0.0}%  1h:{r.Change1h:+0.0;-0.0}%  24h:{r.Change24h:+0.0;-0.0}%  liq:${r.LiquidityUsd:0}  vol:${r.Volume24h:0}  mcap:${r.MarketCap:0}"))
            + $"\n\nRank the top {topN}. Return the JSON.";

        var payload = new
        {
            model       = _model,
            max_tokens  = 700,
            temperature = 0.2,
            system =
                "You are a DEX momentum analyst. Rank tokens by genuine momentum while penalising rug risk " +
                "(thin liquidity, parabolic-then-fading, volume >> liquidity churn). " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"tokens\":[{\"symbol\":string,\"address\":string,\"score\":0..100,\"signal\":\"MOMENTUM\"|\"EARLY\"|\"FADING\"|\"AVOID\",\"reason\":string}]}.",
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, DefaultEndpoint) { Content = JsonContent.Create(payload) };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API {(int)res.StatusCode}: {body}");

        return Parse(body, _model);
    }

    private static DexTrendingResult? Parse(string body, string model)
    {
        var text = AiJson.ExtractText(body);
        if (text is null) return null;
        try
        {
            using var parsed = JsonDocument.Parse(text);
            var root = parsed.RootElement;
            if (!root.TryGetProperty("tokens", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

            var list = new List<DexTrendPick>();
            foreach (var o in arr.EnumerateArray())
            {
                var sym = AiJson.Str(o, "symbol");
                if (string.IsNullOrWhiteSpace(sym)) continue;
                var score = (int)Math.Clamp(AiJson.Num(o, "score", 50m), 0m, 100m);
                var signal = NormalizeSignal(AiJson.Str(o, "signal"));
                list.Add(new DexTrendPick(sym, AiJson.Str(o, "address"), score, signal, AiJson.Str(o, "reason")));
            }
            return list.Count == 0 ? null : new DexTrendingResult(list, $"Claude {model}", false);
        }
        catch (JsonException) { return null; }
    }

    private static string NormalizeSignal(string? raw) => (raw ?? "").Trim().ToUpperInvariant() switch
    {
        "MOMENTUM" => "MOMENTUM",
        "EARLY"    => "EARLY",
        "FADING"   => "FADING",
        _          => "AVOID"
    };
}

public readonly record struct DexTrendRow(
    string Symbol, string Address, decimal PriceUsd,
    decimal Change5m, decimal Change1h, decimal Change24h,
    decimal LiquidityUsd, decimal Volume24h, decimal MarketCap);

public sealed record DexTrendPick(string Symbol, string Address, int Score, string Signal, string Reason);
public sealed record DexTrendingResult(IReadOnlyList<DexTrendPick> Tokens, string Source, bool IsFallback);
