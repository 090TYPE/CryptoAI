using System.Net.Http.Json;
using System.Text.Json;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Asks Claude to assess the risk of a freshly-listed token from its
/// on-chain metrics and (optionally) the local security-scan summary.
///
/// Mirrors <see cref="ClaudeSignalProvider"/>: a hand-rolled call to
/// api.anthropic.com /v1/messages so we don't add a transitive NuGet graph
/// and the terminal still builds offline.
/// </summary>
public sealed class TokenSecurityAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public TokenSecurityAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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

        var payload = new
        {
            model       = _model,
            max_tokens  = 320,
            temperature = 0.2,
            system =
                "You are a crypto due-diligence analyst screening freshly-listed DEX/CEX tokens for rug-pull and honeypot risk. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"risk\":0..100,\"verdict\":\"AVOID\"|\"RISKY\"|\"NEUTRAL\"|\"FAVORABLE\",\"red_flags\":[string],\"reason\":string}. " +
                "Higher risk = more dangerous. Be conservative: thin liquidity, parabolic pumps, and fresh deployers are red flags.",
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

    private static TokenAiVerdict? ParseResponse(string body, string model)
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

            var risk = root.TryGetProperty("risk", out var rk) && rk.ValueKind == JsonValueKind.Number
                ? Math.Clamp(rk.GetInt32(), 0, 100)
                : 50;

            var verdict = root.TryGetProperty("verdict", out var vd) ? vd.GetString() : null;
            verdict = NormalizeVerdict(verdict);

            var reason = root.TryGetProperty("reason", out var rsn) ? rsn.GetString() ?? "" : "";

            var flags = new List<string>();
            if (root.TryGetProperty("red_flags", out var rf) && rf.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in rf.EnumerateArray())
                {
                    var s = f.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) flags.Add(s.Trim());
                }
            }

            return new TokenAiVerdict
            {
                RiskScore = risk,
                Verdict   = verdict,
                RedFlags  = flags.ToArray(),
                Reason    = reason,
                Source    = $"Claude {model}",
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
