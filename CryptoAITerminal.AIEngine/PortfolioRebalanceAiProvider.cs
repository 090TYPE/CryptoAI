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
        if (string.IsNullOrWhiteSpace(apiKey))
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

        var prompt = $"Risk profile: {riskProfile}\nCurrent holdings:\n"
            + string.Join('\n', holdings.Take(30).Select(h => $"- {h.Symbol}: ${h.ValueUsd:0} ({h.CurrentPct:0.0}%)"))
            + "\n\nPropose target weights that sum to ~100%. Return the JSON.";

        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 600, temperature: 0.2,
            system:
                "You are a crypto portfolio strategist. Propose target weights for the stated risk " +
                "profile: Conservative leans to BTC/ETH and stablecoins; Aggressive allows more alt " +
                "exposure; Balanced sits between. Weights must sum to ~100. Only use symbols present in " +
                "the holdings (you may add USDT as a cash buffer). " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"targets\":[{\"symbol\":string,\"target_pct\":0..100,\"reason\":string}],\"commentary\":string}.",
            userContent: prompt, _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model);
    }

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
                    ? Math.Clamp(p.GetDecimal(), 0m, 100m) : 0m;
                var reason = o.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                targets.Add(new RebalanceTarget(sym.Trim().ToUpperInvariant(), pct, reason.Trim()));
            }
            if (targets.Count == 0) return null;

            var commentary = root.TryGetProperty("commentary", out var c) ? c.GetString() ?? "" : "";
            return new RebalancePlan(targets, commentary.Trim(), $"{AiRuntime.VendorLabel} {model}", false);
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
