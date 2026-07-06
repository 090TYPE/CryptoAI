using System.Net.Http.Headers;
using System.Text;

namespace CryptoAITerminal.Backend.Services;

/// <summary>
/// Calls upstream paid APIs with the server-held keys, so the desktop app never ships them.
/// Anthropic (AI) and Covalent (portfolio) are the two the app needs proxied. Keys come from
/// configuration/environment and stay on the server.
/// </summary>
public sealed class UpstreamProxy
{
    private readonly HttpClient _http;
    private readonly string _anthropicKey;
    private readonly string _covalentKey;

    public UpstreamProxy(HttpClient http, string anthropicKey, string covalentKey)
    {
        _http = http;
        _anthropicKey = anthropicKey;
        _covalentKey = covalentKey;
    }

    public bool AiConfigured => !string.IsNullOrWhiteSpace(_anthropicKey);
    public bool PortfolioConfigured => !string.IsNullOrWhiteSpace(_covalentKey);

    /// <summary>Forwards a Messages API request body to Anthropic and returns (status, body).</summary>
    public async Task<(int Status, string Body)> ForwardAnthropicAsync(string requestJson, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.TryAddWithoutValidation("x-api-key", _anthropicKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, body);
    }

    /// <summary>Fetches Covalent balances_v2 for a wallet on a chain; returns (status, body).</summary>
    public async Task<(int Status, string Body)> FetchCovalentBalancesAsync(int chainId, string address, CancellationToken ct)
    {
        var url = $"https://api.covalenthq.com/v1/{chainId}/address/{address}/balances_v2/?key={_covalentKey}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, body);
    }
}
