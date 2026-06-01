using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ── Row VM ────────────────────────────────────────────────────────────────────

public sealed class NewsItemRowVM : ReactiveObject
{
    public NewsItem Item { get; }

    public string Title        => Item.Title;
    public string Source       => Item.Source;
    public string AgeLabel     { get; private set; }
    public string CurrencyTags => string.Join(" · ", Item.Currencies.Take(4));
    public bool   HasTags      => Item.Currencies.Count > 0;
    public bool   IsImportant  => Item.IsImportant;

    public string SentimentLabel => Item.Sentiment switch
    {
        NewsSentiment.Bullish => "▲ Bullish",
        NewsSentiment.Bearish => "▼ Bearish",
        _                     => "● Neutral"
    };

    public string SentimentBrush => Item.Sentiment switch
    {
        NewsSentiment.Bullish => "#21E6C1",
        NewsSentiment.Bearish => "#FF6B6B",
        _                     => "#8FA3B8"
    };

    public string SentimentBackground => Item.Sentiment switch
    {
        NewsSentiment.Bullish => "#0F2820",
        NewsSentiment.Bearish => "#2A1010",
        _                     => "#0A1018"
    };

    public string ImportantBadgeBrush => "#F4B860";

    public string VotesLabel => Item.Votes > 0 ? $"+{Item.Votes}" : Item.Votes.ToString();
    public string VotesBrush => Item.Votes >= 0 ? "#21E6C1" : "#FF6B6B";

    public NewsItemRowVM(NewsItem item)
    {
        Item     = item;
        AgeLabel = FormatAge(item.PublishedAt);
    }

    public void RefreshAge() => AgeLabel = FormatAge(Item.PublishedAt);

    private static string FormatAge(DateTime publishedAt)
    {
        var delta = DateTime.UtcNow - publishedAt;
        return delta.TotalMinutes < 1  ? "just now"
             : delta.TotalMinutes < 60 ? $"{(int)delta.TotalMinutes}m ago"
             : delta.TotalHours  < 24  ? $"{(int)delta.TotalHours}h ago"
             : $"{(int)delta.TotalDays}d ago";
    }
}

// ── Main VM ───────────────────────────────────────────────────────────────────

/// <summary>
/// Displays real-time crypto news aggregated from major RSS sources (CoinTelegraph, CoinDesk,
/// Decrypt, The Block, Bitcoin Magazine) with auto-sentiment classification.
/// Optionally augments with CryptoPanic when CRYPTOPANIC_API_KEY is set.
/// Supports filtering by symbol and alerts for important news on watched coins.
/// </summary>
public sealed class NewsFeedViewModel : ReactiveObject, IDisposable
{
    private readonly NewsFeedService _service;
    private readonly List<NewsItem>  _allItems = [];
    private readonly NewsAiSummaryService _aiSummary = new();

    // ── Market pulse (aggregated sentiment over the last hour) ──────────────────
    private static readonly TimeSpan PulseWindow = TimeSpan.FromHours(1);
    private int _pulseScore;
    private string _pulseLabel = "No data";
    private string _pulseBrush = "#8FA3B8";
    private string _pulseDetail = "Awaiting headlines";

    // ── AI market digest ────────────────────────────────────────────────────────
    private string _aiDigest = "AI digest pending…";
    private string _aiDigestBias = "NEUTRAL";
    private string _aiDigestSource = string.Empty;
    private bool _aiDigestRunning;
    private DateTime _lastDigestUtc = DateTime.MinValue;
    private static readonly TimeSpan DigestThrottle = TimeSpan.FromMinutes(3);

    // ── Filter state ──────────────────────────────────────────────────────────
    private string _filterSymbol  = "";
    private string _filterSentiment = "All";   // "All" | "Bullish" | "Bearish" | "Neutral"
    private string _searchText    = "";
    private bool   _showImportantOnly;
    private string _statusLabel   = "Waiting for news…";
    private bool   _isLoading     = true;
    private int    _unreadCount;

    public ObservableCollection<NewsItemRowVM>  Rows       { get; } = [];
    public IReadOnlyList<string> SentimentOptions { get; } = ["All", "Bullish", "Bearish", "Neutral"];

