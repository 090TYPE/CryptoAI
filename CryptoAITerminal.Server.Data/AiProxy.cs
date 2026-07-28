using System.Text;
using System.Text.Json;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Forwards AI chat requests upstream with the SERVER-held key, so the key never ships to clients.
/// Request and response bodies pass through verbatim.
///
/// The upstream is configurable. It was hardwired to api.anthropic.com and api.openai.com, which
/// ruled out every OpenAI-compatible router — the class of service that resells the same models
/// cheaper, or reaches them from a region where the vendor is blocked. Switching to one is now a
/// settings change, not a code change, because the wire format is identical: a router that speaks
/// /v1/messages is indistinguishable from Anthropic from this side.
///
/// Two things vary between an upstream and the vendor it imitates, and both are settings:
/// the URL, and whether the key goes in x-api-key (Anthropic's own scheme) or in an Authorization
/// bearer header (what every router uses).
/// </summary>
public sealed class AiProxy
{
    // Kept as aliases so existing call sites and tests keep working; the values live in
    // AiProxyDefaults, which the admin surface also reads.
    public const string DefaultAnthropicUrl = AiProxyDefaults.Anthropic;
    public const string DefaultOpenAiUrl = AiProxyDefaults.OpenAi;

    /// <summary>
    /// Насколько живёт список моделей провайдера. Он меняется от силы раз в недели, а спрашивать
    /// его на каждый вызов означало бы удваивать число обращений к чужому сервису.
    /// </summary>
    public static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(10);

    /// <summary>Пауза перед повтором после неудачи — короче, чтобы починка провайдера подхватилась быстро.</summary>
    public static readonly TimeSpan CatalogRetryTtl = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly ProviderKeyStore _keys;
    private readonly SettingsStore? _settings;
    private readonly string? _anthropicEnv;
    private readonly string? _openAiEnv;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private (DateTime At, AiFamily Family, IReadOnlyList<string> Ids, TimeSpan Ttl)? _catalog;

    public AiProxy(HttpClient http, ProviderKeyStore keys, string? anthropicEnv = null,
        string? openAiEnv = null, SettingsStore? settings = null)
    {
        _http = http;
        _keys = keys;
        _anthropicEnv = anthropicEnv;
        _openAiEnv = openAiEnv;
        _settings = settings;
    }

