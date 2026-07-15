using System.Net.Http.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker;

/// <summary>Pushes a message to a user's configured channel. Best-effort — never throws.</summary>
public interface INotifier
{
    Task SendAsync(Guid userId, string title, string message, CancellationToken ct = default);
}

/// <summary>ntfy (phone push, no token) and Telegram (bot token + chat id) notifier.</summary>
public sealed class Notifier : INotifier
{
    private readonly HttpClient _http;
    private readonly NotificationRepository _repo;
    private readonly ILogger<Notifier> _log;

    public Notifier(NotificationRepository repo, ILogger<Notifier> log)
    {
        _repo = repo;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task SendAsync(Guid userId, string title, string message, CancellationToken ct = default)
    {
        var ch = await _repo.GetForUserAsync(userId, ct);
        if (ch is null || !ch.Enabled) return;

        try
        {
            switch (ch.Kind.ToLowerInvariant())
            {
                case "ntfy":
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://ntfy.sh/" + ch.Target)
                    {
                        Content = new StringContent(message)
                    };
                    req.Headers.TryAddWithoutValidation("Title", title);
                    using var r = await _http.SendAsync(req, ct);
                    r.EnsureSuccessStatusCode();
                    break;
                }
                case "telegram" when !string.IsNullOrWhiteSpace(ch.Token):
                {
                    var url = $"https://api.telegram.org/bot{ch.Token}/sendMessage";
                    using var r = await _http.PostAsJsonAsync(url, new { chat_id = ch.Target, text = title + "\n" + message }, ct);
                    r.EnsureSuccessStatusCode();
                    break;
                }
                default:
                    return;
            }
            _log.LogInformation("notified {User} via {Kind}", userId, ch.Kind);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "notify {User} via {Kind} failed", userId, ch.Kind); }
    }
}