    public string FilterSymbol
    {
        get => _filterSymbol;
        set { this.RaiseAndSetIfChanged(ref _filterSymbol, value?.ToUpperInvariant() ?? ""); ApplyFilter(); }
    }
    public string FilterSentiment
    {
        get => _filterSentiment;
        set { this.RaiseAndSetIfChanged(ref _filterSentiment, value); ApplyFilter(); }
    }
    public string SearchText
    {
        get => _searchText;
        set { this.RaiseAndSetIfChanged(ref _searchText, value ?? ""); ApplyFilter(); }
    }
    public bool ShowImportantOnly
    {
        get => _showImportantOnly;
        set { this.RaiseAndSetIfChanged(ref _showImportantOnly, value); ApplyFilter(); }
    }
    public string StatusLabel   { get => _statusLabel;  private set => this.RaiseAndSetIfChanged(ref _statusLabel, value); }
    public bool   IsLoading     { get => _isLoading;    private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    public int    UnreadCount   { get => _unreadCount;  private set => this.RaiseAndSetIfChanged(ref _unreadCount, value); }
    public bool   HasUnread     => _unreadCount > 0;
    public string UnreadBadge   => _unreadCount > 99 ? "99+" : _unreadCount.ToString();

    // ── Market pulse (deterministic, always available) ──────────────────────────
    public int    PulseScore  { get => _pulseScore;  private set => this.RaiseAndSetIfChanged(ref _pulseScore, value); }
    public string PulseLabel  { get => _pulseLabel;  private set => this.RaiseAndSetIfChanged(ref _pulseLabel, value); }
    public string PulseBrush  { get => _pulseBrush;  private set => this.RaiseAndSetIfChanged(ref _pulseBrush, value); }
    public string PulseDetail { get => _pulseDetail; private set => this.RaiseAndSetIfChanged(ref _pulseDetail, value); }

    // ── AI digest (Claude or offline heuristic) ─────────────────────────────────
    public string AiDigest       { get => _aiDigest;       private set => this.RaiseAndSetIfChanged(ref _aiDigest, value); }
    public string AiDigestBias   { get => _aiDigestBias;   private set { this.RaiseAndSetIfChanged(ref _aiDigestBias, value); this.RaisePropertyChanged(nameof(AiDigestBrush)); } }
    public string AiDigestSource { get => _aiDigestSource; private set => this.RaiseAndSetIfChanged(ref _aiDigestSource, value); }
    public bool   AiDigestRunning { get => _aiDigestRunning; private set => this.RaiseAndSetIfChanged(ref _aiDigestRunning, value); }

    public string AiDigestBrush => _aiDigestBias switch
    {
        "BULLISH" => "#21E6C1",
        "BEARISH" => "#FF6B6B",
        _         => "#8FA3B8"
    };

    public ReactiveCommand<Unit, Unit> RefreshDigestCommand { get; }

    /// <summary>Wire the Claude key/model shared from the AI Bot settings.</summary>
    public void ConfigureAi(string? apiKey, string? model = null)
    {
        if (apiKey is not null) _aiSummary.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) _aiSummary.Model = model;
    }

    // Fired when a major news item arrives for a watched coin
    public event Action<string>? AlertTriggered;

