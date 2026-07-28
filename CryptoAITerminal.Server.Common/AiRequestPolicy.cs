using System.Text.Json;
using System.Text.Json.Nodes;

namespace CryptoAITerminal.Server.Common;

public enum AiVendor
{
    Anthropic,
    OpenAi,
}

/// <summary>
/// Limits on what a client may ask the server's AI key to pay for. Every value is overridable
/// from configuration; the defaults are deliberately tight.
/// </summary>
public sealed record AiPolicyOptions(
    IReadOnlySet<string> AllowedAnthropicModels,
    IReadOnlySet<string> AllowedOpenAiModels,
    int MaxTokensCap,
    int MaxRequestBytes,
    int MaxMessages,
    long DailyTokenCapPerLicense)
{
    /// <summary>
    /// Built from env/appsettings: AI_ALLOWED_ANTHROPIC_MODELS, AI_ALLOWED_OPENAI_MODELS,
    /// AI_MAX_TOKENS_CAP, AI_MAX_REQUEST_BYTES, AI_MAX_MESSAGES, AI_DAILY_TOKENS_PER_LICENSE.
    /// </summary>
    public static AiPolicyOptions FromConfig(Func<string, string?> cfg)
    {
        static IReadOnlySet<string> Models(string? raw, string[] fallback)
        {
            var list = string.IsNullOrWhiteSpace(raw)
                ? fallback
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }

        static int Int(string? raw, int fallback) => int.TryParse(raw, out var v) && v > 0 ? v : fallback;
        static long Long(string? raw, long fallback) => long.TryParse(raw, out var v) && v > 0 ? v : fallback;

        return new AiPolicyOptions(
            // MUST contain every model an already-shipped terminal sends, or this gate turns into
            // an outage: all ~20 AIEngine providers and ClaudeAgentRunner send claude-sonnet-4-6
            // (AiRuntime.cs:53), and OpenAiAgentRunner sends gpt-4o (AiRuntime.cs:57). The haiku
            // and sonnet-5 entries are for the server's own jobs and for forward compatibility.
            //
            // claude-3-5-haiku-20241022 was removed on 2026-07-27: Anthropic retired it on
            // 2026-02-19, so every request naming it now 404s. It was the default for all 31
            // digests, the token scorer, the anomaly detector and the pre-trade reviewer, which
            // is why those jobs had gone silent. Leaving a retired id in the allow-list only
            // lets a stale client fail slowly at the vendor instead of fast here.
            AllowedAnthropicModels: Models(cfg("AI_ALLOWED_ANTHROPIC_MODELS"), new[]
            {
                "claude-sonnet-4-6",
                "claude-haiku-4-5-20251001",
                "claude-sonnet-5",
            }),
            AllowedOpenAiModels: Models(cfg("AI_ALLOWED_OPENAI_MODELS"), new[]
            {
                "gpt-4o-mini",
                "gpt-4o",
            }),
            // Выше самого длинного законного запроса клиента: панель сигналов просит до 5240 на
            // полном списке символов, агентские циклы — 1024. Зажим молчаливый, поэтому потолок
            // ниже реального потребления не экономит, а портит ответы.
            //
            // Поднято с 2048 после того, как это ровно и произошло, и затем ещё раз — до величины,
            // при которой панель не упирается в него ни на каком размере списка. Расход это не
            // увеличивает: потолок не оплачивается, модель останавливается сама, а вот обрезанный
            // ответ оплачивается целиком и не используется вовсе. Настоящий ограничитель трат —
            // суточная квота лицензии, а не длина одного ответа.
            MaxTokensCap: Int(cfg("AI_MAX_TOKENS_CAP"), 8192),
            MaxRequestBytes: Int(cfg("AI_MAX_REQUEST_BYTES"), 128 * 1024),
            // Must clear a full agent run, not just a single-turn chat: ClaudeAgentRunner /
            // OpenAiAgentRunner append an assistant turn plus a tool_result turn per iteration and
            // allow up to 24 iterations (AgentRunnerFactory clamps there), so a transcript can
            // legitimately reach ~50 messages. A cap of 40 would have killed long tool-use runs
            // mid-loop with a 400.
            MaxMessages: Int(cfg("AI_MAX_MESSAGES"), 64),
            DailyTokenCapPerLicense: Long(cfg("AI_DAILY_TOKENS_PER_LICENSE"), 200_000));
    }
}

