using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>One shared AI digest row (same content every user sees).</summary>
public sealed class AiDigestRowVM
{
    public AiDigestRowVM(ServerDigest d)
    {
        var info = AiStreamCatalog.Describe(d.Kind);
        Kind = info.Name;                 // human name, not the raw kind
        Cadence = info.Cadence;
        Group = info.Group;
        Title = d.Title;
        Body = d.Body ?? string.Empty;
        AgeLabel = AiFeedViewModel.Age(d.CreatedUtc);
    }

    public string Kind { get; }
    public string Cadence { get; }
    public string Group { get; }
    public string Title { get; }
    public string Body { get; }
    public string AgeLabel { get; }
}

/// <summary>One AI function the server runs — listed even before it has published anything.</summary>
public sealed class AiStreamRowVM
{
    public AiStreamRowVM(AiStreamInfo info, ServerDigest? latest)
    {
        Name = info.Name;
        Description = info.Description;
        Cadence = info.Cadence;
        Group = info.Group;
        HasLatest = latest is not null;
        LatestTitle = latest?.Title ?? "waiting for first publish";
        AgeLabel = latest is null ? "" : AiFeedViewModel.Age(latest.CreatedUtc);
    }

    public string Name { get; }
    public string Description { get; }
    public string Cadence { get; }
    public string Group { get; }
    public bool HasLatest { get; }
    public string LatestTitle { get; }
    public string AgeLabel { get; }
}

/// <summary>One personal event delivered to this terminal.</summary>
public sealed class InboxRowVM
{
    public InboxRowVM(InboxMessage m)
    {
        Id = m.Id;
        Kind = m.Kind.ToUpperInvariant();
        Title = m.Title;
        Message = m.Message ?? string.Empty;
        AgeLabel = AiFeedViewModel.Age(m.CreatedUtc);
        IsUnread = m.ReadUtc is null;
    }

    public Guid Id { get; }
    public string Kind { get; }
    public string Title { get; }
    public string Message { get; }
    public string AgeLabel { get; }
    public bool IsUnread { get; }
}

/// <summary>
/// The dashboard's server feed: the shared AI digest streams the server computes once for
/// everyone, plus this user's own events (alerts, AI anomalies, bots). Silent no-op when no
/// server/licence is configured — the terminal keeps working purely locally.
/// </summary>
public sealed class AiFeedViewModel : ReactiveObject, IDisposable
{
    private readonly ServerFeedService _feed;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<AiDigestRowVM> Digests { get; } = [];
    public ObservableCollection<InboxRowVM> Inbox { get; } = [];

    /// <summary>Every AI function the server runs, with its cadence and last publish.</summary>
    public ObservableCollection<AiStreamRowVM> Streams { get; } = [];

    private string _statusLabel = "Server not configured — local mode";
    private bool _isLoading;
    private int _unreadBadge;

    public string StatusLabel { get => _statusLabel; private set => this.RaiseAndSetIfChanged(ref _statusLabel, value); }
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    public int UnreadBadge { get => _unreadBadge; private set { this.RaiseAndSetIfChanged(ref _unreadBadge, value); this.RaisePropertyChanged(nameof(HasUnread)); } }
    public bool HasUnread => UnreadBadge > 0;
    public bool HasInbox => Inbox.Count > 0;
    public bool HasDigests => Digests.Count > 0;
    public bool IsConfigured => _feed.IsConfigured;

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> MarkAllReadCommand { get; }

    public AiFeedViewModel(ServerFeedService? feed = null)
    {
        _feed = feed ?? new ServerFeedService();
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        MarkAllReadCommand = ReactiveCommand.CreateFromTask(MarkAllReadAsync);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) => await RefreshAsync();

        if (_feed.IsConfigured)
        {
            _timer.Start();
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        if (!_feed.IsConfigured) return;

        if (Digests.Count == 0 && Inbox.Count == 0) IsLoading = true;

        // Pull deep enough to find the latest publish of every stream, not just the newest few.
        var digests = await _feed.GetDigestsAsync(limit: 100);
        var inbox = await _feed.GetInboxAsync(limit: 20);
        var unread = await _feed.GetUnreadCountAsync();

        // Latest publish per kind → lets us show every catalogued function with its state.
        var latestByKind = digests
            .GroupBy(d => d.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.CreatedUtc).First(), StringComparer.OrdinalIgnoreCase);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Digests.Clear();
            foreach (var d in digests.OrderByDescending(d => d.CreatedUtc).Take(20))
                Digests.Add(new AiDigestRowVM(d));

            Inbox.Clear();
            foreach (var m in inbox) Inbox.Add(new InboxRowVM(m));

            Streams.Clear();
            foreach (var info in AiStreamCatalog.BroadcastStreams)
                Streams.Add(new AiStreamRowVM(info, latestByKind.GetValueOrDefault(info.Kind)));

            UnreadBadge = unread;
            this.RaisePropertyChanged(nameof(HasInbox));
            this.RaisePropertyChanged(nameof(HasDigests));
            var live = latestByKind.Count;
            StatusLabel = digests.Count == 0 && inbox.Count == 0
                ? "Connected — nothing published yet"
                : $"{live}/{AiStreamCatalog.BroadcastStreams.Count} AI streams live · {inbox.Count} events · {DateTime.Now:HH:mm:ss}";
            IsLoading = false;
        });
    }

    private async Task MarkAllReadAsync()
    {
        if (await _feed.MarkAllReadAsync()) await RefreshAsync();
    }

    /// <summary>"3m ago" style age for a UTC timestamp.</summary>
    public static string Age(DateTime utc)
    {
        var span = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    public void Dispose() => _timer.Stop();
}
