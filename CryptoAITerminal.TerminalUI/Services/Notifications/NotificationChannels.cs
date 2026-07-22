using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.Notifications;

/// <summary>Shared text formatting so every channel renders a notification the same way.</summary>
internal static class NotificationFormat
{
    public static string Icon(NotificationSeverity s) => s switch
    {
        NotificationSeverity.Critical => "🔴",
        NotificationSeverity.Warning => "🟠",
        NotificationSeverity.Success => "🟢",
        _ => "🔵",
    };

    public static string Title(AppNotification n) =>
        $"{Icon(n.Severity)} {n.Title}" + (string.IsNullOrEmpty(n.Symbol) ? "" : $" · {n.Symbol}");

    public static string OneLine(AppNotification n) => $"{Title(n)}\n{n.Body}";
}

/// <summary>Telegram channel adapter over the existing <see cref="TelegramNotificationService"/>.</summary>
public sealed class TelegramChannel : INotificationChannel
{
    private readonly TelegramNotificationService _svc;
    public TelegramChannel(TelegramNotificationService svc, NotificationSeverity minSeverity = NotificationSeverity.Info)
    { _svc = svc; MinSeverity = minSeverity; }
    public string Name => "Telegram";
    public bool IsConfigured => _svc.IsConfigured;
    public NotificationSeverity MinSeverity { get; }
    public Task<bool> SendAsync(AppNotification n, CancellationToken ct) => _svc.SendAsync(NotificationFormat.OneLine(n));
}

/// <summary>Discord webhook adapter.</summary>
public sealed class DiscordChannel : INotificationChannel
{
    private readonly DiscordWebhookNotificationService _svc;
    public DiscordChannel(DiscordWebhookNotificationService svc, NotificationSeverity minSeverity = NotificationSeverity.Info)
    { _svc = svc; MinSeverity = minSeverity; }
    public string Name => "Discord";
    public bool IsConfigured => _svc.IsConfigured;
    public NotificationSeverity MinSeverity { get; }
    public Task<bool> SendAsync(AppNotification n, CancellationToken ct) => _svc.SendAsync(NotificationFormat.OneLine(n));
}

/// <summary>ntfy.sh push adapter (uses the native title field).</summary>
public sealed class NtfyChannel : INotificationChannel
{
    private readonly NtfyNotificationService _svc;
    public NtfyChannel(NtfyNotificationService svc, NotificationSeverity minSeverity = NotificationSeverity.Info)
    { _svc = svc; MinSeverity = minSeverity; }
    public string Name => "ntfy";
    public bool IsConfigured => _svc.IsConfigured;
    public NotificationSeverity MinSeverity { get; }
    public Task<bool> SendAsync(AppNotification n, CancellationToken ct) => _svc.SendAsync(n.Body, NotificationFormat.Title(n));
}

/// <summary>Email adapter (subject = title, body = body).</summary>
public sealed class EmailChannel : INotificationChannel
{
    private readonly EmailNotificationService _svc;
    public EmailChannel(EmailNotificationService svc, NotificationSeverity minSeverity = NotificationSeverity.Warning)
    { _svc = svc; MinSeverity = minSeverity; }
    public string Name => "Email";
    public bool IsConfigured => _svc.IsConfigured;
    public NotificationSeverity MinSeverity { get; }
    public Task<bool> SendAsync(AppNotification n, CancellationToken ct) => _svc.SendAsync(NotificationFormat.Title(n), n.Body);
}

/// <summary>A code-driven channel — used for the in-app desktop toast/tray and in tests.</summary>
public sealed class DelegateChannel : INotificationChannel
{
    private readonly Func<AppNotification, CancellationToken, Task<bool>> _send;
    private readonly Func<bool> _configured;
    public DelegateChannel(string name, Func<AppNotification, CancellationToken, Task<bool>> send,
        Func<bool>? configured = null, NotificationSeverity minSeverity = NotificationSeverity.Info)
    { Name = name; _send = send; _configured = configured ?? (() => true); MinSeverity = minSeverity; }
    public string Name { get; }
    public bool IsConfigured => _configured();
    public NotificationSeverity MinSeverity { get; }
    public Task<bool> SendAsync(AppNotification n, CancellationToken ct) => _send(n, ct);
}

/// <summary>Bounded in-memory inbox (newest first). The UI binds to <see cref="Recent"/>.</summary>
public sealed class InMemoryNotificationInbox : INotificationSink
{
    private readonly int _capacity;
    private readonly LinkedList<AppNotification> _items = new();
    private readonly object _lock = new();

    public InMemoryNotificationInbox(int capacity = 500) => _capacity = capacity;

    public void Append(AppNotification n)
    {
        lock (_lock)
        {
            _items.AddFirst(n);
            if (_items.Count > _capacity) _items.RemoveLast();
        }
    }

    public IReadOnlyList<AppNotification> Recent(int max)
    {
        lock (_lock) return _items.Take(max).ToList();
    }
}
