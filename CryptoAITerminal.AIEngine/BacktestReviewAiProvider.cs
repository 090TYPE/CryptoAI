using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Reviews a finished backtest and writes a plain-English verdict (robust /
/// promising / weak / overfit) plus key risks. One cheap Claude call, mirroring
/// the other hand-rolled AIEngine providers.
/// </summary>
public sealed class BacktestReviewAiProvider
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public BacktestReviewAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<BacktestReview?> ReviewAsync(BacktestMetrics m, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(m);

        var payload = new
        {
            model       = _model,
            max_tokens  = 400,
            temperature = 0.2,
            system =
                "You are a quantitative strategy reviewer. Judge whether a backtest result is " +
                "trustworthy or likely overfit. Watch for: too few trades, returns that only beat " +
                "buy&hold by luck, sky-high Sharpe on tiny samples, and large drawdowns. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"verdict\":\"ROBUST\"|\"PROMISING\"|\"WEAK\"|\"OVERFIT\",\"summary\":string,\"risks\":[string]}.",
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

    private static string BuildPrompt(BacktestMetrics m)
    {
        var lines = new List<string>
        {
            $"Strategy: {m.StrategyName}",
            $"Net return: {m.NetReturnPct:0.0}%   Buy&Hold: {m.BuyHoldReturnPct:0.0}%   (edge {m.NetReturnPct - m.BuyHoldReturnPct:+0.0;-0.0}%)",
            $"Trades: {m.Trades}   Win rate: {m.WinRatePct:0.0}%",
            $"Max drawdown: {m.MaxDrawdownPct:0.0}%   Sharpe: {m.Sharpe:0.00}",
            $"Best trade: {m.BestTradePct:+0.0}%   Worst: {m.WorstTradePct:0.0}%"
        };
        if (!string.IsNullOrWhiteSpace(m.MonteCarloNote))
            lines.Add($"Monte Carlo: {m.MonteCarloNote}");

        return "Review this backtest:\n" + string.Join('\n', lines) + "\n\nReturn the JSON verdict.";
    }

    private static BacktestReview? ParseResponse(string body, string model)
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
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(summary)) return null;

            var verdict = NormalizeVerdict(root.TryGetProperty("verdict", out var v) ? v.GetString() : null);
            var risks = new List<string>();
            if (root.TryGetProperty("risks", out var rf) && rf.ValueKind == JsonValueKind.Array)
                foreach (var f in rf.EnumerateArray())
                {
                    var rs = f.GetString();
                    if (!string.IsNullOrWhiteSpace(rs)) risks.Add(rs.Trim());
                }

            return new BacktestReview(verdict, summary.Trim(), risks.ToArray(), $"Claude {model}", false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeVerdict(string? raw) => (raw ?? "").Trim().ToUpperInvariant() switch
    {
        "ROBUST"    => "ROBUST",
        "PROMISING" => "PROMISING",
        "OVERFIT"   => "OVERFIT",
        _           => "WEAK"
    };
}

public readonly record struct BacktestMetrics(
    string StrategyName,
    decimal NetReturnPct,
    decimal BuyHoldReturnPct,
    decimal WinRatePct,
    int Trades,
    decimal MaxDrawdownPct,
    decimal Sharpe,
    decimal BestTradePct,
    decimal WorstTradePct,
    string? MonteCarloNote = null);

/// <param name="Verdict">ROBUST | PROMISING | WEAK | OVERFIT</param>
public sealed record BacktestReview(string Verdict, string Summary, string[] Risks, string Source, bool IsFallback);
