using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class GridBotViewModel : ReactiveObject
{
    private readonly IExchangeGateway _spotGateway;
    private readonly IExchangeGateway _futuresGateway;
    private GridBot? _bot;

    private string _symbol = "BTCUSDT";
    private decimal _lowerPrice = 90000m;
    private decimal _upperPrice = 100000m;
    private int _gridLevels = 10;
    private decimal _quantityPerGrid = 0.001m;
    private string _selectedMarketMode = "Spot";
    private int _leverage = 3;
    private string _selectedMarginMode = "Cross";

    private bool _isRunning;
    private bool _isPaused;
    private int _cyclesCompleted;
    private decimal _gridPnL;
    private string _botLog = string.Empty;

    /// <summary>
    /// Fired each time a grid cycle completes. Args: symbol, buyPrice, sellPrice, qty, profit.
    /// Set by MainWindowViewModel to record trades into P&L dashboard.
    /// </summary>
    public Action<string, decimal, decimal, decimal, decimal>? OnCycleClosed;

    public string Symbol
    {
        get => _symbol;
        set { this.RaiseAndSetIfChanged(ref _symbol, value); this.RaisePropertyChanged(nameof(GridSummary)); }
    }

    public decimal LowerPrice
    {
        get => _lowerPrice;
        set { this.RaiseAndSetIfChanged(ref _lowerPrice, value); this.RaisePropertyChanged(nameof(GridSpacing)); this.RaisePropertyChanged(nameof(GridSummary)); }
    }

    public decimal UpperPrice
    {
        get => _upperPrice;
        set { this.RaiseAndSetIfChanged(ref _upperPrice, value); this.RaisePropertyChanged(nameof(GridSpacing)); this.RaisePropertyChanged(nameof(GridSummary)); }
    }

    public int GridLevels
    {
        get => _gridLevels;
        set { this.RaiseAndSetIfChanged(ref _gridLevels, Math.Max(2, value)); this.RaisePropertyChanged(nameof(GridSpacing)); this.RaisePropertyChanged(nameof(GridSummary)); }
    }

    public decimal QuantityPerGrid
    {
        get => _quantityPerGrid;
        set => this.RaiseAndSetIfChanged(ref _quantityPerGrid, value);
    }

    public string SelectedMarketMode
    {
        get => _selectedMarketMode;
        set { this.RaiseAndSetIfChanged(ref _selectedMarketMode, value); this.RaisePropertyChanged(nameof(IsFuturesMode)); }
    }

    public int Leverage
    {
        get => _leverage;
        set => this.RaiseAndSetIfChanged(ref _leverage, Math.Max(1, value));
    }

    public string SelectedMarginMode
    {
        get => _selectedMarginMode;
        set => this.RaiseAndSetIfChanged(ref _selectedMarginMode, value);
    }

    public bool IsFuturesMode => string.Equals(_selectedMarketMode, "Futures", StringComparison.OrdinalIgnoreCase);

    public bool IsRunning
    {
        get => _isRunning;
        private set { this.RaiseAndSetIfChanged(ref _isRunning, value); this.RaisePropertyChanged(nameof(StatusLabel)); this.RaisePropertyChanged(nameof(CanStart)); this.RaisePropertyChanged(nameof(CanStop)); this.RaisePropertyChanged(nameof(CanPause)); this.RaisePropertyChanged(nameof(CanResume)); }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set { this.RaiseAndSetIfChanged(ref _isPaused, value); this.RaisePropertyChanged(nameof(StatusLabel)); this.RaisePropertyChanged(nameof(CanPause)); this.RaisePropertyChanged(nameof(CanResume)); }
    }

    public int CyclesCompleted
    {
        get => _cyclesCompleted;
        private set => this.RaiseAndSetIfChanged(ref _cyclesCompleted, value);
    }

    public decimal GridPnL
    {
        get => _gridPnL;
        private set { this.RaiseAndSetIfChanged(ref _gridPnL, value); this.RaisePropertyChanged(nameof(GridPnLLabel)); this.RaisePropertyChanged(nameof(GridPnLColor)); }
    }

    public string BotLog
    {
        get => _botLog;
        set => this.RaiseAndSetIfChanged(ref _botLog, value);
    }

    public decimal GridSpacing => _gridLevels > 0 && _upperPrice > _lowerPrice
        ? (_upperPrice - _lowerPrice) / _gridLevels
        : 0m;

    public string GridSummary => GridSpacing > 0
        ? $"{_gridLevels} levels · spacing {GridSpacing:N2} · qty {_quantityPerGrid} each"
        : "Set Lower < Upper to preview";

    public string GridPnLLabel => _gridPnL >= 0 ? $"+{_gridPnL:N4} USDT" : $"{_gridPnL:N4} USDT";
    public string GridPnLColor => _gridPnL >= 0 ? "#3DDC84" : "#FF5D73";
    public string StatusLabel => !_isRunning ? "Stopped" : (_isPaused ? "Paused" : "Running");
    public string StatusColor => _isRunning && !_isPaused ? "#3DDC84" : (_isPaused ? "#F4B860" : "#8FA3B8");

    public bool CanStart => !_isRunning;
    public bool CanStop => _isRunning;
    public bool CanPause => _isRunning && !_isPaused;
    public bool CanResume => _isRunning && _isPaused;

    public IReadOnlyList<string> AvailableMarketModes { get; } = ["Spot", "Futures"];
    public IReadOnlyList<string> AvailableMarginModes { get; } = ["Cross", "Isolated"];

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseCommand { get; }
    public ReactiveCommand<Unit, Unit> ResumeCommand { get; }

    public GridBotViewModel(BinanceGateway spotGateway, BinanceFuturesGateway futuresGateway)
    {
        _spotGateway = spotGateway;
        _futuresGateway = futuresGateway;

        StartCommand = ReactiveCommand.CreateFromTask(StartAsync, outputScheduler: App.UiScheduler);
        StopCommand = ReactiveCommand.CreateFromTask(StopAsync, outputScheduler: App.UiScheduler);
        PauseCommand = ReactiveCommand.CreateFromTask(PauseAsync, outputScheduler: App.UiScheduler);
        ResumeCommand = ReactiveCommand.CreateFromTask(ResumeAsync, outputScheduler: App.UiScheduler);
    }

    private async Task StartAsync()
    {
        if (_bot is not null) await StopAsync();

        if (_upperPrice <= _lowerPrice || _gridLevels < 2)
        {
            BotLog = "Error: Upper > Lower required and GridLevels >= 2";
            return;
        }

        var gateway = IsFuturesMode ? _futuresGateway : _spotGateway;
        var marketType = IsFuturesMode ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot;
        var marginMode = string.Equals(_selectedMarginMode, "Isolated", StringComparison.OrdinalIgnoreCase)
            ? FuturesMarginMode.Isolated : FuturesMarginMode.Cross;

        var config = new GridBotConfig
        {
            Symbol = Symbol,
            LowerPrice = LowerPrice,
            UpperPrice = UpperPrice,
            GridLevels = GridLevels,
            QuantityPerGrid = QuantityPerGrid,
            MarketType = marketType,
            Leverage = Leverage,
            MarginMode = marginMode
        };

        _bot = new GridBot(gateway, config);
        _bot.OnLog += AppendLog;
        _bot.OnStatsChanged += () =>
        {
            if (_bot is null) return;
            CyclesCompleted = _bot.CyclesCompleted;
            GridPnL = _bot.GridPnL;
        };
        _bot.OnCycleCompleted += (sym, buy, sell, qty, profit) =>
            OnCycleClosed?.Invoke(sym, buy, sell, qty, profit);

        BotLog = string.Empty;
        CyclesCompleted = 0;
        GridPnL = 0m;
        IsRunning = true;
        IsPaused = false;

        await _bot.StartAsync();
    }

    private async Task StopAsync()
    {
        if (_bot is null) return;
        var bot = _bot;
        _bot = null;
        await bot.StopAsync();
        bot.Dispose();
        IsRunning = false;
        IsPaused = false;
    }

    private async Task PauseAsync()
    {
        if (_bot is null || !_isRunning || _isPaused) return;
        await _bot.PauseAsync();
        IsPaused = true;
    }

    private async Task ResumeAsync()
    {
        if (_bot is null || !_isPaused) return;
        await _bot.ResumeAsync();
        IsPaused = false;
    }

    private void AppendLog(string msg) =>
        BotLog += $"\n{DateTime.Now:HH:mm:ss}  {msg}";

    public void StopSync() => StopAsync().ConfigureAwait(false);
}
