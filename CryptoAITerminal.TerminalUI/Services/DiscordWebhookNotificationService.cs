using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class DiscordWebhookNotificationService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string _webhookUrl = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_webhookUrl)
        && Uri.TryCreate(_webhookUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public void Configure(string webhookUrl) => _webhookUrl = webhookUrl?.Trim() ?? string.Empty;

    /// <summary>
    /// Discord webhooks accept up to 2000 chars per "content". HTML-style tags are not interpreted —
    /// Discord uses Markdown. Strips Telegram-style &lt;b&gt;/&lt;/b&gt; so the same alert string renders cleanly here.
    /// </summary>
    public async Task<bool> SendAsync(string message)
    {
        if (!IsConfigured) return false;
        try
        {
            var sanitized = StripHtml(message);
            if (sanitized.Length > 1900) sanitized = sanitized[..1900] + "…";
            var payload = new { content = sanitized };
            using var response = await _http.PostAsJsonAsync(_webhookUrl, payload);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        if (!IsConfigured) return false;
        return await SendAsync("✅ CryptoAI Terminal — Discord webhook connected.");
    }

    private static string StripHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("<b>", "**", StringComparison.OrdinalIgnoreCase)
            .Replace("</b>", "**", StringComparison.OrdinalIgnoreCase)
            .Replace("<i>", "*", StringComparison.OrdinalIgnoreCase)
            .Replace("</i>", "*", StringComparison.OrdinalIgnoreCase)
            .Replace("<code>", "`", StringComparison.OrdinalIgnoreCase)
            .Replace("</code>", "`", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _http.Dispose();
}
