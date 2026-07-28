using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Suggests target portfolio weights for a chosen risk profile from the current
/// holdings. One cheap model call routed through <see cref="ChatClient"/>.
/// </summary>
public sealed class PortfolioRebalanceAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public PortfolioRebalanceAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        // Only fatal when unbound — a bound terminal has no local key and the server holds it.
        if (!ChatClient.CanCallModel(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    /// <param name="riskProfile">"Conservative" | "Balanced" | "Aggressive".</param>
    public async Task<RebalancePlan?> SuggestAsync(
        IReadOnlyList<HoldingRow> holdings,
        string riskProfile,
        CancellationToken ct = default)
    {
        if (holdings is null || holdings.Count == 0) return null;

        var fed = Math.Min(holdings.Count, MaxHoldings);
        var prompt = $"Risk profile: {riskProfile}\nCurrent holdings:\n"
            + string.Join('\n', holdings.Take(MaxHoldings).Select(h => $"- {h.Symbol}: ${h.ValueUsd:0} ({h.CurrentPct:0.0}%)"))
            + "\n\nPropose target weights that sum to ~100%. Return the JSON.";

        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: MaxTokensFor(fed), temperature: 0.2,
            system:
                "You are a crypto portfolio strategist. Propose target weights for the stated risk " +
                "profile: Conservative leans to BTC/ETH and stablecoins; Aggressive allows more alt " +
                "exposure; Balanced sits between. Weights must sum to ~100. Only use symbols present in " +
                "the holdings (you may add USDT as a cash buffer). " +
                "Reply ONLY with a single compact JSON object вЂ” no prose, no markdown. " +
                "Schema: {\"targets\":[{\"symbol\":string,\"target_pct\":0..100,\"reason\":string}],\"commentary\":string}.",
            userContent: prompt, AiFeatureIds.PortfolioRebalance, _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model);
    }

    /// <summary>Сколько позиций отдаётся модели за один проход.</summary>
    public const int MaxHoldings = 30;

    /// <summary>
    /// Длина ответа под число позиций, а не одно число на все случаи.
    ///
    /// Схема требует запись на каждую поданную позицию — символ, вес и причину, — то есть примерно
    /// 40 токенов на штуку плюс общий комментарий. Фиксированные 600 покрывали около пятнадцати:
    /// на портфеле шире ответ обрывался на середине строки, разбор выбрасывал его целиком, и
    /// пользователь видел «модель не вернула пригодных весов» — ровно тот же текст, что и если бы
    /// модель вообще не вызывалась.
    /// </summary>
    public static int MaxTokensFor(int holdingCount) =>
        Math.Min(400 + Math.Clamp(holdingCount, 1, MaxHoldings) * 60, 4000);

    private static RebalancePlan? ParseResponse(string text, string model)
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
            if (!root.TryGetProperty("targets", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var targets = new List<RebalanceTarget>();
            foreach (var o in arr.EnumerateArray())
            {
                var sym = o.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(sym)) continue;
                var pct = o.TryGetProperty("target_pct", out var p) && p.ValueKind == JsonValueKind.Number
                    && p.TryGetDecimal(out var rawPct) ? Math.Clamp(rawPct, 0m, 100m) : 0m;
                var reason = o.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                targets.Add(new RebalanceTarget(sym.Trim().ToUpperInvariant(), pct, reason.Trim()));
            }
            if (targets.Count == 0) return null;

            var commentary = root.TryGetProperty("commentary", out var c) ? c.GetString() ?? "" : "";
            return new RebalancePlan(targets, commentary.Trim(), ChatClient.SourceLabel(AiFeatureIds.PortfolioRebalance, model), false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public readonly record struct HoldingRow(string Symbol, decimal ValueUsd, decimal CurrentPct);
public sealed record RebalanceTarget(string Symbol, decimal TargetPct, string Reason);
public sealed record RebalancePlan(IReadOnlyList<RebalanceTarget> Targets, string Commentary, string Source, bool IsFallback);
