using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.Notifications;

/// <summary>How important a notification is — also the routing threshold per channel.</summary>
public enum NotificationSeverity { Info = 0, Success = 1, Warning = 2, Critical = 3 }

/// <summary>
/// One app event worth telling the user about — a fired alert, a bot action, a risk breach, etc.
/// <see cref="DedupKey"/> lets the hub suppress repeats of the same thing within a cooldown window;
/// leave it null for events that should always go out.
/// </summary>
public sealed record AppNotification(
    string Title,
    string Body,
    NotificationSeverity Severity,
    string Category,
    string? Symbol = null,
    string? DedupKey = null,
    DateTime Timestamp = default,
    string Id = "",
    IReadOnlyCollection<string>? ChannelsOnly = null);

/// <summary>An outbound destination (Telegram/Discord/ntfy/email/desktop). Adapters wrap the
/// existing per-channel services.</summary>
public interface INotificationChannel
{
    string Name { get; }
    bool IsConfigured { get; }
    /// <summary>Only receives notifications at or above this severity.</summary>
    NotificationSeverity MinSeverity { get; }
    Task<bool> SendAsync(AppNotification n, CancellationToken ct);
}

/// <summary>In-app inbox — the full record of admitted notifications (kept even when a channel is muted).</summary>
public interface INotificationSink
{
    void Append(AppNotification n);
    IReadOnlyList<AppNotification> Recent(int max);
}

/// <summary>Hub behaviour: global severity floor, muted categories, and the antispam cooldown.</summary>
public sealed class NotificationHubOptions
{
    public NotificationSeverity MinSeverity { get; set; } = NotificationSeverity.Info;
    public TimeSpan DedupWindow { get; set; } = TimeSpan.FromMinutes(5);
    public HashSet<string> MutedCategories { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What happened to a published notification.</summary>
/// <param name="Dropped">Below the severity floor or in a muted category — not logged, not sent.</param>
/// <param name="Suppressed">A duplicate within the cooldown — logged to inbox but not sent to channels.</param>
/// <param name="GroupedCount">How many duplicates have been folded into the current group.</param>
/// <param name="SentTo">Channels that accepted the notification.</param>
public readonly record struct PublishResult(
    bool Dropped, bool Suppressed, int GroupedCount, IReadOnlyList<string> SentTo)
{
    public static readonly PublishResult DroppedResult = new(true, false, 0, Array.Empty<string>());
}
