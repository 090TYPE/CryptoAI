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
        // AiRuntime.IsConfigured only looks for a local key, which left Russian localization dead
        // on a server-bound terminal: strings silently stayed English with no explanation.
        if (english.Count == 0 || !ChatClient.CanCallModel(AiRuntime.ActiveApiKey))
        {
            return null;
        }

        var user = JsonSerializer.Serialize(english);

        string raw;
        try
        {
            raw = await ChatClient.CompleteTextAsync(
                AiRuntime.ActiveApiKey, AiRuntime.ActiveModel,
                maxTokens: MaxTokensFor(english), temperature: 0.0,
                system: System, userContent: user,
                feature: AiFeatureIds.UiTranslate, ct: ct).ConfigureAwait(false);
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

    /// <summary>
    /// Длина ответа по объёму самой пачки.
    ///
    /// Здесь стояло фиксированное 2000 с объяснением «серверный потолок 2048». Потолок с тех пор
    /// поднят, а число осталось — и превратилось из защиты в единственное ограничение. Ответ
    /// линеен по входу: столько же строк, но по-русски, а кириллица в обоих токенизаторах стоит
    /// примерно вдвое дороже латиницы. Пачка из двадцати строк по паре сотен символов упиралась в
    /// потолок, проверка на совпадение количества строк не проходила, и вся оплаченная пачка
    /// выбрасывалась — молча, оставляя интерфейс английским.
    ///
    /// Считается по символам, а не по числу строк: пачка из двух абзацев дороже двадцати подписей
    /// к кнопкам.
    /// </summary>
    public static int MaxTokensFor(IReadOnlyList<string> english)
    {
        var chars = 0;
        foreach (var s in english) chars += s?.Length ?? 0;
        // Примерно токен на латинский символ после перевода в кириллицу, плюс кавычки и запятые
        // JSON, плюс запас на разброс.
        return Math.Clamp(300 + chars * 2, 600, 6000);
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
