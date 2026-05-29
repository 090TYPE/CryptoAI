using Avalonia.Media;
using Avalonia.Threading;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// ViewModel for Statistical Arbitrage (Pairs Trading).
/// Monitors log-ratio spread between two correlated pairs,
/// enters when z-score diverges and exits on mean-reversion.
/// </summary>
public sealed class StatArbViewModel : ReactiveObject, IDisposable
{
    private StatArbService? _svc;
    private readonly IExchangeGateway _gateway;

    // ── Config fields ─────────────────────────────────────────────────────────

    private string  _symbolA     = "BTCUSDT";
    private string  _symbolB     = "ETHUSDT";
    private int     _window      = 50;
    private decimal _entryZScore = 2.0m;
    private decimal _exitZScore  = 0.5m;
    private decimal _notionalUsd = 500m;

    public string  SymbolA     { get => _symbolA;     set => this.RaiseAndSetIfChanged(ref _symbolA, value.ToUpperInvariant().Trim()); }
    public string  SymbolB     { get => _symbolB;     set => this.RaiseAndSetIfChanged(ref _symbolB, value.ToUpperInvariant().Trim()); }
    public int     Window      { get => _window;      set => this.RaiseAndSetIfChanged(ref _window, Math.Clamp(value, 5, 500)); }
    public decimal EntryZScore { get => _entryZScore; set => this.RaiseAndSetIfChanged(ref _entryZScore, Math.Max(0.5m, value)); }
    public decimal ExitZScore  { get => _exitZScore;  set => this.RaiseAndSetIfChanged(ref _exitZScore, Math.Max(0.1m, value)); }
    public decimal NotionalUsd { get => _notionalUsd; set => this.RaiseAndSetIfChanged(ref _notionalUsd, Math.Max(10m, value)); }

    // ── State ─────────────────────────────────────────────────────────────────

    private bool    _isRunning;
    private decimal _currentZScore;
    private decimal _currentSpread;
    private string  _positionLabel  = "No position";
    private string  _pnlLabel       = "—";
    private string  _statusLabel    = "Stopped";
    private string  _zScoreLabel    = "—";

    public bool    IsRunning      { get => _isRunning;      private set => this.RaiseAndSetIfChanged(ref _isRunning, value); }
    public decimal CurrentZScore  { get => _currentZScore;  private set => this.RaiseAndSetIfChanged(ref _currentZScore, value); }
    public decimal CurrentSpread  { get => _currentSpread;  private set => this.RaiseAndSetIfChanged(ref _currentSpread, value); }
    public string  PositionLabel  { get => _positionLabel;  private set => this.RaiseAndSetIfChanged(ref _positionLabel, value); }
    public string  PnlLabel       { get => _pnlLabel;       private set => this.RaiseAndSetIfChanged(ref _pnlLabel, value); }
    public string  StatusLabel    { get => _statusLabel;    private set => this.RaiseAndSetIfChanged(ref _statusLabel, value); }
    public string  ZScoreLabel    { get => _zScoreLabel;    private set => this.RaiseAndSetIfChanged(ref _zScoreLabel, value); }

    public string SpreadLabel => _currentSpread == 0m ? "—" : $"{_currentSpread:N4}";

    public IBrush ZScoreBrush =>
        Math.Abs(_currentZScore) >= _entryZScore ? new SolidColorBrush(Color.Parse("#FF6B6B")) :
        Math.Abs(_currentZScore) >= _exitZScore  ? new SolidColorBrush(Color.Parse("#F4B860")) :
                                                    new SolidColorBrush(Color.Parse("#21E6C1"));

    // ── Log ───────────────────────────────────────────────────────────────────

    public ObservableCollection<string> LogLines { get; } = [];

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand  { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public StatArbViewModel(IExchangeGateway gateway)
    {
        _gateway = gateway;

        StartCommand = ReactiveCommand.CreateFromTask(StartAsync);
        StopCommand  = ReactiveCommand.CreateFromTask(StopAsync);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private async Task StartAsync()
    {
        if (IsRunning) return;

        var cfg = new StatArbConfig
        {
            SymbolA     = _symbolA,
            SymbolB     = _symbolB,
            Window      = _window,
            EntryZScore = _entryZScore,
            ExitZScore  = _exitZScore,
            NotionalUsd = _notionalUsd,
        };

        _svc = new StatArbService(_gateway, cfg);
        _svc.LogMessage    += OnLog;
        _svc.StateChanged  += OnStateChanged;

        StatusLabel = "Starting...";
        await _svc.StartAsync();
        IsRunning   = true;
        StatusLabel = $"Running — {_symbolA}/{_symbolB}";
    }

    private async Task StopAsync()
    {
        if (_svc is null) return;
        StatusLabel = "Stopping...";
        await _svc.StopAsync();
        IsRunning   = false;
        StatusLabel = "Stopped";
        PositionLabel = "No position";
        PnlLabel      = "—";
        ZScoreLabel   = "—";
        _svc.LogMessage   -= OnLog;
        _svc.StateChanged -= OnStateChanged;
        _svc.Dispose();
        _svc = null;
    }

    private void OnStateChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_svc is null) return;

            CurrentZScore = _svc.CurrentZScore;
            CurrentSpread = _svc.CurrentSpread;
            ZScoreLabel   = $"{_svc.CurrentZScore:+0.00;-0.00}";
            this.RaisePropertyChanged(nameof(ZScoreBrush));
            this.RaisePropertyChanged(nameof(SpreadLabel));

            var pos = _svc.CurrentPosition;
            if (pos is null)
            {
                PositionLabel = "No position";
                PnlLabel      = "—";
            }
            else
            {
                var dir = pos.Direction == StatArbPositionDirection.LongAShortB
                    ? $"Long {pos.SymbolA} / Short {pos.SymbolB}"
                    : $"Short {pos.SymbolA} / Long {pos.SymbolB}";
                PositionLabel = dir;
                PnlLabel      = pos.PnlUsd >= 0
                    ? $"+${pos.PnlUsd:N2}"
                    : $"-${Math.Abs(pos.PnlUsd):N2}";
            }
        });
    }

    private void OnLog(string msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogLines.Insert(0, msg);
            while (LogLines.Count > 300) LogLines.RemoveAt(LogLines.Count - 1);
        });
    }

    public void Dispose()
    {
        if (_svc is not null)
        {
            _svc.LogMessage   -= OnLog;
            _svc.StateChanged -= OnStateChanged;
            _svc.Dispose();
        }
    }
}
