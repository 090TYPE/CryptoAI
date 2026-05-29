using Avalonia.Threading;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ── Per-coin config row ──────────────────────────────────────────────────────
public class DcaCoinRowViewModel : ReactiveObject
{
    private string _symbol;
    private int _weightPercent;
    private bool _conditionalBuy;
    private int _maPeriod;
    private decimal _totalQty;
    private decimal _avgPrice;

    public string Symbol
    {
        get => _symbol;
        set => this.RaiseAndSetIfChanged(ref _symbol, value);
    }

    public int WeightPercent
    {
        get => _weightPercent;
        set => this.RaiseAndSetIfChanged(ref _weightPercent, Math.Clamp(value, 1, 100));
    }

    public bool ConditionalBuy
    {
        get => _conditionalBuy;
        set => this.RaiseAndSetIfChanged(ref _conditionalBuy, value);
    }

    public int MaPeriod
    {
        get => _maPeriod;
        set => this.RaiseAndSetIfChanged(ref _maPeriod, Math.Max(10, value));
    }

    public decimal TotalQty
    {
        get => _totalQty;
        set { this.RaiseAndSetIfChanged(ref _totalQty, value); this.RaisePropertyChanged(nameof(StatsLabel)); }
    }

    public decimal AvgPrice
    {
        get => _avgPrice;
        set { this.RaiseAndSetIfChanged(ref _avgPrice, value); this.RaisePropertyChanged(nameof(StatsLabel)); }
    }

    public string StatsLabel => _totalQty > 0
        ? $"avg {_avgPrice:N4} · held {_totalQty:N6}"
        : "—";

    public string Id { get; } = Guid.NewGuid().ToString();

    public DcaCoinRowViewModel(string symbol, int weight, bool conditional = false, int maPeriod = 200)
    {
        _symbol = symbol;
        _weightPercent = weight;
        _conditionalBuy = conditional;
        _maPeriod = maPeriod;
    }
}

// ── Execution history row ────────────────────────────────────────────────────
public class DcaExecutionRowViewModel
{
    public string TimeLabel { get; }
    public string Symbol { get; }
    public string PriceLabel { get; }
    public string QtyLabel { get; }
    public string TotalLabel { get; }
    public string StatusLabel { get; }
    public string StatusColor { get; }

    public DcaExecutionRowViewModel(DcaExecution e)
    {
        TimeLabel = e.ExecutedAt.ToLocalTime().ToString("dd.MM HH:mm");
        Symbol = e.Symbol;
        PriceLabel = e.Price > 0 ? $"{e.Price:N4}" : "—";
        QtyLabel = e.Quantity > 0 ? $"{e.Quantity:N6}" : "—";
        TotalLabel = e.TotalUsdt > 0 ? $"{e.TotalUsdt:N2}" : "—";
        StatusLabel = e.Executed ? "Executed" : "Skipped";
        StatusColor = e.Executed ? "#3DDC84" : "#F4B860";
    }
}

// ── Main DCA ViewModel ───────────────────────────────────────────────────────
public class DcaBotViewModel : ReactiveObject
{
    // Multi-exchange spot gateway map.
    // DCA по архитектуре работает только с спотом — фьючерсы здесь не нужны.
    private readonly Dictionary<string, IExchangeGateway> _gateways;
    private string _selectedExchange = "Binance";
    private DcaBot? _bot;

    // Schedule config
    private decimal _totalBudget = 100m;
    private string _selectedIntervalType = "Days";
    private int _intervalValue = 1;
    private bool _executeImmediately;

    // New coin form
    private string _newCoinSymbol = "BTCUSDT";
    private int _newCoinWeight = 50;
    private bool _newCoinConditional;
    private int _newCoinMaPeriod = 200;

    // State
    private bool _isRunning;
    private string _botLog = string.Empty;

    /// <summary>
    /// Forwarded from DcaBot.OnExecution — set by MainWindowViewModel to record into P&L dashboard.
    /// Only fires for executed (Executed == true) buys.
    /// </summary>
    public Action<DcaExecution>? OnExecutionForwarded;

    // Per-coin accumulators for average price tracking: symbol → (totalCost, totalQty)
    private readonly Dictionary<string, (decimal Cost, decimal Qty)> _avgAccum = new();

    // History persistence
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CryptoAITerminal", "dca_history.json");

    // 1-second countdown refresh
    private IDisposable? _countdownSub;

    public ObservableCollection<DcaCoinRowViewModel> Coins { get; } = new();
    public ObservableCollection<DcaExecutionRowViewModel> Executions { get; } = new();

    public decimal TotalBudget
    {
        get => _totalBudget;
        set => this.RaiseAndSetIfChanged(ref _totalBudget, Math.Max(1m, value));
    }

    public string SelectedIntervalType
    {
        get => _selectedIntervalType;
        set => this.RaiseAndSetIfChanged(ref _selectedIntervalType, value);
    }

    public int IntervalValue
    {
        get => _intervalValue;
        set => this.RaiseAndSetIfChanged(ref _intervalValue, Math.Max(1, value));
    }

