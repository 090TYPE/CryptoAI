using System.Net.Http.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker;

/// <summary>Delivers a server event to the user. Best-effort — never throws.</summary>
public interface INotifier
{
    Task SendAsync(Guid userId, string title, string message, string kind = "system", CancellationToken ct = default);
}

/// <summary>
/// Primary channel is the in-terminal inbox: every user gets it, no setup. A phone channel
/// (ntfy topic / Telegram bot) is optional and only fires when the user configured one.
/// </summary>
public sealed class Notifier : INotifier
{
    private readonly HttpClient _http;
    private readonly InboxRepository _inbox;
    private readonly NotificationRepository _repo;
    private readonly ILogger<Notifier> _log;

    public Notifier(InboxRepository inbox, NotificationRepository repo, ILogger<Notifier> log)
    {
        _inbox = inbox;
        _repo = repo;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task SendAsync(Guid userId, string title, string message, string kind = "system", CancellationToken ct = default)
    {
        // 1) Always land it in the terminal inbox.
        try
        {
            await _inbox.InsertAsync(userId, kind, title, message, ct);
            _log.LogInformation("inbox <- {User}: {Title}", userId, title);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "inbox write failed for {User}", userId); }

        // 2) Optional phone push, only if the user set a channel up.
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
