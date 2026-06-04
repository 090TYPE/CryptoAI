using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Plans how to slice a large order (TWAP/VWAP-style) to limit market impact.
/// One cheap model call routed through <see cref="ChatClient"/>.
/// </summary>
public sealed class ExecutionScheduleAiProvider
{
    private readonly HttpClient? _http;
    private readonly string _apiKey;
    private readonly string _model;

    public ExecutionScheduleAiProvider(string apiKey, string? model = null, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model  = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _http   = http;
    }

    public async Task<ExecutionPlan?> PlanAsync(OrderExecutionContext ctx, CancellationToken ct = default)
    {
        var prompt =
            $"Order: {ctx.Side} ${ctx.TotalUsd:0} of {ctx.Symbol}\n" +
            $"24h volume: ${ctx.Volume24hUsd:0}  Top-of-book depth: ${ctx.BookDepthUsd:0}\n" +
            $"Urgency: {ctx.Urgency}\n\n" +
            "Plan a slice schedule that limits market impact. Return the JSON.";

        var raw = await ChatClient.CompleteTextAsync(
            _apiKey, _model, maxTokens: 320, temperature: 0.2,
            system:
                "You are an execution trader. Slice a large order to keep each child small vs available " +
                "liquidity (rule of thumb: a child should be a small fraction of top-of-book depth). More " +
                "slices and longer intervals for large-vs-liquidity orders; fewer/faster when urgent. " +
                "Reply ONLY with a single compact JSON object — no prose, no markdown. " +
                "Schema: {\"slices\":int,\"interval_seconds\":int,\"slice_usd\":number,\"rationale\":string}.",
            userContent: prompt, _http, ct).ConfigureAwait(false);

        var text = AiJson.StripFences(raw);
        if (text is null) return null;
        try
        {
            using var parsed = JsonDocument.Parse(text);
            var r = parsed.RootElement;
            var slices = (int)Math.Clamp(AiJson.Num(r, "slices", 0m), 1m, 500m);
            var interval = (int)Math.Clamp(AiJson.Num(r, "interval_seconds", 0m), 1m, 86400m);
            if (slices <= 0) return null;
            var sliceUsd = AiJson.Num(r, "slice_usd", 0m);
            if (sliceUsd <= 0m && slices > 0) sliceUsd = Math.Round(ctx.TotalUsd / slices, 2);
            return new ExecutionPlan(slices, interval, sliceUsd, AiJson.Str(r, "rationale"), $"{AiRuntime.VendorLabel} {_model}", false);
        }
        catch (JsonException) { return null; }
    }
}

public readonly record struct OrderExecutionContext(
    string Symbol, string Side, decimal TotalUsd, decimal Volume24hUsd, decimal BookDepthUsd, string Urgency);

public sealed record ExecutionPlan(int Slices, int IntervalSeconds, decimal SliceUsd, string Rationale, string Source, bool IsFallback);