    /// <summary>
    /// Forward a Messages-API request. Returns null when no key is configured (→ 503 at the API).
    ///
    /// Когда сервер настроен на семейство ChatGPT, запрос переводится в формат Chat Completions и
    /// уходит на openai-адрес, а ответ переводится обратно. Иначе переключатель семейства менял бы
    /// только имя модели и получал 400: формат запроса зашит в клиенте, и все ~20 провайдеров
    /// терминала вместе с фоновыми задачами говорят в формате Anthropic.
    /// </summary>
    public async Task<(int Status, string Body)?> ForwardAnthropicAsync(string requestJson, CancellationToken ct = default)
    {
        if (await FamilyAsync(ct) == AiFamily.ChatGpt)
        {
            var translated = AiFormatBridge.RequestToOpenAi(requestJson, AiFormatBridge.ModelOf(requestJson) ?? "");
            if (translated is null)
                return (400, """{"error":{"message":"request could not be translated"}}""");

            var relayed = await ForwardOpenAiAsync(translated, ct);
            if (relayed is null) return null;

            // Переводится только успешный ответ. Тело ошибки уходит наверх как есть: оно попадает в
            // лог и в сообщение оператору, и обёрнутое в чужую форму диагностируется хуже.
            return relayed.Value.Status == 200
                ? (200, AiFormatBridge.ResponseToAnthropic(relayed.Value.Body))
                : relayed.Value;
        }

        var key = await _keys.GetAsync("anthropic", ct) ?? _anthropicEnv;
        if (string.IsNullOrWhiteSpace(key)) return null;

        var url = await SettingAsync(SettingKeys.AnthropicBaseUrl, DefaultAnthropicUrl, ct);
        var bearer = await BearerAsync(SettingKeys.AnthropicAuthBearer, url, DefaultAnthropicUrl, ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        if (bearer)
        {
            req.Headers.Add("Authorization", "Bearer " + key);
        }
        else
        {
            req.Headers.Add("x-api-key", key);
            // Only Anthropic itself requires this, and routers reject unknown headers often enough
            // that sending it unconditionally is not free.
            req.Headers.Add("anthropic-version", "2023-06-01");
        }

        using var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Forward a Chat-Completions request. Returns null when no key is configured.</summary>
    public async Task<(int Status, string Body)?> ForwardOpenAiAsync(string requestJson, CancellationToken ct = default)
    {
        var url = await SettingAsync(SettingKeys.OpenAiBaseUrl, DefaultOpenAiUrl, ct);

        var key = await _keys.GetAsync("openai", ct) ?? _openAiEnv;
        if (string.IsNullOrWhiteSpace(key) && AiProxyDefaults.LooksLikeRouter(url, DefaultOpenAiUrl))
        {
            // На роутере оба формата обслуживает один ключ, и класть его дважды под двумя именами —
            // ровно тот способ настроить всё правильно и получить отказ, от которого этот класс и
            // избавляется. Подстановка ограничена случаем, когда адрес не вендорский: ключ Anthropic
            // не должен уехать на api.openai.com ни при каких настройках.
            key = await _keys.GetAsync("anthropic", ct) ?? _anthropicEnv;
        }

        if (string.IsNullOrWhiteSpace(key)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        // Chat Completions is a bearer protocol everywhere, vendor and router alike.
        req.Headers.Add("Authorization", "Bearer " + key);

        using var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Семейство, которое обслуживает сервер. Единственный выбор, оставленный человеку.</summary>
    public async Task<AiFamily> FamilyAsync(CancellationToken ct = default) =>
        AiModelCatalog.ParseFamily(_settings is null
            ? null
            : await _settings.GetAsync(SettingKeys.Family, null, ct));

    /// <summary>
    /// Какой моделью обслуживать вызов. Порядок: своя настройка вызова → автоподбор по живому списку
    /// провайдера → модель по умолчанию для семейства → окружение → константа в коде.
    ///
    /// Автоподбор стоит выше окружения и констант намеренно. Имена в .env и в коде вендорские
    /// (<c>claude-haiku-4-5-20251001</c>), у роутера они с префиксом, и после переключения провайдера
    /// именно они бы перебили выбор и обрушили все фоновые задачи молча — задача пишет предупреждение
    /// в лог и возвращает ноль, в админке при этом всё выглядит переключённым.
    ///
    /// Для терминальных вызовов последним значением стоит null, а не константа: null означает
    /// «оставить то, что прислал клиент». Подставить сюда фоновую модель значило бы незаметно
    /// перевести весь терминал на самую дешёвую.
    /// </summary>
    public async Task<string?> ModelForAsync(string? ownKey, AiModelRole role, string? envModel,
        CancellationToken ct = default)
    {
        if (_settings is not null && !string.IsNullOrWhiteSpace(ownKey))
        {
            var pinned = await _settings.GetAsync(ownKey, null, ct);
            if (!string.IsNullOrWhiteSpace(pinned)) return pinned;
        }

        var family = await FamilyAsync(ct);

        if (_settings is null || await _settings.GetBoolAsync(SettingKeys.ModelAuto, true, ct))
        {
            var picked = AiModelCatalog.Pick(await CatalogAsync(family, ct), family, role);
            if (picked is not null) return picked;
        }

        if (_settings is not null)
        {
            var configured = await _settings.GetAsync(
                SettingKeys.DefaultModel(family == AiFamily.Claude), null, ct);
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
        }

        if (role != AiModelRole.Background) return null;

        // Переменная окружения относится к claude-задачам: она писалась при развёртывании, когда
        // другого семейства не было. Для ChatGPT она бессмысленна, и вендорское имя оттуда — прямой
        // 404.
        return family == AiFamily.Claude && !string.IsNullOrWhiteSpace(envModel)
            ? envModel
            : family == AiFamily.Claude
                ? SettingKeys.DefaultBackgroundModel
                : SettingKeys.DefaultBackgroundChatGptModel;
    }

    /// <summary>
    /// Список моделей провайдера с коротким кэшем. Кэшируется и неудача — иначе недоступный
    /// провайдер означал бы обращение к нему на каждый вызов терминала.
    /// </summary>
    private async Task<IReadOnlyList<string>> CatalogAsync(AiFamily family, CancellationToken ct)
    {
        var cached = _catalog;
        if (cached is { } c && c.Family == family && DateTime.UtcNow - c.At < c.Ttl)
            return c.Ids;

        await _catalogLock.WaitAsync(ct);
        try
        {
            cached = _catalog;
            if (cached is { } again && again.Family == family && DateTime.UtcNow - again.At < again.Ttl)
                return again.Ids;

            var vendor = family == AiFamily.Claude ? AiVendor.Anthropic : AiVendor.OpenAi;
            var listed = await ListModelsAsync(vendor, ct);
            _catalog = (DateTime.UtcNow, family, listed.Models,
                listed.Ok ? CatalogTtl : CatalogRetryTtl);
            return listed.Models;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <summary>
    /// What the configured upstream says it serves. Used by the admin screen, because the model
    /// names are the one thing about a provider switch that cannot be worked out from this side —
    /// they live in the provider's dashboard, and getting one wrong fails as "model is not allowed"
    /// without ever naming the alternative.
    ///
    /// Returns the ids, or a short reason. Never throws: a provider that has no /v1/models, or is
    /// unreachable, must leave the rest of the admin screen usable.
    /// </summary>
    public async Task<(bool Ok, string Url, IReadOnlyList<string> Models, string? Error)> ListModelsAsync(
        AiVendor vendor, CancellationToken ct = default)
    {
        var anthropic = vendor == AiVendor.Anthropic;
        var vendorUrl = anthropic ? DefaultAnthropicUrl : DefaultOpenAiUrl;
        var baseUrl = await SettingAsync(
            anthropic ? SettingKeys.AnthropicBaseUrl : SettingKeys.OpenAiBaseUrl, vendorUrl, ct);
        var url = AiProxyDefaults.ModelsUrl(baseUrl);

        var key = await _keys.GetAsync(anthropic ? "anthropic" : "openai", ct)
                  ?? (anthropic ? _anthropicEnv : _openAiEnv);
        // Тот же один-ключ-на-два-формата, что и при пересылке: иначе список моделей для ChatGPT
        // не читался бы на верно настроенном роутере.
        if (string.IsNullOrWhiteSpace(key) && !anthropic && AiProxyDefaults.LooksLikeRouter(baseUrl, vendorUrl))
            key = await _keys.GetAsync("anthropic", ct) ?? _anthropicEnv;

        if (string.IsNullOrWhiteSpace(key))
            return (false, url, Array.Empty<string>(), "ключ провайдера не задан");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!anthropic || await BearerAsync(SettingKeys.AnthropicAuthBearer, baseUrl, vendorUrl, ct))
            {
                req.Headers.Add("Authorization", "Bearer " + key);
            }
            else
            {
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
            }

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, url, Array.Empty<string>(), $"{(int)resp.StatusCode} от провайдера");

            // Anthropic and every OpenAI-compatible service answer {"data":[{"id":"…"}]}.
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return (false, url, Array.Empty<string>(), "ответ не похож на список моделей");

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                    ids.Add(s);

            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return (true, url, ids, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, url, Array.Empty<string>(), e.GetType().Name);
        }
    }

    private async Task<string> SettingAsync(string key, string fallback, CancellationToken ct) =>
        _settings is null ? fallback : await _settings.GetAsync(key, fallback, ct) ?? fallback;

    /// <summary>
    /// Whether to authenticate with a bearer token rather than x-api-key.
    ///
    /// Defaults by destination rather than to a fixed value: pointing the URL at anything that is
    /// not Anthropic's own host almost certainly means a router, and every router speaks bearer.
    /// Getting this wrong produces a 401 that reads like a bad key, so the default is the one that
    /// makes a URL change sufficient on its own.
    /// </summary>
    private async Task<bool> BearerAsync(string key, string url, string vendorUrl, CancellationToken ct)
    {
        var defaultForUrl = AiProxyDefaults.LooksLikeRouter(url, vendorUrl);
        return _settings is null ? defaultForUrl : await _settings.GetBoolAsync(key, defaultForUrl, ct);
    }
}