    public ReactiveCommand<Unit, Unit> ClearUnreadCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFilterCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public NewsFeedViewModel(NewsFeedService service)
    {
        _service = service;
        _service.NewsReceived += OnNewsReceived;

        // Safety: clear loading spinner after 20 s even if no news arrived yet
        var startupTimeout = new System.Timers.Timer(20_000) { AutoReset = false };
        startupTimeout.Elapsed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsLoading)
                {
                    IsLoading   = false;
                    StatusLabel = "Waiting for news…";
                }
                startupTimeout.Dispose();
            });
        startupTimeout.Start();

        ClearUnreadCommand = ReactiveCommand.Create(() =>
        {
            UnreadCount = 0;
            this.RaisePropertyChanged(nameof(HasUnread));
        }, outputScheduler: App.UiScheduler);

        ClearFilterCommand = ReactiveCommand.Create(() =>
        {
            FilterSymbol      = "";
            FilterSentiment   = "All";
            SearchText        = "";
            ShowImportantOnly = false;
        }, outputScheduler: App.UiScheduler);

        RefreshDigestCommand = ReactiveCommand.CreateFromTask(() => RefreshDigestAsync(force: true),
            outputScheduler: App.UiScheduler);

        // Refresh age labels every minute
        var ageTimer = new System.Timers.Timer(60_000) { AutoReset = true };
        ageTimer.Elapsed += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var r in Rows) r.RefreshAge();
            }, DispatcherPriority.Background);
        ageTimer.Start();
    }

    // ── Event handler ─────────────────────────────────────────────────────────

    private void OnNewsReceived(IReadOnlyList<NewsItem> items)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in items)
                _allItems.Insert(0, item);

            // Cap total history
            while (_allItems.Count > 500) _allItems.RemoveAt(_allItems.Count - 1);

            UnreadCount += items.Count;
            this.RaisePropertyChanged(nameof(HasUnread));
            this.RaisePropertyChanged(nameof(UnreadBadge));

            ApplyFilter();

            StatusLabel = $"{_allItems.Count} items · last update {DateTime.Now:HH:mm:ss}";
            IsLoading   = false;

            RecomputePulse();
            _ = RefreshDigestAsync(force: false);

            // Alerts: fire for important items matching filter symbol
            foreach (var item in items.Where(i => i.IsImportant))
            {
                var symbol = _filterSymbol;
                if (!string.IsNullOrWhiteSpace(symbol) &&
                    !item.Currencies.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                    continue;

                var sentimentTag = item.Sentiment == NewsSentiment.Bullish ? "🟢 Bullish"
                    : item.Sentiment == NewsSentiment.Bearish ? "🔴 Bearish"
                    : "⚪ Neutral";
                AlertTriggered?.Invoke(
                    $"📰 [{sentimentTag}] {item.Source}: {TruncateTitle(item.Title)}");
            }
        }, DispatcherPriority.Background);
    }

    // ── Filtering ─────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var filtered = _allItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(_filterSymbol))
            filtered = filtered.Where(i =>
                i.Currencies.Any(c => c.Equals(_filterSymbol, StringComparison.OrdinalIgnoreCase)));

        if (_filterSentiment != "All")
        {
            var target = _filterSentiment switch
            {
                "Bullish" => NewsSentiment.Bullish,
                "Bearish" => NewsSentiment.Bearish,
                _         => NewsSentiment.Neutral
            };
            filtered = filtered.Where(i => i.Sentiment == target);
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
            filtered = filtered.Where(i =>
                i.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                i.Source.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

        if (_showImportantOnly)
            filtered = filtered.Where(i => i.IsImportant);

        var list = filtered.Take(200).ToList();

        Rows.Clear();
        foreach (var item in list)
            Rows.Add(new NewsItemRowVM(item));
    }

    // ── Market pulse + AI digest ────────────────────────────────────────────────

    private (int Bull, int Bear, int Neutral, List<string> Headlines) GetRecentWindow()
    {
        var cutoff = DateTime.UtcNow - PulseWindow;
        var recent = _allItems.Where(i => i.PublishedAt >= cutoff).ToList();
        var bull    = recent.Count(i => i.Sentiment == NewsSentiment.Bullish);
        var bear    = recent.Count(i => i.Sentiment == NewsSentiment.Bearish);
        var neutral = recent.Count(i => i.Sentiment == NewsSentiment.Neutral);
        var headlines = recent
            .OrderByDescending(i => i.PublishedAt)
            .Select(i => i.Title)
            .ToList();
        return (bull, bear, neutral, headlines);
    }

    private void RecomputePulse()
    {
        var (bull, bear, neutral, _) = GetRecentWindow();
        var total = bull + bear + neutral;

        if (total == 0)
        {
            PulseScore  = 0;
            PulseLabel  = "No data";
            PulseBrush  = "#8FA3B8";
            PulseDetail = "No headlines in the last hour";
            return;
        }

        // -100 (fully bearish) … +100 (fully bullish)
        PulseScore = (int)Math.Round((bull - bear) * 100.0 / total);

        (PulseLabel, PulseBrush) = PulseScore switch
        {
            >= 40  => ("Bullish",        "#21E6C1"),
            >= 10  => ("Mildly bullish", "#3DDC84"),
            <= -40 => ("Bearish",        "#FF6B6B"),
            <= -10 => ("Mildly bearish", "#FF8A4C"),
            _      => ("Neutral",        "#8FA3B8")
        };

        PulseDetail = $"{total} headlines/h · {bull}▲ {bear}▼ {neutral}● · score {PulseScore:+0;-0;0}";
    }

    private async Task RefreshDigestAsync(bool force)
    {
        if (_aiDigestRunning) return;
        if (!force && DateTime.UtcNow - _lastDigestUtc < DigestThrottle) return;

        var (bull, bear, neutral, headlines) = GetRecentWindow();
        if (headlines.Count == 0)
        {
            // Fall back to the most recent items regardless of age so demo always shows a digest.
            headlines = _allItems.Take(25).Select(i => i.Title).ToList();
            if (headlines.Count == 0) return;
        }

        _lastDigestUtc = DateTime.UtcNow;
        AiDigestRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var digest = await _aiSummary
                .SummarizeAsync(headlines, bull, bear, neutral, cts.Token)
                .ConfigureAwait(true);

            AiDigest       = digest.Summary;
            AiDigestBias   = digest.Bias;
            AiDigestSource = digest.Source;
        }
        catch (Exception)
        {
            // Keep the previous digest; never surface as a crash.
        }
        finally
        {
            AiDigestRunning = false;
        }
    }

    private static string TruncateTitle(string t, int max = 80) =>
        t.Length <= max ? t : t[..max] + "…";

    public void Dispose()
    {
        _service.NewsReceived -= OnNewsReceived;
    }
}
