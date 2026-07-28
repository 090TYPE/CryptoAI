using System.Text.Json;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Assesses the risk of a freshly-listed token from its on-chain metrics and
/// (optionally) the local security-scan summary. The vendor call is routed through
/// <see cref="ChatClient"/>, so it uses Claude or ChatGPT per <see cref="AiRuntime.Vendor"/>.
/// </summary>
public sealed class TokenSecurityAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public TokenSecurityAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        // Only fatal when unbound — a bound terminal has no local key and the server holds it.
        if (!ChatClient.CanCallModel(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    /// <param name="securitySummary">
    /// Optional one-line digest of the local security scan (honeypot/mint/tax/etc).
    /// Pass null when no scan is available.
    /// </param>
    public async Task<TokenAiVerdict?> AssessAsync(
        DexTokenInfo token,
        string? securitySummary = null,
        CancellationToken ct = default)
    {
        if (token is null) return null;

        var prompt = BuildPrompt(token, securitySummary);

        var text = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 1200, temperature: 0.2,
            system:
                "You are a crypto due-diligence analyst screening freshly-listed DEX/CEX tokens for rug-pull and honeypot risk. " +
                "Reply ONLY with a single compact JSON object вЂ” no prose, no markdown. " +
                "Schema: {\"risk\":0..100,\"verdict\":\"AVOID\"|\"RISKY\"|\"NEUTRAL\"|\"FAVORABLE\",\"red_flags\":[string],\"reason\":string}. " +
                "Higher risk = more dangerous. Be conservative: thin liquidity, parabolic pumps, and fresh deployers are red flags.",
            userContent: prompt, AiFeatureIds.TokenSecurity, _http, ct).ConfigureAwait(false);

        return ParseResponse(text, _model);
    }

    private static string BuildPrompt(DexTokenInfo t, string? securitySummary)
    {
        var ageMinutes = t.ObservedFirstSeenUtc == DateTime.MinValue
            ? -1
            : (int)Math.Max(0, (DateTime.UtcNow - t.ObservedFirstSeenUtc).TotalMinutes);

        var lines = new List<string>
        {
            $"Name: {t.Name} ({t.Symbol})",
            $"Chain/DEX: {t.ChainId} / {t.DexId}  Quote: {t.QuoteSymbol}",
            $"Price USD: {t.PriceUsd:0.########}",
            $"Liquidity USD: {t.LiquidityUsd:0}",
            $"Volume 24h USD: {t.Volume24h:0}",
            $"Market cap USD: {t.MarketCap:0}",
            $"Price change: 5m {t.PriceChange5m:0.##}%  1h {t.PriceChange1h:0.##}%  24h {t.PriceChange24h:0.##}%",
            $"Observed age: {(ageMinutes < 0 ? "unknown" : ageMinutes + "m")}",
            $"DEX quality: {t.DexQualityLabel}",
            $"Signal: {t.SignalSourceLabel} ({t.SignalConfirmationLabel})"
        };

        if (!string.IsNullOrWhiteSpace(securitySummary))
            lines.Add($"Local security scan: {securitySummary}");

        return "Assess this token:\n" + string.Join('\n', lines) + "\n\nReturn the JSON verdict.";
    }

    private static TokenAiVerdict? ParseResponse(string text, string model)
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

            // Через AiJson: GetInt32() бросает FormatException на дробной оценке, а это не
            // JsonException — уходит мимо catch ниже и уничтожает весь вердикт по токену.
            var risk = (int)Math.Clamp(AiJson.Num(root, "risk", 50m), 0m, 100m);

            var verdict = root.TryGetProperty("verdict", out var vd) ? vd.GetString() : null;
            verdict = NormalizeVerdict(verdict);

            var reason = root.TryGetProperty("reason", out var rsn) ? rsn.GetString() ?? "" : "";

            var flags = new List<string>();
            if (root.TryGetProperty("red_flags", out var rf) && rf.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in rf.EnumerateArray())
                {
                    var s = AiJson.Text(f);
                    if (!string.IsNullOrWhiteSpace(s)) flags.Add(s.Trim());
                }
            }

            return new TokenAiVerdict
            {
                RiskScore = risk,
                Verdict   = verdict,
                RedFlags  = flags.ToArray(),
                Reason    = reason,
                Source    = ChatClient.SourceLabel(AiFeatureIds.TokenSecurity, model),
                IsFallback = false
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeVerdict(string? raw) => (raw ?? "").Trim().ToUpperInvariant() switch
    {
        "AVOID"     => "AVOID",
        "RISKY"     => "RISKY",
        "FAVORABLE" => "FAVORABLE",
        _           => "NEUTRAL"
    };
}
