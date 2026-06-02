using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Suggests target portfolio weights for a chosen risk profile from the current
/// holdings. One cheap Claude call; hand-rolled HTTP like the other providers.
/// </summary>
public sealed class PortfolioRebalanceAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public PortfolioRebalanceAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
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

        var payload = new
        {
            model       = _model,
            max_tokens  = 600,
            temperature = 0.2,
            system =
                "You are a crypto portfolio strategist. Propose target weights for the stated risk " +
                "profile: Conservative leans to BTC/ETH and stablecoins; Aggressive allows more alt " +
                "exposure; Balanced sits between. Weights must sum to ~100. Only use symbols present in " +
                "the holdings (you may add USDT as a cash buffer). " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"targets\":[{\"symbol\":string,\"target_pct\":0..100,\"reason\":string}],\"commentary\":string}.",
            messages = new[] { new { role = "user", content = prompt } }
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

    private static RebalancePlan? ParseResponse(string body, string model)
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
            { text = v.GetString() ?? ""; break; }
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
            return new RebalancePlan(targets, commentary.Trim(), $"Claude {model}", false);
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
