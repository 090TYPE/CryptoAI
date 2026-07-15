using System.Text;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Forwards AI chat requests to Anthropic's Messages API with the SERVER-held key, so the key
/// never ships to clients. The key is read from ProviderKeyStore ('anthropic') on every call —
/// editable via the admin console — with an env fallback (ANTHROPIC_API_KEY). The request/response
/// bodies pass through verbatim, so the app sends the normal Anthropic payload.
/// </summary>
public sealed class AiProxy
{
    private const string AnthropicUrl = "https://api.anthropic.com/v1/messages";
    private const string OpenAiUrl = "https://api.openai.com/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly ProviderKeyStore _keys;
    private readonly string? _anthropicEnv;
    private readonly string? _openAiEnv;

    public AiProxy(HttpClient http, ProviderKeyStore keys, string? anthropicEnv = null, string? openAiEnv = null)
    {
        _http = http;
        _keys = keys;
        _anthropicEnv = anthropicEnv;
        _openAiEnv = openAiEnv;
    }

    /// <summary>Forward to Claude. Returns null when no key is configured (→ 503 at the API).</summary>
    public async Task<(int Status, string Body)?> ForwardAnthropicAsync(string requestJson, CancellationToken ct = default)
    {
        var key = await _keys.GetAsync("anthropic", ct) ?? _anthropicEnv;
        if (string.IsNullOrWhiteSpace(key)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Post, AnthropicUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Forward to OpenAI Chat Completions. Returns null when no key is configured.</summary>
    public async Task<(int Status, string Body)?> ForwardOpenAiAsync(string requestJson, CancellationToken ct = default)
    {
        var key = await _keys.GetAsync("openai", ct) ?? _openAiEnv;
        if (string.IsNullOrWhiteSpace(key)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Post, OpenAiUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", "Bearer " + key);

        using var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }
}
