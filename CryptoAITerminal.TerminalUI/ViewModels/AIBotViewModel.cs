using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.TerminalUI;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class AIBotViewModel : ReactiveObject
{
    private readonly IExchangeGateway _binanceSpot;
    private readonly IExchangeGateway _binanceFutures;
    private readonly IExchangeGateway? _bybitSpot;
    private readonly IExchangeGateway? _bybitFutures;
    private readonly IExchangeGateway? _okxSpot;
    private readonly IExchangeGateway? _okxFutures;
    private readonly IExchangeGateway? _kucoinSpot;
    private readonly IExchangeGateway? _kucoinFutures;

    /// Optional — set by MainWindowViewModel to record bot trades in the P&amp;L dashboard.
    public Action<string, string, decimal, decimal, decimal, decimal>? OnBotTradeClosed { get; set; }
    private TradingBot? _bot;
    private string _selectedExchange = "Binance";
    private bool _isRunning;
    private string _symbol = "BTCUSDT";
    private decimal _quantity = 0.001m;
    private decimal _maxRiskPerTrade = 100m;
    private string _selectedMarketMode = "Spot";
    private int _futuresLeverage = 3;
    private string _selectedFuturesMarginMode = "Cross";
    private string _selectedFuturesBias = "Long & Short";
    private string _botLog = string.Empty;
    private int _maFastPeriod = 10;
    private int _maSlowPeriod = 30;

    // Strategy selection + tunables per strategy
    private string _selectedStrategy = "MA Cross";
    private int _rsiPeriod = 14;
    private decimal _rsiOverbought = 70m;
    private decimal _rsiOversold = 30m;
    private int _bbPeriod = 20;
    private decimal _bbDeviation = 2.0m;
    private int _breakoutPeriod = 20;
    private int _macdFast = 12;
    private int _macdSlow = 26;
    private int _macdSignal = 9;
    private decimal _vwapBandPct = 0.05m;

    // Claude API strategy
    private string _claudeApiKey = string.Empty;
    private string _claudeModel  = "claude-sonnet-4-6";
    private int _claudePollSeconds = 60;

    // TP / SL
    private bool _tpEnabled = true;
    private decimal _tpPercent = 2.0m;
    private bool _slEnabled = true;
    private decimal _slPercent = 1.0m;
    private bool _trailingStop;
    private bool _partialTp;
    private decimal _partialTpClosePercent = 50m;
    private decimal _partialTp2Percent = 4.0m;

    public bool IsRunning
    {
        get => _isRunning;
        set => this.RaiseAndSetIfChanged(ref _isRunning, value);
    }

    public string Symbol
    {
        get => _symbol;
        set => this.RaiseAndSetIfChanged(ref _symbol, value);
    }

    public decimal Quantity
    {
        get => _quantity;
        set => this.RaiseAndSetIfChanged(ref _quantity, value);
    }

    public decimal MaxRiskPerTrade
    {
        get => _maxRiskPerTrade;
        set => this.RaiseAndSetIfChanged(ref _maxRiskPerTrade, value);
    }

    public string SelectedMarketMode
    {
        get => _selectedMarketMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMarketMode, value);
            this.RaisePropertyChanged(nameof(IsFuturesMode));
            this.RaisePropertyChanged(nameof(BotModeSummary));
        }
    }

    public int FuturesLeverage
    {
        get => _futuresLeverage;
        set
        {
            var normalized = value < 1 ? 1 : value;
            this.RaiseAndSetIfChanged(ref _futuresLeverage, normalized);
            this.RaisePropertyChanged(nameof(BotModeSummary));
        }
    }

    public string SelectedFuturesMarginMode
    {
        get => _selectedFuturesMarginMode;
        set
        {
            var normalized = string.Equals(value, "Isolated", StringComparison.OrdinalIgnoreCase) ? "Isolated" : "Cross";
            this.RaiseAndSetIfChanged(ref _selectedFuturesMarginMode, normalized);
            this.RaisePropertyChanged(nameof(BotModeSummary));
        }
    }

    public string SelectedFuturesBias
    {
        get => _selectedFuturesBias;
        set
        {
            var normalized = value switch
            {
                "Long Only" => "Long Only",
                "Short Only" => "Short Only",
                _ => "Long & Short"
            };
            this.RaiseAndSetIfChanged(ref _selectedFuturesBias, normalized);
            this.RaisePropertyChanged(nameof(BotModeSummary));
        }
    }

    public bool IsFuturesMode => string.Equals(SelectedMarketMode, "Futures", StringComparison.OrdinalIgnoreCase);
    public int MaFastPeriod
    {
        get => _maFastPeriod;
        set => this.RaiseAndSetIfChanged(ref _maFastPeriod, Math.Max(2, value));
    }

    public int MaSlowPeriod
    {
        get => _maSlowPeriod;
        set => this.RaiseAndSetIfChanged(ref _maSlowPeriod, Math.Max(_maFastPeriod + 1, value));
    }

    public IReadOnlyList<string> AvailableStrategies { get; } =
        ["MA Cross", "RSI", "Bollinger Bands", "Breakout", "MACD", "VWAP", "AI (Claude)"];

    public string ClaudeApiKey
    {
        get => _claudeApiKey;
        set => this.RaiseAndSetIfChanged(ref _claudeApiKey, value ?? string.Empty);
    }

    public string ClaudeModel
    {
        get => _claudeModel;
        set => this.RaiseAndSetIfChanged(ref _claudeModel, string.IsNullOrWhiteSpace(value) ? "claude-sonnet-4-6" : value);
    }

    public int ClaudePollSeconds
    {
        get => _claudePollSeconds;
        set { this.RaiseAndSetIfChanged(ref _claudePollSeconds, Math.Clamp(value, 15, 3600)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public string SelectedStrategy
    {
        get => _selectedStrategy;
        set
        {
            var normalized = AvailableStrategies.Contains(value) ? value : "MA Cross";
            this.RaiseAndSetIfChanged(ref _selectedStrategy, normalized);
            this.RaisePropertyChanged(nameof(StrategySummary));
        }
    }

    public int RsiPeriod
    {
        get => _rsiPeriod;
        set { this.RaiseAndSetIfChanged(ref _rsiPeriod, Math.Max(2, value)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public decimal RsiOverbought
    {
        get => _rsiOverbought;
        set { this.RaiseAndSetIfChanged(ref _rsiOverbought, Math.Clamp(value, 50m, 100m)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public decimal RsiOversold
    {
        get => _rsiOversold;
        set { this.RaiseAndSetIfChanged(ref _rsiOversold, Math.Clamp(value, 0m, 50m)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public int BbPeriod
    {
        get => _bbPeriod;
        set { this.RaiseAndSetIfChanged(ref _bbPeriod, Math.Max(2, value)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public decimal BbDeviation
    {
        get => _bbDeviation;
        set { this.RaiseAndSetIfChanged(ref _bbDeviation, Math.Max(0.1m, value)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public int BreakoutPeriod
    {
        get => _breakoutPeriod;
        set { this.RaiseAndSetIfChanged(ref _breakoutPeriod, Math.Max(2, value)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public int MacdFast
    {
        get => _macdFast;
        set { this.RaiseAndSetIfChanged(ref _macdFast, Math.Max(2, value)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public int MacdSlow
    {
        get => _macdSlow;
        set { this.RaiseAndSetIfChanged(ref _macdSlow, Math.Max(_macdFast + 1, value)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public int MacdSignal
    {
        get => _macdSignal;
        set { this.RaiseAndSetIfChanged(ref _macdSignal, Math.Max(2, value)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public decimal VwapBandPct
    {
        get => _vwapBandPct;
        set { this.RaiseAndSetIfChanged(ref _vwapBandPct, Math.Clamp(value, 0m, 5m)); this.RaisePropertyChanged(nameof(StrategySummary)); }
    }

    public string StrategySummary => _selectedStrategy switch
    {
        "RSI"             => $"RSI({_rsiPeriod}, OS={_rsiOversold:0}/OB={_rsiOverbought:0}) · Wilder's smoothing",
        "Bollinger Bands" => $"BB({_bbPeriod}, {_bbDeviation:0.#}σ)",
        "Breakout"        => $"Breakout({_breakoutPeriod})",
        "MACD"            => $"MACD({_macdFast}/{_macdSlow}/{_macdSignal})",
        "VWAP"            => $"VWAP(band={_vwapBandPct:0.##}%)",
        "AI (Claude)"     => $"Claude {_claudeModel} · poll every {_claudePollSeconds}s",
        _                 => $"SMA({_maFastPeriod}/{_maSlowPeriod})"
    };

    private CryptoAITerminal.Core.Interfaces.IStrategy CreateStrategy()
    {
        switch (_selectedStrategy)
        {
            case "RSI":             return new RsiStrategy(_rsiPeriod, _rsiOverbought, _rsiOversold);
            case "Bollinger Bands": return new BollingerBandsStrategy(_bbPeriod, _bbDeviation);
            case "Breakout":        return new BreakoutStrategy(_breakoutPeriod);
            case "MACD":            return new MacdStrategy(_macdFast, _macdSlow, _macdSignal);
            case "VWAP":            return new VwapStrategy(_vwapBandPct);
            case "AI (Claude)":
            {
                if (string.IsNullOrWhiteSpace(_claudeApiKey))
                {
                    BotLog += "\n[Claude] API key is empty — falling back to MA Cross.";
                    return new SimpleMaStrategy(_maFastPeriod, _maSlowPeriod);
                }
                var provider = new CryptoAITerminal.AIEngine.ClaudeSignalProvider(_claudeApiKey, _claudeModel);
                return new CryptoAITerminal.AIEngine.ClaudeStrategy(
                    provider, Symbol,
                    minPollInterval: TimeSpan.FromSeconds(_claudePollSeconds),
                    logger: msg => BotLog += $"\n{msg}");
            }
            default: return new SimpleMaStrategy(_maFastPeriod, _maSlowPeriod);
        }
    }

    public string BotLog
    {
        get => _botLog;
        set => this.RaiseAndSetIfChanged(ref _botLog, value);
    }

    public bool TpEnabled
    {
        get => _tpEnabled;
        set { this.RaiseAndSetIfChanged(ref _tpEnabled, value); this.RaisePropertyChanged(nameof(TpSlSummary)); }
    }

    public decimal TpPercent
    {
        get => _tpPercent;
        set { this.RaiseAndSetIfChanged(ref _tpPercent, Math.Max(0.1m, value)); this.RaisePropertyChanged(nameof(TpSlSummary)); }
    }

    public bool SlEnabled
    {
        get => _slEnabled;
        set { this.RaiseAndSetIfChanged(ref _slEnabled, value); this.RaisePropertyChanged(nameof(TpSlSummary)); }
    }

    public decimal SlPercent
    {
        get => _slPercent;
        set { this.RaiseAndSetIfChanged(ref _slPercent, Math.Max(0.1m, value)); this.RaisePropertyChanged(nameof(TpSlSummary)); }
    }

    public bool TrailingStop
    {
        get => _trailingStop;
        set { this.RaiseAndSetIfChanged(ref _trailingStop, value); this.RaisePropertyChanged(nameof(TpSlSummary)); }
    }

    public bool PartialTp
    {
        get => _partialTp;
        set { this.RaiseAndSetIfChanged(ref _partialTp, value); this.RaisePropertyChanged(nameof(TpSlSummary)); }
    }

    public decimal PartialTpClosePercent
    {
        get => _partialTpClosePercent;
        set => this.RaiseAndSetIfChanged(ref _partialTpClosePercent, Math.Clamp(value, 1m, 99m));
    }

    public decimal PartialTp2Percent
    {
        get => _partialTp2Percent;
        set => this.RaiseAndSetIfChanged(ref _partialTp2Percent, Math.Max(0.1m, value));
    }

    public string TpSlSummary
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (_tpEnabled) parts.Add(_partialTp ? $"TP1 +{_tpPercent}% / TP2 +{_partialTp2Percent}% ({_partialTpClosePercent}% partial)" : $"TP +{_tpPercent}%");
            if (_slEnabled) parts.Add(_trailingStop ? $"Trailing SL -{_slPercent}%" : $"SL -{_slPercent}%");
            return parts.Count > 0 ? string.Join(" · ", parts) : "TP/SL disabled";
        }
    }

    public string SelectedExchange
    {
        get => _selectedExchange;
        set => this.RaiseAndSetIfChanged(ref _selectedExchange, value);
    }

    public IReadOnlyList<string> AvailableExchanges { get; } = ["Binance", "Bybit", "OKX", "KuCoin"];
    public IReadOnlyList<string> AvailableMarketModes { get; } = ["Spot", "Futures"];
    public IReadOnlyList<string> AvailableFuturesMarginModes { get; } = ["Cross", "Isolated"];
    public IReadOnlyList<string> AvailableFuturesBiasModes { get; } = ["Long & Short", "Long Only", "Short Only"];
    public string BotModeSummary => IsFuturesMode
        ? $"USD-M futures x{FuturesLeverage} · {SelectedFuturesMarginMode} · {SelectedFuturesBias}"
        : "Spot market";

    public ReactiveCommand<Unit, Unit> StartBotCommand { get; }
    public ReactiveCommand<Unit, Unit> StopBotCommand { get; }

    public AIBotViewModel(
        BinanceGateway binanceSpot,
        BinanceFuturesGateway binanceFutures,
        IExchangeGateway? bybitSpot = null,
        IExchangeGateway? bybitFutures = null,
        IExchangeGateway? okxSpot = null,
        IExchangeGateway? okxFutures = null,
        IExchangeGateway? kucoinSpot = null,
        IExchangeGateway? kucoinFutures = null)
    {
        _binanceSpot = binanceSpot;
        _binanceFutures = binanceFutures;
        _bybitSpot = bybitSpot;
        _bybitFutures = bybitFutures;
        _okxSpot = okxSpot;
        _okxFutures = okxFutures;
        _kucoinSpot = kucoinSpot;
        _kucoinFutures = kucoinFutures;

        StartBotCommand = ReactiveCommand.CreateFromTask(StartBotAsync, outputScheduler: App.UiScheduler);
        StopBotCommand = ReactiveCommand.Create(StopBot, outputScheduler: App.UiScheduler);
    }

    private async Task StartBotAsync()
    {
        if (_bot is not null)
        {
            StopBot();
        }

        var marketType = IsFuturesMode ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot;
        bool useBybit  = string.Equals(_selectedExchange, "Bybit",  StringComparison.Ordinal)
            && (_bybitSpot is not null || _bybitFutures is not null);
        bool useOkx    = string.Equals(_selectedExchange, "OKX",    StringComparison.Ordinal)
            && (_okxSpot is not null || _okxFutures is not null);
        bool useKucoin = string.Equals(_selectedExchange, "KuCoin", StringComparison.Ordinal)
            && (_kucoinSpot is not null || _kucoinFutures is not null);
        var gateway = IsFuturesMode
            ? (useBybit  ? _bybitFutures  ?? _binanceFutures
               : useOkx  ? _okxFutures   ?? _binanceFutures
               : useKucoin ? _kucoinFutures ?? _binanceFutures
               : _binanceFutures)
            : (useBybit  ? _bybitSpot  ?? _binanceSpot
               : useOkx  ? _okxSpot   ?? _binanceSpot
               : useKucoin ? _kucoinSpot ?? _binanceSpot
               : _binanceSpot);
        var marginMode = string.Equals(SelectedFuturesMarginMode, "Isolated", StringComparison.OrdinalIgnoreCase)
            ? FuturesMarginMode.Isolated
            : FuturesMarginMode.Cross;
        var futuresBias = SelectedFuturesBias switch
        {
            "Long Only" => FuturesTradeBias.LongOnly,
            "Short Only" => FuturesTradeBias.ShortOnly,
            _ => FuturesTradeBias.LongShort
        };

        var strategy = CreateStrategy();
        var claudeStrategy = strategy as CryptoAITerminal.AIEngine.ClaudeStrategy;
        BotLog += $"\n[Strategy] {strategy.Name}";
        var tpSl = new TpSlConfig
        {
            TpEnabled = _tpEnabled,
            TpPercent = _tpPercent,
            SlEnabled = _slEnabled,
            SlPercent = _slPercent,
            TrailingStop = _trailingStop,
            PartialTp = _partialTp,
            PartialTpClosePercent = _partialTpClosePercent,
            PartialTp2Percent = _partialTp2Percent
        };
        _bot = new TradingBot(gateway, Symbol, Quantity, marketType, FuturesLeverage, MaxRiskPerTrade, marginMode, futuresBias, strategy, tpSl);
        _bot.OnError += msg =>
        {
            BotLog += $"\n[ERROR] {msg}";
            App.Tray?.ShowError("Rule Bot Error", msg.Length > 120 ? msg[..120] + "…" : msg);
        };
        _bot.OnSignal += (sig, conf, price) =>
        {
            BotLog += $"\n[{sig}] conf={conf:P0} @ {price:N4}";
            // For the AI strategy, attach the model's "why" to each trade signal.
            var reason = claudeStrategy?.LastReason;
            if (!string.IsNullOrWhiteSpace(reason))
                BotLog += $"\n   🧠 {reason}";
        };
        _bot.OnTradeClosed += (sym, dir, entry, exit, qty, pnl) =>
        {
            BotLog += $"\n[CLOSED] {sym} {dir}  entry={entry:N4}  exit={exit:N4}  P&L={pnl:+0.00;-0.00}";
            OnBotTradeClosed?.Invoke(sym, dir, entry, exit, qty, pnl);

            var sign = pnl >= 0 ? "+" : "";
            App.Tray?.ShowInfo(
                $"Rule Bot — Trade Closed: {sym}",
                $"{dir}  {sign}{pnl:N2} USDT  ({qty:N4} @ exit {exit:N4})");
        };
        await _bot.StartAsync();
        IsRunning = true;
        App.Tray?.ShowInfo("Rule Bot Started",
            $"{Symbol} · {_selectedExchange} · {(IsFuturesMode ? "Futures" : "Spot")}");
    }

    public void StopBot()
    {
        if (IsRunning)
            App.Tray?.ShowInfo("Rule Bot Stopped", $"{Symbol} · бот остановлен");

        var bot = _bot;
        _bot = null;
        IsRunning = false;

        if (bot is null) return;

        // Явный fire-and-forget: TP/SL ордера на бирже снимаются асинхронно.
        // Для гарантированного ожидания (например, при закрытии приложения) — StopBotAsync.
        _ = Task.Run(async () =>
        {
            try { await bot.StopAsync(); }
            catch (Exception ex) { BotLog += $"\n[Stop error] {ex.Message}"; }
        });
    }

    public async Task StopBotAsync()
    {
        if (IsRunning)
            App.Tray?.ShowInfo("Rule Bot Stopped", $"{Symbol} · бот остановлен");

        var bot = _bot;
        _bot = null;
        IsRunning = false;

        if (bot is null) return;

        try { await bot.StopAsync(); }
        catch (Exception ex) { BotLog += $"\n[Stop error] {ex.Message}"; }
    }
}
