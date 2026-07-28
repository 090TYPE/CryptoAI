using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// The single vendor-aware seam for one-shot "system + user → assistant text" calls.
/// Every non-agentic AI provider (market insight, ranking, alerts, options, …) builds
/// its own prompt and parses its own JSON, but the wire call in the middle — which
/// endpoint, which headers, which response shape — lives here and branches on
/// <see cref="AiRuntime.Vendor"/>. That is what lets one toggle in Settings reroute
/// every AI feature between Claude and ChatGPT.
///
/// Hand-rolled HTTP (no vendor SDK) to keep the terminal buildable offline, mirroring
/// the original Anthropic-only providers this replaced.
/// </summary>
public static class ChatClient
{
    private const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const string OpenAiEndpoint = "https://api.openai.com/v1/chat/completions";

    // ── Server routing ────────────────────────────────────────────────────────
    // When the app is bound to a CryptoAI server (ServerBaseUrl set at startup from
    // ServerEndpoint), AI calls go to the server's proxy with the license token instead
    // of the vendor API with a client-held key — the key lives only on the server.
    // Left null → direct-to-vendor (unchanged behaviour).
    public static string? ServerBaseUrl { get; set; }
    public static Func<string?>? LicenseTokenProvider { get; set; }

    private static bool UseServer => !string.IsNullOrWhiteSpace(ServerBaseUrl);
    private static string? LicenseToken => LicenseTokenProvider?.Invoke();

    /// <summary>
    /// Whether a real model call is possible right now — the single predicate every AI feature
    /// should gate its offline fallback on.
    ///
    /// It is true when the caller holds a vendor key OR the terminal is bound to a server, because
    /// in the bound case the server holds the key and <see cref="CompleteTextAsync"/> deliberately
    /// accepts an empty <c>apiKey</c>. Testing only for a client-held key — which every
    /// <c>UsesLiveModel</c> used to do — silently downgrades the entire app to heuristics on a
    /// server-bound terminal that has no local key, which is the normal configuration for a
    /// licensed user.
    /// </summary>
    public static bool CanCallModel(string? apiKey) => UseServer || !string.IsNullOrWhiteSpace(apiKey);

    /// <summary>
    /// Sends a single-turn completion and returns the assistant's text (already
    /// extracted from the vendor's response envelope). Throws
    /// <see cref="HttpRequestException"/> on a non-success status so callers can fall
    /// back to their offline heuristic, exactly as the old per-provider code did.
    /// </summary>
    /// <param name="apiKey">Key for the active vendor (callers pass <c>AiRuntime.ActiveApiKey</c> by default).</param>
    /// <param name="model">
    /// Model id for the active vendor. When bound to a server this is a REQUEST, not a decision:
    /// the server substitutes whatever it has assigned to <paramref name="feature"/>.
    /// </param>
    /// <param name="feature">
    /// Which product feature is calling, from <see cref="AiFeatureIds"/>. Sent only in server mode,
    /// where it is what lets an operator route this feature to a different model without a new
    /// build. Null means "do not participate" and the model above stands.
    /// </param>
    public static async Task<string> CompleteTextAsync(
        string apiKey,
        string model,
        int maxTokens,
        double? temperature,
        string system,
        string userContent,
        string? feature,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        // When routing through the server the client needs no key — the server holds it.
        if (!UseServer && string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key is required.", nameof(apiKey));

        var client = http ?? SharedHttp;
        return AiRuntime.Vendor == AiVendor.OpenAi
            ? await OpenAiAsync(client, apiKey, model, maxTokens, temperature, system, userContent, feature, ct).ConfigureAwait(false)
            : await AnthropicAsync(client, apiKey, model, maxTokens, temperature, system, userContent, feature, ct).ConfigureAwait(false);
    }

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ── What the server actually ran ─────────────────────────────────────────
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastModels = new(StringComparer.Ordinal);

    /// <summary>
    /// The model the server reported for this feature on its last call, or null if it has not run
    /// yet. Kept per feature rather than as one "last model" because several panels refresh at
    /// once and a single slot would show whichever finished last.
    ///
    /// Used for the Source label under a result, so it names what produced the answer rather than
    /// what the terminal asked for — which after a server-side switch are different things.
    /// </summary>
    /// <summary>
    /// Семейство, выбранное пользователем, для сервера.
    ///
    /// В серверном режиме терминал не знает и не должен знать, какой именно моделью его обслужат —
    /// это решает сервер по живому списку провайдера. Но выбор между Claude и ChatGPT принадлежит
    /// пользователю: разницу в манере ответа он чувствует, а разницу между версиями одной линейки
    /// оценить не по чему. Заголовок передаёт первое и не трогает второе.
    /// </summary>
    public static string FamilyHeader => AiRuntime.Vendor == AiVendor.OpenAi ? "chatgpt" : "claude";

    /// <summary>
    /// Подпись под результатом: что реально отработало, а если сервер не сказал — что просил сам
    /// терминал.
    ///
    /// Помощник, а не две строки в каждом провайдере, потому что провайдеров четырнадцать: один
    /// пропущенный вызов означает панель, которая показывает выдуманное имя модели, и именно такая
    /// подпись однажды увела поиск неработающего AI совсем в другую сторону. Имя, которое «просил
    /// терминал», к тому же почти всегда неправда — при пустом окружении это зашитая в код
    /// константа, а не чей-либо выбор.
    /// </summary>
    public static string SourceLabel(string feature, string? requestedModel) =>
        LastServerModel(feature) ?? $"{AiRuntime.VendorLabel} {requestedModel}";

    /// <summary>
    /// Последняя модель, о которой сообщил сервер, по любой функции.
    ///
    /// Для мест, где подпись описывает не один вызов, а «чем сейчас обслуживает сервер»: бейдж на
    /// панели, заголовок раздела. Своя функция там может ещё ни разу не вызываться, а брать в этом
    /// случае локальную настройку клиента нельзя — при пустом окружении это зашитая константа, то
    /// есть имя модели, которую никто не выбирал и которая, возможно, вообще не отвечала.
    /// </summary>
    public static string? LastServerModelAny() => LastModels.Values.FirstOrDefault();

    public static string? LastServerModel(string feature) =>
        LastModels.TryGetValue(feature, out var m) ? m : null;

    private static void RecordServerModel(string? feature, HttpResponseMessage res)
    {
        if (string.IsNullOrEmpty(feature)) return;
        if (res.Headers.TryGetValues("X-AI-Model", out var vals))
        {
            var m = vals.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(m)) LastModels[feature] = m;
        }
    }

