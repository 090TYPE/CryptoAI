using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Dashboard overview: shows status of all bots, total P&L, balances and alerts at a glance.
/// Refreshes every 5 seconds from the existing ViewModels.
/// </summary>
public sealed class DashboardViewModel : ReactiveObject, IDisposable
{
    private readonly DispatcherTimer _refreshTimer;

    // Source VMs (injected)
    private readonly AIBotViewModel       _aiBot;
    private readonly GridBotViewModel     _gridBot;
    private readonly DcaBotViewModel      _dcaBot;
    private readonly PnlDashboardService  _pnl;
    private readonly AllPositionsViewModel? _positions;
    private readonly NewsFeedViewModel?   _news;

    // ── Observable properties ──────────────────────────────────────────────────

    private string  _totalEquityLabel     = "--";
    private string  _pnlTodayLabel        = "--";
    private string  _pnlTodayBrush        = SemanticColor.Muted;
    private string  _openPositionsLabel   = "0";
    private string  _activeBotsSummary    = "No bots running";
    private string  _lastUpdated          = string.Empty;

    public string TotalEquityLabel   { get => _totalEquityLabel;   private set => this.RaiseAndSetIfChanged(ref _totalEquityLabel, value); }
    public string PnlTodayLabel      { get => _pnlTodayLabel;      private set => this.RaiseAndSetIfChanged(ref _pnlTodayLabel, value); }
    public string PnlTodayBrush      { get => _pnlTodayBrush;      private set => this.RaiseAndSetIfChanged(ref _pnlTodayBrush, value); }
    public string OpenPositionsLabel { get => _openPositionsLabel;  private set => this.RaiseAndSetIfChanged(ref _openPositionsLabel, value); }
    public string ActiveBotsSummary  { get => _activeBotsSummary;  private set => this.RaiseAndSetIfChanged(ref _activeBotsSummary, value); }
    public string LastUpdated        { get => _lastUpdated;         private set => this.RaiseAndSetIfChanged(ref _lastUpdated, value); }

    // ── Bot status cards ───────────────────────────────────────────────────────
    public ObservableCollection<BotStatusCard> BotCards { get; } = new();

    // ── Recent activity feed ───────────────────────────────────────────────────
    public ObservableCollection<DashboardActivityItem> RecentActivity { get; } = new();

    // ── Correlation warning ────────────────────────────────────────────────────
    private string _correlationWarning = string.Empty;
    public string CorrelationWarning { get => _correlationWarning; private set => this.RaiseAndSetIfChanged(ref _correlationWarning, value); }
    public bool HasCorrelationWarning => !string.IsNullOrEmpty(CorrelationWarning);

    // ── News market pulse + AI digest (mirrored from NewsFeedViewModel) ──────────
    private string _newsPulseLabel  = "No data";
    private string _newsPulseBrush  = SemanticColor.Muted;
    private string _newsPulseDetail = "Awaiting headlines";
    private string _newsAiDigest    = "AI digest pending…";
    private string _newsAiDigestBrush = SemanticColor.Muted;
    public string NewsPulseLabel    { get => _newsPulseLabel;    private set => this.RaiseAndSetIfChanged(ref _newsPulseLabel, value); }
    public string NewsPulseBrush    { get => _newsPulseBrush;    private set => this.RaiseAndSetIfChanged(ref _newsPulseBrush, value); }
    public string NewsPulseDetail   { get => _newsPulseDetail;   private set => this.RaiseAndSetIfChanged(ref _newsPulseDetail, value); }
    public string NewsAiDigest      { get => _newsAiDigest;      private set => this.RaiseAndSetIfChanged(ref _newsAiDigest, value); }
    public string NewsAiDigestBrush { get => _newsAiDigestBrush; private set => this.RaiseAndSetIfChanged(ref _newsAiDigestBrush, value); }
    public bool HasNews => _news is not null;

    // ── ctor ───────────────────────────────────────────────────────────────────

    public DashboardViewModel(
        AIBotViewModel       aiBot,
        GridBotViewModel     gridBot,
        DcaBotViewModel      dcaBot,
        PnlDashboardService  pnl,
        AllPositionsViewModel? positions = null,
        NewsFeedViewModel?   news = null)
    {
        _aiBot     = aiBot;
        _gridBot   = gridBot;
        _dcaBot    = dcaBot;
        _pnl       = pnl;
        _positions = positions;
        _news      = news;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        Refresh();
    }

