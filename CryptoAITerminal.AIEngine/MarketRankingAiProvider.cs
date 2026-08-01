using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Ranks the most promising symbols from a live market-scanner snapshot. One cheap
/// model call per refresh, routed through <see cref="ChatClient"/> to the active vendor.
/// </summary>
public sealed class MarketRankingAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    /// <summary>
    /// Длина ответа по числу мест в рейтинге: на каждое идёт строка с обоснованием.
    /// Верхняя граница обязана совпадать с каталогом сервера (AiFeatures.All) — это держит тест.
    /// </summary>
    public static int MaxTokensFor(int topN) => Math.Clamp(400 + Math.Max(0, topN) * 110, 800, MaxTokens);

    /// <summary>Самый длинный ответ, который панель может запросить.</summary>
    public const int MaxTokens = 1800;

    public MarketRankingAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        // Only fatal when unbound — a bound terminal has no local key and the server holds it.
        if (!ChatClient.CanCallModel(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    public async Task<MarketRankingResult?> RankAsync(
        IReadOnlyList<MarketScanRow> rows,
        int topN = 5,
        CancellationToken ct = default)
    {
        if (rows is null || rows.Count == 0) return null;
        topN = Math.Clamp(topN, 1, 10);

        var prompt = BuildPrompt(rows, topN);

        var text = await ChatClient.CompleteTextAsync(
            // По числу запрошенных позиций: у каждой своя причина словами, и на topN=10 фикс в 700
            // упирался в потолок — а обрезанный JSON здесь выбрасывается целиком.
            _apiKey, _model, maxTokens: MaxTokensFor(topN), temperature: 0.2,
            system:
                "You are a crypto market analyst. From a live scanner snapshot, pick the strongest " +
                $"trade candidates RIGHT NOW (at most {topN}). Consider momentum (24h change), liquidity " +
                "(24h volume), RSI extremes (oversold<30 / overbought>70), and tick activity. " +
                "Reply ONLY with a single compact JSON object вЂ” no prose, no markdown. " +
                "Schema: {\"opportunities\":[{\"symbol\":string,\"score\":0..100,\"bias\":\"LONG\"|\"SHORT\"|\"WATCH\",\"reason\":string}]}. " +
                "Higher score = stronger setup. Only include symbols present in the input.",
            userContent: prompt, AiFeatureIds.MarketRanking, _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model);
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

    private static MarketRankingResult? ParseResponse(string text, string model)
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
            if (!root.TryGetProperty("opportunities", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<RankedOpportunity>();
            foreach (var o in arr.EnumerateArray())
            {
                var symbol = o.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(symbol)) continue;
                // GetInt32() бросает FormatException на дробном числе — 87.5 это тоже Number, — а
                // FormatException не JsonException и уходит мимо catch ниже, уничтожая весь
                // ранжированный список. В схеме «0..100» дробь ничем не запрещена.
                var score = (int)Math.Clamp(AiJson.Num(o, "score", 50m), 0m, 100m);
                var bias = NormalizeBias(o.TryGetProperty("bias", out var b) ? b.GetString() : null);
                var reason = o.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                list.Add(new RankedOpportunity(symbol.Trim(), score, bias, reason.Trim()));
            }

            if (list.Count == 0) return null;
            return new MarketRankingResult(list, ChatClient.SourceLabel(AiFeatureIds.MarketRanking, model), false);
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