    // ── Anthropic Messages API ────────────────────────────────────────────────
    private static async Task<string> AnthropicAsync(
        HttpClient http, string apiKey, string model, int maxTokens, double? temperature,
        string system, string userContent, string? feature, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = new[] { new { role = "user", content = userContent } }
        };
        if (temperature is { } t) payload["temperature"] = t;

        var endpoint = UseServer ? ServerBaseUrl!.TrimEnd('/') + "/api/ai/message" : AnthropicEndpoint;
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        if (UseServer)
        {
            var token = LicenseToken;
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.Add("X-License", token);
            // Only in server mode: on a customer's own key there is nobody to interpret it.
            if (!string.IsNullOrEmpty(feature)) req.Headers.Add("X-AI-Feature", feature);
            req.Headers.Add("X-AI-Family", FamilyHeader);
        }
        else
        {
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);
        }

        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        RecordServerModel(feature, res);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw AiCallException.FromResponse("Anthropic", (int)res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var contentArr) ||
            contentArr.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var block in contentArr.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                block.TryGetProperty("text", out var v))
                return v.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    // ── OpenAI Chat Completions API ───────────────────────────────────────────
    private static async Task<string> OpenAiAsync(
        HttpClient http, string apiKey, string model, int maxTokens, double? temperature,
        string system, string userContent, string? feature, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent }
            }
        };
        if (temperature is { } t) payload["temperature"] = t;

        var endpoint = UseServer ? ServerBaseUrl!.TrimEnd('/') + "/api/ai/openai" : OpenAiEndpoint;
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        if (UseServer)
        {
            var token = LicenseToken;
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.Add("X-License", token);
            if (!string.IsNullOrEmpty(feature)) req.Headers.Add("X-AI-Feature", feature);
            // Оба пути обязаны сообщать выбор пользователя. Пропущенный здесь заголовок означал бы,
            // что выбор действует для панелей, ушедших одним форматом, и молча не действует для
            // ушедших другим, — а какой формат сработал, пользователю не видно вовсе.
            req.Headers.Add("X-AI-Family", FamilyHeader);
        }
        else
        {
            req.Headers.Add("Authorization", "Bearer " + apiKey);
        }

        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        RecordServerModel(feature, res);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw AiCallException.FromResponse("OpenAI", (int)res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return string.Empty;

        var msg = choices[0];
        if (msg.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        return string.Empty;
    }
}
