using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.Notifications;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Notifications;

/// <summary>The unified notification hub: severity floor + category mute drop events, antispam folds
/// duplicates within the cooldown (still logged), and routing respects per-channel thresholds.</summary>
public class NotificationHubTests
{
    private sealed class RecordingChannel : INotificationChannel
    {
        public readonly List<AppNotification> Sent = new();
        public RecordingChannel(string name, NotificationSeverity min = NotificationSeverity.Info, bool configured = true)
        { Name = name; MinSeverity = min; IsConfigured = configured; }
        public string Name { get; }
        public bool IsConfigured { get; }
        public NotificationSeverity MinSeverity { get; }
        public Task<bool> SendAsync(AppNotification n, CancellationToken ct) { Sent.Add(n); return Task.FromResult(true); }
    }

    private static AppNotification N(string title, NotificationSeverity sev = NotificationSeverity.Info,
        string category = "bot", string? dedup = null)
        => new(title, "body", sev, category, DedupKey: dedup);

    [Fact]
    public async Task Below_min_severity_is_dropped_not_logged()
    {
        var ch = new RecordingChannel("c");
        var inbox = new InMemoryNotificationInbox();
        var hub = new NotificationHub(new[] { ch }, inbox,
            new NotificationHubOptions { MinSeverity = NotificationSeverity.Warning });

        var r = await hub.PublishAsync(N("noise", NotificationSeverity.Info));

        Assert.True(r.Dropped);
        Assert.Empty(ch.Sent);
        Assert.Empty(inbox.Recent(10));
    }

    [Fact]
    public async Task Muted_category_is_dropped()
    {
        var ch = new RecordingChannel("c");
        var opts = new NotificationHubOptions();
        opts.MutedCategories.Add("news");
        var hub = new NotificationHub(new[] { ch }, new InMemoryNotificationInbox(), opts);

        var r = await hub.PublishAsync(N("headline", NotificationSeverity.Warning, category: "news"));

        Assert.True(r.Dropped);
        Assert.Empty(ch.Sent);
    }

    [Fact]
    public async Task Admitted_notification_is_logged_and_routed()
    {
        var ch = new RecordingChannel("c");
        var inbox = new InMemoryNotificationInbox();
        var hub = new NotificationHub(new[] { ch }, inbox);

        var r = await hub.PublishAsync(N("bot filled", NotificationSeverity.Success));

        Assert.Contains("c", r.SentTo);
        Assert.Single(ch.Sent);
        Assert.Single(inbox.Recent(10));
    }

    [Fact]
    public async Task Channel_threshold_filters_low_severity()
    {
        var critical = new RecordingChannel("email", NotificationSeverity.Critical);
        var all = new RecordingChannel("tg", NotificationSeverity.Info);
        var hub = new NotificationHub(new[] { critical, all }, new InMemoryNotificationInbox());

        await hub.PublishAsync(N("warn", NotificationSeverity.Warning));

        Assert.Empty(critical.Sent);   // warning < critical
        Assert.Single(all.Sent);
    }

    [Fact]
    public async Task Duplicate_within_cooldown_is_suppressed_but_still_logged()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ch = new RecordingChannel("c");
        var inbox = new InMemoryNotificationInbox();
        var hub = new NotificationHub(new[] { ch }, inbox,
            new NotificationHubOptions { DedupWindow = TimeSpan.FromMinutes(5) }, () => now);

        var first = await hub.PublishAsync(N("BTC > 100k", NotificationSeverity.Warning, dedup: "btc-100k"));
        var second = await hub.PublishAsync(N("BTC > 100k", NotificationSeverity.Warning, dedup: "btc-100k"));

        Assert.False(first.Suppressed);
        Assert.True(second.Suppressed);
        Assert.Equal(1, second.GroupedCount);
        Assert.Single(ch.Sent);              // sent once
        Assert.Equal(2, inbox.Recent(10).Count); // both logged
    }

    [Fact]
    public async Task Duplicate_after_cooldown_is_sent_again()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ch = new RecordingChannel("c");
        var hub = new NotificationHub(new[] { ch }, new InMemoryNotificationInbox(),
            new NotificationHubOptions { DedupWindow = TimeSpan.FromMinutes(5) }, () => now);

        await hub.PublishAsync(N("x", NotificationSeverity.Warning, dedup: "k"));
        now = now.AddMinutes(6); // past the cooldown
        var again = await hub.PublishAsync(N("x", NotificationSeverity.Warning, dedup: "k"));

        Assert.False(again.Suppressed);
        Assert.Equal(2, ch.Sent.Count);
    }

    [Fact]
    public async Task ChannelsOnly_restricts_routing_to_named_channels()
    {
        var desktop = new RecordingChannel("Desktop");
        var telegram = new RecordingChannel("Telegram");
        var hub = new NotificationHub(new[] { desktop, telegram }, new InMemoryNotificationInbox());

        var n = new AppNotification("x", "y", NotificationSeverity.Warning, "alert",
            ChannelsOnly: new[] { "Desktop" });
        var r = await hub.PublishAsync(n);

        Assert.Equal(new[] { "Desktop" }, r.SentTo);
        Assert.Single(desktop.Sent);
        Assert.Empty(telegram.Sent);
    }

    [Fact]
    public void Digest_rolls_up_counts_and_top_symbols()
    {
        var items = new[]
        {
            N("a", NotificationSeverity.Warning, "bot"),
            N("b", NotificationSeverity.Warning, "bot") with { Symbol = "BTCUSDT" },
            N("c", NotificationSeverity.Info, "news") with { Symbol = "BTCUSDT" },
        };

        var d = NotificationHub.BuildDigest(items);

        Assert.Equal(3, d.Total);
        Assert.Equal(2, d.ByCategory["bot"]);
        Assert.Equal(2, d.BySeverity[NotificationSeverity.Warning]);
        Assert.Equal("BTCUSDT", d.TopSymbols[0]);
    }
}