    public bool ExecuteImmediately
    {
        get => _executeImmediately;
        set => this.RaiseAndSetIfChanged(ref _executeImmediately, value);
    }

    public string NewCoinSymbol
    {
        get => _newCoinSymbol;
        set => this.RaiseAndSetIfChanged(ref _newCoinSymbol, value);
    }

    public int NewCoinWeight
    {
        get => _newCoinWeight;
        set => this.RaiseAndSetIfChanged(ref _newCoinWeight, Math.Clamp(value, 1, 100));
    }

    public bool NewCoinConditional
    {
        get => _newCoinConditional;
        set => this.RaiseAndSetIfChanged(ref _newCoinConditional, value);
    }

    public int NewCoinMaPeriod
    {
        get => _newCoinMaPeriod;
        set => this.RaiseAndSetIfChanged(ref _newCoinMaPeriod, Math.Max(10, value));
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(CanStart));
            this.RaisePropertyChanged(nameof(CanStop));
        }
    }

    public string BotLog
    {
        get => _botLog;
        set => this.RaiseAndSetIfChanged(ref _botLog, value);
    }

    public string CountdownLabel
    {
        get
        {
            if (_bot == null || !_isRunning) return "—";
            var t = _bot.TimeUntilNext;
            if (t <= TimeSpan.Zero) return "Executing...";
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}h {t.Minutes:D2}m {t.Seconds:D2}s"
                : $"{t.Minutes:D2}m {t.Seconds:D2}s";
        }
    }

    public string NextExecutionLabel => _bot is not null && _isRunning
        ? _bot.NextExecutionUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
        : "—";

    public string WeightSumLabel
    {
        get
        {
            int sum = Coins.Sum(c => c.WeightPercent);
            return sum == 100 ? $"{sum}% ✓" : $"{sum}% (should be 100%)";
        }
    }

    public string WeightSumColor => Coins.Sum(c => c.WeightPercent) == 100 ? "#3DDC84" : "#F4B860";

    public bool CanStart => !_isRunning;
    public bool CanStop => _isRunning;

    public IReadOnlyList<string> IntervalTypes { get; } = ["Hours", "Days", "Weeks"];

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> RunNowCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCoinCommand { get; }
    public ReactiveCommand<string, Unit> RemoveCoinCommand { get; }

    public IReadOnlyList<string> AvailableExchanges { get; } = ["Binance", "Bybit", "OKX", "KuCoin"];

    public string SelectedExchange
    {
        get => _selectedExchange;
        set
        {
            var normalized = AvailableExchanges.Contains(value, StringComparer.OrdinalIgnoreCase)
                ? value : "Binance";
            this.RaiseAndSetIfChanged(ref _selectedExchange, normalized);
        }
    }

    /// <summary>
    /// Constructor для обратной совместимости — только Binance.
    /// </summary>
    public DcaBotViewModel(BinanceGateway gateway)
        : this(gateway, null, null, null)
    {
    }

    public DcaBotViewModel(
        IExchangeGateway binanceSpot,
        IExchangeGateway? bybitSpot,
        IExchangeGateway? okxSpot,
        IExchangeGateway? kucoinSpot = null)
    {
        _gateways = new Dictionary<string, IExchangeGateway>(StringComparer.OrdinalIgnoreCase)
        {
            ["Binance"] = binanceSpot,
        };
        if (bybitSpot  is not null) _gateways["Bybit"]  = bybitSpot;
        if (okxSpot    is not null) _gateways["OKX"]    = okxSpot;
        if (kucoinSpot is not null) _gateways["KuCoin"] = kucoinSpot;

        // Defaults: BTC 60% + ETH 40%
        Coins.Add(new DcaCoinRowViewModel("BTCUSDT", 60, false, 200));
        Coins.Add(new DcaCoinRowViewModel("ETHUSDT", 40, false, 200));
        Coins.CollectionChanged += (_, _) => RaiseWeightChanged();

        StartCommand = ReactiveCommand.CreateFromTask(StartAsync, outputScheduler: App.UiScheduler);
        StopCommand = ReactiveCommand.CreateFromTask(StopAsync, outputScheduler: App.UiScheduler);
        RunNowCommand = ReactiveCommand.CreateFromTask(RunNowAsync, outputScheduler: App.UiScheduler);
        AddCoinCommand = ReactiveCommand.Create(AddCoin, outputScheduler: App.UiScheduler);
        RemoveCoinCommand = ReactiveCommand.Create<string>(RemoveCoin, outputScheduler: App.UiScheduler);

        LoadHistory();
    }

    private async Task StartAsync()
    {
        if (_bot is not null) await StopAsync();
        if (Coins.Count == 0) { AppendLog("No coins configured."); return; }

        var intervalType = _selectedIntervalType switch
        {
            "Hours" => DcaIntervalType.Hours,
            "Weeks" => DcaIntervalType.Weeks,
            _ => DcaIntervalType.Days
        };

        var config = new DcaBotConfig
        {
            TotalBudgetPerCycleUsdt = _totalBudget,
            IntervalType = intervalType,
            IntervalValue = _intervalValue,
            Coins = Coins.Select(c => new DcaCoinEntry
            {
                Symbol = c.Symbol.Trim().ToUpperInvariant(),
                WeightPercent = c.WeightPercent,
                ConditionalBuyEnabled = c.ConditionalBuy,
                MaPeriod = c.MaPeriod
            }).ToList()
        };

        if (!_gateways.TryGetValue(_selectedExchange, out var gw))
            gw = _gateways["Binance"]; // fallback на Binance если выбранный гейтвей недоступен

        _bot = new DcaBot(gw, config);
        AppendLog($"[DCA] Exchange: {_selectedExchange}");
        _bot.OnLog += AppendLog;
        _bot.OnExecution += OnExecution;

        IsRunning = true;
        BotLog = string.Empty;

        _countdownSub = Observable.Interval(TimeSpan.FromSeconds(1), App.UiScheduler)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(CountdownLabel));
                this.RaisePropertyChanged(nameof(NextExecutionLabel));
            });

        await _bot.StartAsync(_executeImmediately);
    }

    private async Task StopAsync()
    {
        _countdownSub?.Dispose();
        _countdownSub = null;

        if (_bot is null) return;
        var bot = _bot;
        _bot = null;
        await bot.StopAsync();
        bot.Dispose();

        IsRunning = false;
        this.RaisePropertyChanged(nameof(CountdownLabel));
        this.RaisePropertyChanged(nameof(NextExecutionLabel));
    }

    private async Task RunNowAsync()
    {
        if (_bot is null) { AppendLog("Start the bot first."); return; }
        AppendLog("Manual cycle triggered...");
        await _bot.ForceExecuteNowAsync();
    }

    private void AddCoin()
    {
        var sym = _newCoinSymbol.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(sym)) return;
        Coins.Add(new DcaCoinRowViewModel(sym, _newCoinWeight, _newCoinConditional, _newCoinMaPeriod));
        RaiseWeightChanged();
    }

    private void RemoveCoin(string id)
    {
        var row = Coins.FirstOrDefault(c => c.Id == id);
        if (row is not null)
        {
            Coins.Remove(row);
            RaiseWeightChanged();
        }
    }

    private void OnExecution(DcaExecution e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Executions.Insert(0, new DcaExecutionRowViewModel(e));

            // Update average price for the coin row
            if (e.Executed && e.Quantity > 0)
            {
                var (prevCost, prevQty) = _avgAccum.GetValueOrDefault(e.Symbol, (0m, 0m));
                decimal newQty = prevQty + e.Quantity;
                decimal newCost = prevCost + e.TotalUsdt;
                _avgAccum[e.Symbol] = (newCost, newQty);

                var row = Coins.FirstOrDefault(c =>
                    string.Equals(c.Symbol, e.Symbol, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.TotalQty = newQty;
                    row.AvgPrice = newQty > 0 ? newCost / newQty : 0m;
                }
            }

            PersistExecution(e);

            // Forward to P&L dashboard (only actual executions, not skipped)
            if (e.Executed)
                OnExecutionForwarded?.Invoke(e);
        });
    }

    private void RaiseWeightChanged()
    {
        this.RaisePropertyChanged(nameof(WeightSumLabel));
        this.RaisePropertyChanged(nameof(WeightSumColor));
    }

    private void AppendLog(string msg) =>
        Dispatcher.UIThread.Post(() => BotLog += $"\n{DateTime.Now:HH:mm:ss}  {msg}");

    // ── History persistence ──────────────────────────────────────────────────

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var json = File.ReadAllText(HistoryPath);
            var list = JsonSerializer.Deserialize<List<DcaExecution>>(json);
            if (list is null) return;

            foreach (var e in list.Take(200))
            {
                Executions.Add(new DcaExecutionRowViewModel(e));

                if (e.Executed && e.Quantity > 0)
                {
                    var (prevCost, prevQty) = _avgAccum.GetValueOrDefault(e.Symbol, (0m, 0m));
                    _avgAccum[e.Symbol] = (prevCost + e.TotalUsdt, prevQty + e.Quantity);
                }
            }

            // Restore avg price stats on coin rows
            foreach (var coin in Coins)
            {
                if (_avgAccum.TryGetValue(coin.Symbol, out var acc) && acc.Qty > 0)
                {
                    coin.TotalQty = acc.Qty;
                    coin.AvgPrice = acc.Cost / acc.Qty;
                }
            }
        }
        catch { }
    }

    private void PersistExecution(DcaExecution e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            List<DcaExecution> existing = [];

            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                existing = JsonSerializer.Deserialize<List<DcaExecution>>(json) ?? [];
            }

            existing.Insert(0, e);
            if (existing.Count > 500) existing = existing.Take(500).ToList();

            var tmp = HistoryPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(existing));
            File.Move(tmp, HistoryPath, overwrite: true);
        }
        catch { }
    }
}