public sealed record AiPolicyResult(bool Ok, string? Body, string? Error, string? Model);

public static class AiPolicyOptionsExtensions
{
    /// <summary>
    /// The same limits with a different allow-list, for when the upstream changes at runtime.
    ///
    /// A router names models with a vendor prefix — <c>anthropic/claude-sonnet-4.6</c> rather than
    /// <c>claude-sonnet-4-6</c> — so pointing the proxy at one and forgetting this turns every AI
    /// call into "model is not allowed". Building the replacement here keeps that a settings
    /// change rather than a redeploy.
    /// </summary>
    public static AiPolicyOptions WithAllowedModels(this AiPolicyOptions options,
        string? anthropicCsv, string? openAiCsv)
    {
        static IReadOnlySet<string>? Parse(string? csv) =>
            string.IsNullOrWhiteSpace(csv)
                ? null
                : new HashSet<string>(
                    csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);

        var anthropic = Parse(anthropicCsv);
        var openAi = Parse(openAiCsv);
        if (anthropic is null && openAi is null) return options;

        return options with
        {
            AllowedAnthropicModels = anthropic ?? options.AllowedAnthropicModels,
            AllowedOpenAiModels = openAi ?? options.AllowedOpenAiModels
        };
    }
}

/// <summary>
/// Gate for the AI proxy endpoints. Without it <c>/api/ai/message</c> is an open relay for the
/// server's Anthropic/OpenAI key: the request body was forwarded verbatim, so a client chose the
/// model, the output length and the context size — and the server paid for all three.
///
/// The policy rewrites rather than merely rejects where it safely can (clamping max_tokens,
/// forcing non-streaming), so ordinary client requests keep working and only abuse is refused.
/// </summary>
public static class AiRequestPolicy
{
    /// <summary>
    /// A body that is JSON but has a field of the wrong type is a client mistake, not a server
    /// fault: it must leave here as a 400 from <see cref="AiPolicyResult"/>, never as an exception
    /// escaping into the request pipeline.
    /// </summary>
    /// <param name="overrideModel">
    /// Model the server has assigned to this feature, replacing whatever the client asked for.
    /// Null means no override is configured and the client's choice stands — which is what keeps
    /// terminals shipped before per-feature control existed working unchanged.
    /// </param>
    /// <param name="overrideMaxTokens">Output cap for this feature, still clamped to the global cap.</param>
    /// <param name="modelIsServerChosen">
    /// Модель выбрана сервером, а не прислана клиентом — тогда белый список не проверяется.
    ///
    /// Список существует, чтобы клиент не мог назначить дорогую модель за счёт серверного ключа.
    /// Когда имя пришло из живого списка провайдера или из настройки, которую набрал оператор,
    /// проверять его не по чему: он и есть источник истины, а совпадение с заранее выписанным
    /// перечнем означало бы, что автоподбор работает только на моделях, известных на момент сборки.
    /// </param>
    public static AiPolicyResult Apply(string requestJson, AiPolicyOptions options, AiVendor vendor,
        string? overrideModel = null, int? overrideMaxTokens = null, bool modelIsServerChosen = false)
    {
        try
        {
            return ApplyCore(requestJson, options, vendor, overrideModel, overrideMaxTokens, modelIsServerChosen);
        }
        catch (JsonException)
        {
            return new AiPolicyResult(false, null, "request is not valid JSON", null);
        }
        catch (InvalidOperationException)
        {
            return new AiPolicyResult(false, null, "request has a field of the wrong type", null);
        }
    }

