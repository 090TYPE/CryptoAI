using Avalonia.Threading;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public enum CopyTradingMode { Off, Leader, Follower }

public sealed class CopyTradeLogEntry
{
    public string Time    { get; init; } = "";
    public string Message { get; init; } = "";
    public bool   IsError { get; init; }
}

/// <summary>
/// ViewModel for the Copy Trading workspace.
/// Leader mode: publishes every bot trade to /api/copy-trades.
/// Follower mode: polls a remote leader URL and mirrors trades.
/// </summary>
public sealed class CopyTradingViewModel : ReactiveObject, IDisposable
{
    private readonly CopyTradingLeaderService   _leader;
    private readonly CopyTradingFollowerService _follower;

    // ── Mode ──────────────────────────────────────────────────────────────────

    private CopyTradingMode _mode = CopyTradingMode.Off;
    public CopyTradingMode Mode
    {
        get => _mode;
        set
        {
            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(IsLeader));
            this.RaisePropertyChanged(nameof(IsFollower));
            this.RaisePropertyChanged(nameof(ModeLabel));
            ApplyMode();
        }
    }

    public bool IsLeader   => _mode == CopyTradingMode.Leader;
    public bool IsFollower => _mode == CopyTradingMode.Follower;
    public string ModeLabel => _mode switch
    {
        CopyTradingMode.Leader   => "Leader — publishing trades",
        CopyTradingMode.Follower => "Follower — mirroring leader",
        _                        => "Off",
    };

    // ── Follower config ───────────────────────────────────────────────────────

    private string  _leaderUrl    = "http://localhost:5180";
    private decimal _scaleRatio   = 1.0m;
    private decimal _perfFeePct   = 20m;

    public string LeaderUrl
    {
        get => _leaderUrl;
        set
        {
            this.RaiseAndSetIfChanged(ref _leaderUrl, value);
            _follower.LeaderUrl = value;
        }
    }

    public decimal ScaleRatio
    {
        get => _scaleRatio;
        set
        {
            this.RaiseAndSetIfChanged(ref _scaleRatio, Math.Clamp(Math.Round(value, 2), 0.01m, 10m));
            _follower.ScaleRatio = _scaleRatio;
        }
    }

    public decimal PerformanceFeePct
    {
        get => _perfFeePct;
        set
        {
            this.RaiseAndSetIfChanged(ref _perfFeePct, Math.Clamp(value, 0m, 100m));
            _follower.PerformanceFeePct = _perfFeePct;
        }
    }

    // ── KPI ───────────────────────────────────────────────────────────────────

    private int     _totalPublished;
    private int     _totalCopied;
    private decimal _estimatedFeeEarned;

    public int     TotalPublished      { get => _totalPublished;     private set => this.RaiseAndSetIfChanged(ref _totalPublished, value); }
    public int     TotalCopied         { get => _totalCopied;        private set => this.RaiseAndSetIfChanged(ref _totalCopied, value); }
    public decimal EstimatedFeeEarned  { get => _estimatedFeeEarned; private set => this.RaiseAndSetIfChanged(ref _estimatedFeeEarned, value); }

    public string FeeLabel => $"${EstimatedFeeEarned:N2}";

    // ── Log ───────────────────────────────────────────────────────────────────

    public ObservableCollection<CopyTradeLogEntry> Log { get; } = [];

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SetLeaderCommand   { get; }
    public ReactiveCommand<Unit, Unit> SetFollowerCommand { get; }
    public ReactiveCommand<Unit, Unit> SetOffCommand      { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public CopyTradingViewModel(
        CopyTradingLeaderService leader,
        CopyTradingFollowerService follower)
    {
        _leader   = leader;
        _follower = follower;

        _follower.LeaderUrl        = _leaderUrl;
        _follower.ScaleRatio       = _scaleRatio;
        _follower.PerformanceFeePct = _perfFeePct;

        _leader.TradePublished += OnTradePublished;
        _follower.ExecutionCompleted += OnExecutionCompleted;
        _follower.LogMessage += msg => AddLog(msg);

        SetLeaderCommand   = ReactiveCommand.Create(() => { Mode = CopyTradingMode.Leader; });
        SetFollowerCommand = ReactiveCommand.Create(() => { Mode = CopyTradingMode.Follower; });
        SetOffCommand      = ReactiveCommand.Create(() => { Mode = CopyTradingMode.Off; });
    }

    public void SetFollowerGateway(IExchangeGateway? gw) => _follower.Gateway = gw;

    // ── Internal ──────────────────────────────────────────────────────────────

    private void ApplyMode()
    {
        switch (_mode)
        {
            case CopyTradingMode.Leader:
                _follower.Stop();
                AddLog("Leader mode active — all bot trades will be published.");
                break;
            case CopyTradingMode.Follower:
                _follower.Start();
                AddLog($"Follower started — polling {_leaderUrl}");
                break;
            case CopyTradingMode.Off:
                _follower.Stop();
                AddLog("Copy trading disabled.");
                break;
        }
    }

    private void OnTradePublished(CopyTrade t)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TotalPublished++;
            AddLog($"Published: {t.Side} {t.Quantity} {t.Symbol} @ {t.Price:N4} [{t.Source}]");
        });
    }

    private void OnExecutionCompleted(CopyExecution e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TotalCopied = _follower.TotalCopied;
            EstimatedFeeEarned = _follower.EstimatedFeesEarned;
            this.RaisePropertyChanged(nameof(FeeLabel));
        });
    }

    private void AddLog(string msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var entry = new CopyTradeLogEntry
            {
                Time    = DateTime.Now.ToString("HH:mm:ss"),
                Message = msg,
            };
            Log.Insert(0, entry);
            while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
        });
    }

    public void Dispose()
    {
        _leader.TradePublished       -= OnTradePublished;
        _follower.ExecutionCompleted -= OnExecutionCompleted;
        _follower.Stop();
    }
}
