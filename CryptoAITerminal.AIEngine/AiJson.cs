using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// Shared parsing helpers for the hand-rolled Anthropic Messages providers:
/// pulls the first text block out of a /v1/messages response and strips any
/// ```json markdown fences, plus small typed JSON accessors.
/// </summary>
internal static class AiJson
{
    /// <summary>Returns the model's text content (markdown fences stripped), or null.</summary>
    public static string? ExtractText(string body)
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
        return text;
    }

    /// <summary>
    /// Strips a leading/trailing ```json markdown fence from a model's text reply.
    /// Used by providers that now receive the assistant text from <see cref="ChatClient"/>
    /// (already vendor-neutral) rather than a raw Anthropic body.
    /// </summary>
    public static string? StripFences(string? text)
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
        return text.Length == 0 ? null : text;
    }

    public static string Str(JsonElement e, string name, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    public static decimal Num(JsonElement e, string name, decimal fallback = 0m) =>
        // TryGetDecimal (not GetDecimal) so an out-of-range LLM number (e.g. 1.5e30)
        // returns the fallback instead of throwing FormatException past the caller's
        // catch(JsonException) guard.
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)
            ? d : fallback;

    public static bool Bool(JsonElement e, string name, bool fallback = false) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    /// <summary>
    /// Массив строк, терпимый к тому, что модель прислала объекты вместо строк.
    ///
    /// Голый <c>GetString()</c> бросает InvalidOperationException, а не JsonException, поэтому он
    /// пролетал мимо <c>catch (JsonException)</c> у вызывающих и уничтожал весь ответ целиком —
    /// сводку, сигнал и всё остальное — из-за одного пункта не той формы. Форма же плавает
    /// закономерно: соседняя схема в этом же коде объявляет пункты как
    /// <c>{"text":…,"tone":…}</c>, и модель переносит эту привычку.
    ///
    /// Элемент не той формы пропускается, а не роняет разбор: половина списка полезнее пустой панели.
    /// </summary>
    public static string[] StrArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                var s = Text(e);
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
        return list.ToArray();
    }

    /// <summary>Текст из элемента, который может быть строкой или объектом с полем text.</summary>
    public static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Object => Str(e, "text", Str(e, "label", Str(e, "summary"))),
        _ => null,
    };
}