    // ── Refresh ────────────────────────────────────────────────────────────────

    public void Refresh()
    {
        RefreshBotCards();
        RefreshPnlSummary();
        RefreshNews();
        LastUpdated = $"Updated {DateTime.Now:HH:mm:ss}";
    }

    private void RefreshNews()
    {
        if (_news is null) return;
        NewsPulseLabel    = _news.PulseLabel;
        NewsPulseBrush    = _news.PulseBrush;
        NewsPulseDetail   = _news.PulseDetail;
        NewsAiDigest      = _news.AiDigest;
        NewsAiDigestBrush = _news.AiDigestBrush;
    }

    private void RefreshBotCards()
    {
        SetCard(0, new BotStatusCard(
            "AI Bot",
            _aiBot.IsRunning ? "RUNNING" : "IDLE",
            _aiBot.IsRunning ? SemanticColor.Accent : SemanticColor.Muted,
            _aiBot.IsRunning ? $"{_aiBot.Symbol} · {_aiBot.SelectedStrategy}" : "Not started",
            "🤖"));

        SetCard(1, new BotStatusCard(
            "Grid Bot",
            _gridBot.IsRunning ? "RUNNING" : "IDLE",
            _gridBot.IsRunning ? SemanticColor.Accent : SemanticColor.Muted,
            _gridBot.IsRunning ? _gridBot.GridSummary : "Not started",
            "⚡"));

        SetCard(2, new BotStatusCard(
            "DCA Bot",
            _dcaBot.IsRunning ? "RUNNING" : "IDLE",
            _dcaBot.IsRunning ? SemanticColor.Accent : SemanticColor.Muted,
            _dcaBot.IsRunning ? _dcaBot.NextExecutionLabel : "Not started",
            "📈"));

        var runningCount = BotCards.Count(c => c.Status == "RUNNING");
        ActiveBotsSummary = runningCount == 0
            ? "No bots running"
            : $"{runningCount} bot{(runningCount > 1 ? "s" : "")} running";
    }

    /// <summary>
    /// Writes a card only when its content actually changed. The 5-second timer used to
    /// Clear() the collection and re-Add all three, which reset the list and rebuilt every
    /// row container even on a tick where nothing moved (records are compared by value).
    /// </summary>
    private void SetCard(int index, BotStatusCard card)
    {
        if (index >= BotCards.Count)
        {
            BotCards.Add(card);
            return;
        }

        if (BotCards[index] != card) BotCards[index] = card;
    }

    private void RefreshPnlSummary()
    {
        var records = _pnl.GetAll();
        var metrics = _pnl.ComputeMetrics(records);

        PnlTodayLabel = $"{(metrics.TotalPnlUsd >= 0 ? "+" : "")}{metrics.TotalPnlUsd:F2} USD";
        PnlTodayBrush = metrics.TotalPnlUsd >= 0 ? SemanticColor.Positive : SemanticColor.Negative;

        var posCount = _positions?.Rows.Count ?? 0;
        OpenPositionsLabel = posCount.ToString();

        // P&L equity (last value). The last point of the equity curve is the cumulative
        // realized P&L, which ComputeMetrics already summed — building (and sorting) the
        // whole N+1 point curve every 5 seconds for that single number was wasted work.
        TotalEquityLabel = records.Count > 0
            ? $"$ {metrics.TotalPnlUsd:N2}"
            : "$ 0.00";
    }

    public void AddActivity(string icon, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RecentActivity.Insert(0, new DashboardActivityItem(icon, message, DateTime.Now));
            if (RecentActivity.Count > 20) RecentActivity.RemoveAt(RecentActivity.Count - 1);
        });
    }

    public void Dispose() => _refreshTimer.Stop();
}

// ── Supporting types ──────────────────────────────────────────────────────────

public sealed record BotStatusCard(
    string Name,
    string Status,
    string StatusBrush,
    string Detail,
    string Icon);

public sealed record DashboardActivityItem(
    string Icon,
    string Message,
    DateTime Time)
{
    public string TimeLabel => Time.ToString("HH:mm:ss");
}
