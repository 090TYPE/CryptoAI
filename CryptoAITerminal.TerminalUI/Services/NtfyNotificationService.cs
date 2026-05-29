using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Бесплатный open-source push через ntfy.sh — анонимные топики, без аккаунта.
/// Топик — это секретный токен (любая строка). Любой, кто знает имя топика, может публиковать и подписываться,
/// поэтому используйте длинное случайное имя вроде "crypto-ai-<random-32-chars>".
/// </summary>
public sealed class NtfyNotificationService : IDisposable
{
    private const string DefaultServer = "https://ntfy.sh/";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string _topic = string.Empty;
    private string _serverBase = DefaultServer;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_topic);

    /// <summary>
    /// Принимает либо полный URL (https://ntfy.sh/my-topic, https://my-host.com/topic),
    /// либо просто имя топика — в этом случае используется ntfy.sh.
    /// </summary>
    public void Configure(string topicOrUrl)
    {
        if (string.IsNullOrWhiteSpace(topicOrUrl))
        {
            _topic = string.Empty;
            _serverBase = DefaultServer;
            return;
        }

        var trimmed = topicOrUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var segment = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrEmpty(segment))
            {
                _topic = string.Empty;
                _serverBase = DefaultServer;
                return;
            }

            _topic = segment;
            _serverBase = $"{uri.Scheme}://{uri.Authority}/";
        }
        else
        {
            _topic = trimmed;
            _serverBase = DefaultServer;
        }
    }

    /// <summary>
    /// ntfy.sh принимает plain text body (max 4 KB). Заголовки X-Title / X-Tags опциональны.
    /// </summary>
    public async Task<bool> SendAsync(string message, string? title = null)
    {
        if (!IsConfigured) return false;
        try
        {
            var sanitized = StripHtml(message);
            if (sanitized.Length > 4000) sanitized = sanitized[..4000] + "…";

            using var request = new HttpRequestMessage(HttpMethod.Post, _serverBase + _topic)
            {
                Content = new StringContent(sanitized, Encoding.UTF8, "text/plain")
            };
            if (!string.IsNullOrWhiteSpace(title))
            {
                request.Headers.Add("X-Title", title);
            }

            using var response = await _http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> TestConnectionAsync()
    {
        if (!IsConfigured) return Task.FromResult(false);
        return SendAsync("CryptoAI Terminal — ntfy push connected.", title: "CryptoAI Terminal");
    }

    private static string StripHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("<b>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</b>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<i>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</i>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<code>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</code>", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _http.Dispose();
}
