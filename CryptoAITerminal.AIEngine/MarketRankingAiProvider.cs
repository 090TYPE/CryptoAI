using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Ranks the most promising symbols from a live market-scanner snapshot. One cheap
/// Claude call per refresh, mirroring <see cref="NewsDigestAiProvider"/>'s hand-rolled
/// HTTP so the terminal keeps building offline without an extra NuGet graph.
/// </summary>
public sealed class MarketRankingAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public MarketRankingAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<MarketRankingResult?> RankAsync(
        IReadOnlyList<MarketScanRow> rows,
        int topN = 5,
        CancellationToken ct = default)
    {
        if (rows is null || rows.Count == 0) return null;
        topN = Math.Clamp(topN, 1, 10);

        var prompt = BuildPrompt(rows, topN);

        var payload = new
        {
            model       = _model,
            max_tokens  = 700,
            temperature = 0.2,
            system =
                "You are a crypto market analyst. From a live scanner snapshot, pick the strongest " +
                $"trade candidates RIGHT NOW (at most {topN}). Consider momentum (24h change), liquidity " +
                "(24h volume), RSI extremes (oversold<30 / overbought>70), and tick activity. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"opportunities\":[{\"symbol\":string,\"score\":0..100,\"bias\":\"LONG\"|\"SHORT\"|\"WATCH\",\"reason\":string}]}. " +
                "Higher score = stronger setup. Only include symbols present in the input.",
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

        return ParseResponse(body, _model);
    }

    private static string BuildPrompt(IReadOnlyList<MarketScanRow> rows, int topN)
    {
        var lines = rows
            .Take(40)
            .Select(r =>
                $"{r.Symbol} [{r.Exchange}]  px:{r.LastPrice:0.######}  24h:{r.ChangePct24h:+0.0;-0.0}%  " +
                $"vol:{r.Volume24hUsd:0}  RSI:{r.Rsi14:0}  act:{r.ActivityScore:0}{(r.IsHot ? "  HOT" : "")}");

        return $"Scanner snapshot ({Math.Min(rows.Count, 40)} symbols):\n"
             + string.Join('\n', lines)
             + $"\n\nReturn the top {topN} opportunities as JSON.";
    }

    private static MarketRankingResult? ParseResponse(string body, string model)
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
            {
                text = v.GetString() ?? "";
                break;
            }
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
            if (!root.TryGetProperty("opportunities", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<RankedOpportunity>();
            foreach (var o in arr.EnumerateArray())
            {
                var symbol = o.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(symbol)) continue;
                var score = o.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(sc.GetInt32(), 0, 100) : 50;
                var bias = NormalizeBias(o.TryGetProperty("bias", out var b) ? b.GetString() : null);
                var reason = o.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                list.Add(new RankedOpportunity(symbol.Trim(), score, bias, reason.Trim()));
            }

            if (list.Count == 0) return null;
            return new MarketRankingResult(list, $"Claude {model}", false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeBias(string? raw) => (raw ?? "").Trim().ToUpperInvariant() switch
    {
        "LONG"  => "LONG",
        "SHORT" => "SHORT",
        _       => "WATCH"
    };
}

/// <summary>Compact scanner row handed to the model (decoupled from the UI's ScanResult).</summary>
public readonly record struct MarketScanRow(
    string Symbol,
    string Exchange,
    decimal LastPrice,
    decimal ChangePct24h,
    decimal Volume24hUsd,
    decimal Rsi14,
    decimal ActivityScore,
    bool IsHot);

/// <param name="Bias">LONG | SHORT | WATCH</param>
public sealed record RankedOpportunity(string Symbol, int Score, string Bias, string Reason);

/// <param name="IsFallback">True when produced by the offline heuristic rather than a model call.</param>
public sealed record MarketRankingResult(
    IReadOnlyList<RankedOpportunity> Opportunities,
    string Source,
    bool IsFallback);
