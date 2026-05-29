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

    // ── Observable properties ──────────────────────────────────────────────────

    private string  _totalEquityLabel     = "--";
    private string  _pnlTodayLabel        = "--";
    private string  _pnlTodayBrush        = "#8FA3B8";
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

    // ── ctor ───────────────────────────────────────────────────────────────────

    public DashboardViewModel(
        AIBotViewModel       aiBot,
        GridBotViewModel     gridBot,
        DcaBotViewModel      dcaBot,
        PnlDashboardService  pnl,
        AllPositionsViewModel? positions = null)
    {
        _aiBot     = aiBot;
        _gridBot   = gridBot;
        _dcaBot    = dcaBot;
        _pnl       = pnl;
        _positions = positions;

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
        LastUpdated = $"Updated {DateTime.Now:HH:mm:ss}";
    }

    private void RefreshBotCards()
    {
        BotCards.Clear();

        BotCards.Add(new BotStatusCard(
            "AI Bot",
            _aiBot.IsRunning ? "RUNNING" : "IDLE",
            _aiBot.IsRunning ? "#21E6C1" : "#8FA3B8",
            _aiBot.IsRunning ? $"{_aiBot.Symbol} · {_aiBot.SelectedStrategy}" : "Not started",
            "🤖"));

        BotCards.Add(new BotStatusCard(
            "Grid Bot",
            _gridBot.IsRunning ? "RUNNING" : "IDLE",
            _gridBot.IsRunning ? "#21E6C1" : "#8FA3B8",
            _gridBot.IsRunning ? _gridBot.GridSummary : "Not started",
            "⚡"));

        BotCards.Add(new BotStatusCard(
            "DCA Bot",
            _dcaBot.IsRunning ? "RUNNING" : "IDLE",
            _dcaBot.IsRunning ? "#21E6C1" : "#8FA3B8",
            _dcaBot.IsRunning ? _dcaBot.NextExecutionLabel : "Not started",
            "📈"));

        var runningCount = BotCards.Count(c => c.Status == "RUNNING");
        ActiveBotsSummary = runningCount == 0
            ? "No bots running"
            : $"{runningCount} bot{(runningCount > 1 ? "s" : "")} running";
    }

    private void RefreshPnlSummary()
    {
        var records = _pnl.GetAll();
        var metrics = _pnl.ComputeMetrics(records);

        PnlTodayLabel = $"{(metrics.TotalPnlUsd >= 0 ? "+" : "")}{metrics.TotalPnlUsd:F2} USD";
        PnlTodayBrush = metrics.TotalPnlUsd >= 0 ? "#3DDC84" : "#FF6B6B";

        var posCount = _positions?.Rows.Count ?? 0;
        OpenPositionsLabel = posCount.ToString();

        // P&L equity (last value)
        var equityPoints = _pnl.ComputeEquityCurve(records);
        if (equityPoints.Count >= 2)
        {
            var latest = equityPoints[^1].Equity;
            TotalEquityLabel = $"$ {latest:N2}";
        }
        else
        {
            TotalEquityLabel = "$ 0.00";
        }
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