    private static AiPolicyResult ApplyCore(string requestJson, AiPolicyOptions options, AiVendor vendor,
        string? overrideModel, int? overrideMaxTokens, bool modelIsServerChosen = false)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestJson);
        }
        catch (JsonException)
        {
            return new AiPolicyResult(false, null, "request is not valid JSON", null);
        }

        if (root is not JsonObject obj)
            return new AiPolicyResult(false, null, "request must be a JSON object", null);

        // TryGetValue rather than GetValue<T>: the latter throws InvalidOperationException on
        // {"model": 1}, which turned a malformed request into a 500 from the input gate itself.
        string? model = null;
        if (obj["model"] is JsonValue modelNode && modelNode.TryGetValue<string>(out var parsedModel))
            model = parsedModel;

        // The server's assignment wins over the client's. Applied before the allow-list check on
        // purpose: an operator who configures a model that is not permitted should see the request
        // refused here with a clear reason, rather than have it silently fall back to whatever the
        // terminal happened to send — that would look like the setting had been ignored.
        if (!string.IsNullOrWhiteSpace(overrideModel))
            model = overrideModel;

        if (string.IsNullOrWhiteSpace(model))
            return new AiPolicyResult(false, null, "model is required", null);
        obj["model"] = model;

        var allowed = vendor == AiVendor.Anthropic
            ? options.AllowedAnthropicModels
            : options.AllowedOpenAiModels;
        if (!modelIsServerChosen && !allowed.Contains(model))
            return new AiPolicyResult(false, null, $"model '{model}' is not allowed", model);

        if (obj["messages"] is JsonArray messages && messages.Count > options.MaxMessages)
            return new AiPolicyResult(false, null, $"too many messages (max {options.MaxMessages})", model);

        // Clamp the output length. Anthropic requires max_tokens; OpenAI treats it as optional,
        // so an absent value there would mean "model default" — we pin it either way.
        var tokenField = vendor == AiVendor.OpenAi && obj.ContainsKey("max_completion_tokens")
            ? "max_completion_tokens"
            : "max_tokens";
        var requested = options.MaxTokensCap;
        if (obj[tokenField] is JsonValue tokenNode && !tokenNode.TryGetValue<int>(out requested))
            return new AiPolicyResult(false, null, $"{tokenField} must be a whole number", model);
        // A per-feature cap replaces the client's request, then the global cap still bounds it —
        // the operator can shorten an answer but not lift the ceiling the server set for itself.
        if (overrideMaxTokens is { } capped) requested = capped;
        obj[tokenField] = Math.Clamp(requested, 1, options.MaxTokensCap);

        // The proxy buffers the upstream body into a string, so a streaming response would be
        // returned as an unusable SSE blob. Force it off rather than fail late.
        obj["stream"] = false;

        return new AiPolicyResult(true, obj.ToJsonString(), null, model);
    }

    /// <summary>
    /// Tokens actually consumed, read from the vendor's usage block, so the budget is charged for
    /// real spend rather than an estimate. Unparsable or usage-free responses charge nothing.
    /// </summary>
    public static long CountUsage(string responseJson, AiVendor vendor)
    {
        var (input, output) = SplitUsage(responseJson, vendor);
        return input + output;
    }

    /// <summary>
    /// Input and output tokens separately. The budget only needs the sum, but they are priced
    /// differently — output costs five times input on every model here — so a spend report built
    /// on the total alone cannot say what anything actually cost.
    /// </summary>
    public static (long Input, long Output) SplitUsage(string responseJson, AiVendor vendor)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return (0, 0);

            var (inName, outName) = vendor == AiVendor.Anthropic
                ? ("input_tokens", "output_tokens")
                : ("prompt_tokens", "completion_tokens");

            long input = 0, output = 0;
            if (usage.TryGetProperty(inName, out var i) && i.TryGetInt64(out var iv)) input = iv;
            if (usage.TryGetProperty(outName, out var o) && o.TryGetInt64(out var ov)) output = ov;
            return (input, output);
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }
}
