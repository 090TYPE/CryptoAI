using System.Text;
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

    private readonly HttpClient _http;
    private readonly ProviderKeyStore _keys;
    private readonly SettingsStore? _settings;
    private readonly string? _anthropicEnv;
    private readonly string? _openAiEnv;

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
    /// </summary>
    public async Task<(int Status, string Body)?> ForwardAnthropicAsync(string requestJson, CancellationToken ct = default)
    {
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
        var key = await _keys.GetAsync("openai", ct) ?? _openAiEnv;
        if (string.IsNullOrWhiteSpace(key)) return null;

        var url = await SettingAsync(SettingKeys.OpenAiBaseUrl, DefaultOpenAiUrl, ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        // Chat Completions is a bearer protocol everywhere, vendor and router alike.
        req.Headers.Add("Authorization", "Bearer " + key);

        using var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
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
