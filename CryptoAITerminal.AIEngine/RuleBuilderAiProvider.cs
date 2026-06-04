using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Turns a natural-language instruction ("buy the dip on BTC when RSI drops below
/// 30 and notify me") into a structured automation-rule spec. The engine stays
/// UI-agnostic: it returns string-typed enum names that the host maps onto its own
/// CompositeRule model. The vendor call goes through <see cref="ChatClient"/>.
/// </summary>
public sealed class RuleBuilderAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public RuleBuilderAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    /// <param name="conditionTypes">Allowed ConditionType enum names.</param>
    /// <param name="actionTypes">Allowed ActionType enum names.</param>
    /// <param name="cooldowns">Allowed RuleCooldown enum names.</param>
    public async Task<AiRuleSpec?> BuildAsync(
        string instruction,
        IReadOnlyList<string> conditionTypes,
        IReadOnlyList<string> actionTypes,
        IReadOnlyList<string> cooldowns,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return null;

        var system =
            "You convert a trader's plain-English instruction into ONE automation rule. " +
            "Map each clause onto the allowed enum values EXACTLY (case-sensitive). " +
            "ConditionType ∈ [" + string.Join(", ", conditionTypes) + "]. " +
            "ActionType ∈ [" + string.Join(", ", actionTypes) + "]. " +
            "RuleCooldown ∈ [" + string.Join(", ", cooldowns) + "]. " +
            "For each condition: Symbol (e.g. BTCUSDT or ANY), Param1 (period or threshold), " +
            "Param2 (threshold or multiplier). For RSI conditions Param1=period(14), Param2=level. " +
            "For Price24hChange conditions Param1=percent. For actions: Symbol, Amount (USD), Message. " +
            "Logic ∈ [And, Or]. Reply ONLY with a single compact JSON object — no prose, no markdown. " +
            "Schema: {\"name\":string,\"logic\":\"And\"|\"Or\",\"cooldown\":string," +
            "\"conditions\":[{\"type\":string,\"symbol\":string,\"param1\":number,\"param2\":number}]," +
            "\"actions\":[{\"type\":string,\"symbol\":string,\"amount\":number,\"message\":string}]}.";

        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 600, temperature: 0.1,
            system: system,
            userContent: "Instruction: " + instruction + "\n\nReturn the JSON rule.",
            _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model);
    }

    private static AiRuleSpec? ParseResponse(string text, string model)
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

            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "AI Rule" : "AI Rule";
            var logic = root.TryGetProperty("logic", out var lg) ? lg.GetString() ?? "And" : "And";
            var cooldown = root.TryGetProperty("cooldown", out var cd) ? cd.GetString() ?? "Minutes5" : "Minutes5";

            var conditions = new List<AiRuleCondition>();
            if (root.TryGetProperty("conditions", out var ca) && ca.ValueKind == JsonValueKind.Array)
                foreach (var c in ca.EnumerateArray())
                    conditions.Add(new AiRuleCondition(
                        StrProp(c, "type"),
                        StrProp(c, "symbol", "BTCUSDT"),
                        NumProp(c, "param1", 14m),
                        NumProp(c, "param2", 30m)));

            var actions = new List<AiRuleAction>();
            if (root.TryGetProperty("actions", out var aa) && aa.ValueKind == JsonValueKind.Array)
                foreach (var a in aa.EnumerateArray())
                    actions.Add(new AiRuleAction(
                        StrProp(a, "type"),
                        StrProp(a, "symbol", "BTCUSDT"),
                        NumProp(a, "amount", 50m),
                        StrProp(a, "message")));

            if (conditions.Count == 0 && actions.Count == 0) return null;

            return new AiRuleSpec(name.Trim(), logic.Trim(), cooldown.Trim(),
                conditions.ToArray(), actions.ToArray(), $"{AiRuntime.VendorLabel} {model}", false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StrProp(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static decimal NumProp(JsonElement e, string name, decimal fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : fallback;
}

public readonly record struct AiRuleCondition(string Type, string Symbol, decimal Param1, decimal Param2);
public readonly record struct AiRuleAction(string Type, string Symbol, decimal Amount, string Message);

public sealed record AiRuleSpec(
    string Name,
    string Logic,
    string Cooldown,
    AiRuleCondition[] Conditions,
    AiRuleAction[] Actions,
    string Source,
    bool IsFallback);
