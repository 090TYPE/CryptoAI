using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.Notifications;

/// <summary>
/// Single entry point for every user-facing event in the app. One <see cref="PublishAsync"/> call
/// fans out to all configured channels, with:
///  • a global severity floor and per-category mute,
///  • antispam: repeats sharing a <see cref="AppNotification.DedupKey"/> inside the cooldown are
///    folded into a group and not re-sent to channels (still logged to the inbox),
///  • per-channel severity thresholds.
/// Consolidates the previously scattered per-alert channel wiring behind one API.
/// </summary>
public sealed class NotificationHub
{
    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly INotificationSink _inbox;
    private readonly NotificationHubOptions _opts;
    private readonly Func<DateTime> _clock;

    // dedupKey → (lastSentUtc, foldedCount)
    private readonly Dictionary<string, (DateTime Last, int Grouped)> _seen =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public NotificationHub(
        IEnumerable<INotificationChannel> channels,
        INotificationSink inbox,
        NotificationHubOptions? options = null,
        Func<DateTime>? clock = null)
    {
        _channels = channels.ToList();
        _inbox = inbox;
        _opts = options ?? new NotificationHubOptions();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public async Task<PublishResult> PublishAsync(AppNotification n, CancellationToken ct = default)
    {
        var now = _clock();
        if (n.Timestamp == default) n = n with { Timestamp = now };
        if (string.IsNullOrEmpty(n.Id)) n = n with { Id = Guid.NewGuid().ToString("N") };

        // Severity floor + category mute → drop entirely.
        if (n.Severity < _opts.MinSeverity || _opts.MutedCategories.Contains(n.Category))
            return PublishResult.DroppedResult;

        // Admitted notifications are always recorded in the inbox (the full audit trail).
        _inbox.Append(n);

        // Antispam: fold duplicates within the cooldown; don't re-hit external channels.
        if (n.DedupKey is { } key)
        {
            lock (_lock)
            {
                if (_seen.TryGetValue(key, out var e) && now - e.Last < _opts.DedupWindow)
                {
                    var grouped = e.Grouped + 1;
                    _seen[key] = (e.Last, grouped);
                    return new PublishResult(false, true, grouped, Array.Empty<string>());
                }
                _seen[key] = (now, 0);
            }
        }

        // Route to every configured channel whose threshold this notification clears.
        var sent = new List<string>();
        foreach (var ch in _channels)
        {
            if (!ch.IsConfigured || n.Severity < ch.MinSeverity) continue;
            try { if (await ch.SendAsync(n, ct)) sent.Add(ch.Name); }
            catch { /* one bad channel must not sink the rest */ }
        }

        return new PublishResult(false, false, 0, sent);
    }

    /// <summary>Aggregate notifications into a compact digest (for the daily AI/summary briefing).</summary>
    public static NotificationDigest BuildDigest(IEnumerable<AppNotification> items)
    {
        var list = items.ToList();
        var byCategory = list.GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var bySeverity = list.GroupBy(x => x.Severity)
            .ToDictionary(g => g.Key, g => g.Count());
        var topSymbols = list.Where(x => !string.IsNullOrEmpty(x.Symbol))
            .GroupBy(x => x.Symbol!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(5).Select(g => g.Key).ToList();
        return new NotificationDigest(list.Count, byCategory, bySeverity, topSymbols);
    }
}

/// <summary>Rolled-up counts for a briefing window.</summary>
public sealed record NotificationDigest(
    int Total,
    IReadOnlyDictionary<string, int> ByCategory,
    IReadOnlyDictionary<NotificationSeverity, int> BySeverity,
    IReadOnlyList<string> TopSymbols);
