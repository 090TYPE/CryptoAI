using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Batch English→Russian translator for UI strings and news headlines, used by
/// <see cref="UiLocalizationService"/> to fill gaps the static dictionary misses.
/// Uses the app's active AI vendor/key via <see cref="AiRuntime"/>. Returns null
/// (caller keeps the English text) whenever no key is configured or the call fails.
/// </summary>
public static class AiUiTranslator
{
    private const string System =
        "You are a professional UI localizer for a crypto trading desktop app. " +
        "Translate each English string to natural, concise Russian suitable for a trading terminal. " +
        "Keep crypto tickers (BTC, ETH, USDT, SOL, ...), exchange names, numbers, percentages, " +
        "wallet addresses and any {placeholders} unchanged. Do not add quotes or commentary. " +
        "Return ONLY a JSON array of strings, exactly the same length and order as the input.";

    public static async Task<IReadOnlyList<string>?> TranslateAsync(
        IReadOnlyList<string> english, CancellationToken ct)
    {
        if (english.Count == 0 || !AiRuntime.IsConfigured)
        {
            return null;
        }

        var user = JsonSerializer.Serialize(english);

        string raw;
        try
        {
            raw = await ChatClient.CompleteTextAsync(
                AiRuntime.ActiveApiKey, AiRuntime.ActiveModel,
                maxTokens: 4000, temperature: 0.0,
                system: System, userContent: user, ct: ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var json = StripFences(raw);
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list is not null && list.Count == english.Count)
            {
                return list;
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    // Strip a leading ```json / ``` fence and trailing ``` if the model wrapped the array.
    private static string StripFences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
        {
            return t;
        }

        var firstNewline = t.IndexOf('\n');
        if (firstNewline >= 0)
        {
            t = t[(firstNewline + 1)..];
        }
        if (t.EndsWith("```", StringComparison.Ordinal))
        {
            t = t[..^3];
        }
        return t.Trim();
    }
}
