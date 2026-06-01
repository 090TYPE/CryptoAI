using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Produces a short market-pulse digest from recent headlines. Uses Claude when
/// an API key is configured, otherwise a deterministic offline summary built
/// from the keyword-sentiment tallies — so the dashboard always shows something,
/// including in the demo flow without keys.
/// </summary>
public sealed class NewsAiSummaryService
{
    public string ApiKey { get; set; } =
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        ?? Environment.GetEnvironmentVariable("CRYPTOAI_CLAUDE_KEY")
        ?? string.Empty;

    public string Model { get; set; } = "claude-sonnet-4-6";

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<NewsDigest> SummarizeAsync(
        IReadOnlyList<string> headlines,
        int bullish,
        int bearish,
        int neutral,
        CancellationToken ct = default)
    {
        if (UsesLiveModel && headlines.Count > 0)
        {
            try
            {
                var provider = new NewsDigestAiProvider(ApiKey, Model);
                var digest = await provider.SummarizeAsync(headlines, ct).ConfigureAwait(false);
                if (digest is not null) return digest;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Degrade silently to the offline digest.
            }
        }

        return BuildOffline(headlines, bullish, bearish, neutral);
    }

    private static NewsDigest BuildOffline(
        IReadOnlyList<string> headlines, int bullish, int bearish, int neutral)
    {
        var total = bullish + bearish + neutral;
        if (total == 0)
            return new NewsDigest("No fresh headlines in the last hour.", "NEUTRAL", "Heuristic (offline)", true);

        var bias = bullish > bearish * 1.3 && bullish >= 2 ? "BULLISH"
                 : bearish > bullish * 1.3 && bearish >= 2 ? "BEARISH"
                 : "NEUTRAL";

        var lean = bias switch
        {
            "BULLISH" => "lean bullish",
            "BEARISH" => "lean bearish",
            _         => "are mixed"
        };

        var top = headlines.Count > 0 ? headlines[0] : null;
        var summary = $"{total} headlines in the last hour {lean} ({bullish} bullish / {bearish} bearish / {neutral} neutral)."
            + (top is not null ? $" Top: “{Truncate(top, 90)}”." : string.Empty);

        return new NewsDigest(summary, bias, "Heuristic (offline)", true);
    }

    private static string Truncate(string t, int max) =>
        t.Length <= max ? t : t[..max] + "…";
}
