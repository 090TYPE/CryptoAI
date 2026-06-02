using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.AIEngine.Agent;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// UI for the autonomous AI trader — Claude drives a tool-use loop and places its
/// own orders. Paper by default; live is opt-in and gated by hard limits in
/// <see cref="AiTraderAgentService"/>. The Claude key/model are shared from the
/// AI Bot tab via <see cref="Configure"/> (same pattern as Sniper/News).
/// </summary>
public sealed class AiTraderViewModel : ReactiveObject
{
    private readonly IReadOnlyDictionary<string, IExchangeGateway> _spotGateways;
    private readonly IReadOnlyDictionary<string, IExchangeGateway> _futuresGateways;
    private readonly Func<IDexTradeGateway?> _dexGatewayAccessor;
    private readonly Func<bool> _dexLiveAllowed;

    private AiTraderAgentService? _service;
    private CancellationTokenSource? _loopCts;

    private string _claudeApiKey = string.Empty;
    private string _claudeModel = "claude-sonnet-4-6";
    private string _selectedVenue = "CEX";
    private string _selectedExchange = "Binance";
    private string _selectedMarketMode = "Spot";
    private int _leverage = 3;
    private string _selectedMarginMode = "Cross";
    private bool _liveEnabled;

    // DEX-specific
    private decimal _dexSlippagePercent = 3m;
    private decimal _maxNativePerTrade = 0.05m;
    private decimal _virtualNativeStart = 1m;
    private bool _isRunning;
    private int _intervalSeconds = 300;

    private decimal _maxOrderUsd = 50m;
    private decimal _maxTotalExposureUsd = 250m;
    private int _maxOpenPositions = 3;
    private decimal _maxDailyLossUsd = 75m;
    private decimal _virtualStartUsd = 1000m;

    private string _agentLog = string.Empty;

    public AiTraderViewModel(
        IReadOnlyDictionary<string, IExchangeGateway> spotGateways,
        IReadOnlyDictionary<string, IExchangeGateway> futuresGateways,
        Func<IDexTradeGateway?> dexGatewayAccessor,
        Func<bool> dexLiveAllowed)
    {
        _spotGateways = spotGateways ?? throw new ArgumentNullException(nameof(spotGateways));
        _futuresGateways = futuresGateways ?? throw new ArgumentNullException(nameof(futuresGateways));
        _dexGatewayAccessor = dexGatewayAccessor ?? throw new ArgumentNullException(nameof(dexGatewayAccessor));
        _dexLiveAllowed = dexLiveAllowed ?? (() => false);

        RunOnceCommand = ReactiveCommand.CreateFromTask(RunOnceAsync, outputScheduler: App.UiScheduler);
        StartLoopCommand = ReactiveCommand.CreateFromTask(StartLoopAsync, outputScheduler: App.UiScheduler);
        StopCommand = ReactiveCommand.Create(Stop, outputScheduler: App.UiScheduler);
        KillCommand = ReactiveCommand.Create(Kill, outputScheduler: App.UiScheduler);
    }

    /// <summary>Forward the Claude key/model from the AI Bot tab (shared, like Sniper/News).</summary>
    public void Configure(string apiKey, string model)
    {
        ClaudeApiKey = apiKey ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(model)) ClaudeModel = model;
    }

    // ── Bindable state ─────────────────────────────────────────────────────────

    public IReadOnlyList<string> AvailableVenues { get; } = ["CEX", "DEX"];
    public IReadOnlyList<string> AvailableExchanges { get; } = ["Binance", "Bybit", "OKX", "KuCoin"];

    public string SelectedVenue
    {
        get => _selectedVenue;
        set
        {
            var normalized = string.Equals(value, "DEX", StringComparison.OrdinalIgnoreCase) ? "DEX" : "CEX";
            this.RaiseAndSetIfChanged(ref _selectedVenue, normalized);
            this.RaisePropertyChanged(nameof(IsDexVenue));
            this.RaisePropertyChanged(nameof(IsCexVenue));
            this.RaisePropertyChanged(nameof(MarketSummary));
        }
    }

    public bool IsDexVenue => string.Equals(_selectedVenue, "DEX", StringComparison.OrdinalIgnoreCase);
    public bool IsCexVenue => !IsDexVenue;

    public string SelectedExchange
    {
        get => _selectedExchange;
        set => this.RaiseAndSetIfChanged(ref _selectedExchange, value);
    }

    public decimal DexSlippagePercent
    {
        get => _dexSlippagePercent;
        set => this.RaiseAndSetIfChanged(ref _dexSlippagePercent, Math.Clamp(value, 0.1m, 50m));
    }

    public decimal MaxNativePerTrade
    {
        get => _maxNativePerTrade;
        set => this.RaiseAndSetIfChanged(ref _maxNativePerTrade, Math.Max(0.0000001m, value));
    }

    public decimal VirtualNativeStart
    {
        get => _virtualNativeStart;
        set => this.RaiseAndSetIfChanged(ref _virtualNativeStart, Math.Max(0.0000001m, value));
    }

    public IReadOnlyList<string> AvailableMarketModes { get; } = ["Spot", "Futures"];
    public IReadOnlyList<string> AvailableMarginModes { get; } = ["Cross", "Isolated"];

    public string SelectedMarketMode
    {
        get => _selectedMarketMode;
        set
        {
            var normalized = string.Equals(value, "Futures", StringComparison.OrdinalIgnoreCase) ? "Futures" : "Spot";
            this.RaiseAndSetIfChanged(ref _selectedMarketMode, normalized);
            this.RaisePropertyChanged(nameof(IsFuturesMode));
            this.RaisePropertyChanged(nameof(MarketSummary));
        }
    }

    public bool IsFuturesMode => string.Equals(_selectedMarketMode, "Futures", StringComparison.OrdinalIgnoreCase);

    public int Leverage
    {
        get => _leverage;
        set { this.RaiseAndSetIfChanged(ref _leverage, Math.Clamp(value, 1, 125)); this.RaisePropertyChanged(nameof(MarketSummary)); }
    }

    public string SelectedMarginMode
    {
        get => _selectedMarginMode;
        set
        {
            var normalized = string.Equals(value, "Isolated", StringComparison.OrdinalIgnoreCase) ? "Isolated" : "Cross";
            this.RaiseAndSetIfChanged(ref _selectedMarginMode, normalized);
            this.RaisePropertyChanged(nameof(MarketSummary));
        }
    }

    public string MarketSummary => IsDexVenue
        ? "On-chain DEX · token swaps · honeypot-screened"
        : IsFuturesMode
            ? $"USD-M futures · {Leverage}x · {SelectedMarginMode} · long/short"
            : "Spot · long-only";

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

    public bool LiveEnabled
    {
        get => _liveEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _liveEnabled, value);
            if (_service is not null) _service.LiveEnabled = value;
            this.RaisePropertyChanged(nameof(ModeLabel));
            this.RaisePropertyChanged(nameof(ModeBrush));
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(StatusLabel));
            this.RaisePropertyChanged(nameof(StatusBrush));
        }
    }

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => this.RaiseAndSetIfChanged(ref _intervalSeconds, Math.Clamp(value, 30, 3600));
    }

    public decimal MaxOrderUsd
    {
        get => _maxOrderUsd;
        set => this.RaiseAndSetIfChanged(ref _maxOrderUsd, Math.Max(1m, value));
    }

    public decimal MaxTotalExposureUsd
    {
        get => _maxTotalExposureUsd;
        set => this.RaiseAndSetIfChanged(ref _maxTotalExposureUsd, Math.Max(1m, value));
    }

    public int MaxOpenPositions
    {
        get => _maxOpenPositions;
        set => this.RaiseAndSetIfChanged(ref _maxOpenPositions, Math.Clamp(value, 1, 20));
    }

    public decimal MaxDailyLossUsd
    {
        get => _maxDailyLossUsd;
        set => this.RaiseAndSetIfChanged(ref _maxDailyLossUsd, Math.Max(1m, value));
    }

    public decimal VirtualStartUsd
    {
        get => _virtualStartUsd;
        set => this.RaiseAndSetIfChanged(ref _virtualStartUsd, Math.Max(10m, value));
    }

    public string AgentLog
    {
        get => _agentLog;
        private set => this.RaiseAndSetIfChanged(ref _agentLog, value);
    }

    public string ModeLabel => LiveEnabled ? "LIVE · real orders" : "PAPER · simulated";
    public string ModeBrush => LiveEnabled ? "#FF6B6B" : "#1FE6C2";
    public string StatusLabel => IsRunning ? "Agent is running" : "Agent is idle";
    public string StatusBrush => IsRunning ? "#3DDC84" : "#8FA3B8";

    public ReactiveCommand<Unit, Unit> RunOnceCommand { get; }
    public ReactiveCommand<Unit, Unit> StartLoopCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> KillCommand { get; }

    // ── Actions ────────────────────────────────────────────────────────────────

    private AiTraderAgentService BuildService()
    {
        var limits = new AiTraderAgentService.Limits(
            MaxOrderUsd: MaxOrderUsd,
            MaxTotalExposureUsd: MaxTotalExposureUsd,
            MaxOpenPositions: MaxOpenPositions,
            MaxDailyLossUsd: MaxDailyLossUsd,
            VirtualStartUsd: VirtualStartUsd);

        AiTraderAgentService svc;
        if (IsDexVenue)
        {
            var dexGateway = _dexGatewayAccessor()
                ?? throw new InvalidOperationException("No DEX wallet is configured — set one up in the DEX/Wallet section first.");

            var dexCfg = new AiTraderAgentService.DexConfig(
                Gateway: dexGateway,
                SlippagePercent: DexSlippagePercent,
                MaxNativePerOrder: MaxNativePerTrade,
                VirtualNativeStart: VirtualNativeStart);

            var dexLive = LiveEnabled && _dexLiveAllowed();
            if (LiveEnabled && !dexLive)
                Append("[guard] DEX live blocked by wallet paper-only mode — running PAPER.");

            svc = new AiTraderAgentService(
                gateway: null!, limits,
                venue: AiTraderAgentService.Venue.Dex,
                dex: dexCfg) { LiveEnabled = dexLive };
        }
        else
        {
            var pool = IsFuturesMode ? _futuresGateways : _spotGateways;
            if (!pool.TryGetValue(SelectedExchange, out var gateway))
                gateway = pool["Binance"];

            var marketType = IsFuturesMode ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot;
            var marginMode = string.Equals(SelectedMarginMode, "Isolated", StringComparison.OrdinalIgnoreCase)
                ? FuturesMarginMode.Isolated : FuturesMarginMode.Cross;

            svc = new AiTraderAgentService(
                gateway, limits,
                marketType: marketType,
                leverage: IsFuturesMode ? Leverage : 1,
                marginMode: marginMode) { LiveEnabled = LiveEnabled };
        }

        svc.OnEvent += AppendEvent;
        svc.OnFill += (sym, side, qty, price, val, mode) =>
            Append($"💰 [{mode}] {side} {qty:0.######} {Short(sym)} @ {price:0.######} (~{val:0.####})");
        return svc;
    }

    private static string Short(string s) => s.Length > 14 ? s[..6] + "…" + s[^4..] : s;

    private async Task RunOnceAsync()
    {
        if (string.IsNullOrWhiteSpace(ClaudeApiKey))
        {
            Append("[error] Claude API key is empty — set it on the AI Bot tab.");
            return;
        }
        Append($"── Run once · {ModeLabel} · {SelectedExchange} ──");
        _service = BuildService();
        try
        {
            var result = await _service.RunOnceAsync(ClaudeApiKey, ClaudeModel);
            Append($"✔ done · {result.StoppedReason} · {result.ToolCallCount} tool calls");
        }
        catch (Exception ex) { Append($"[error] {ex.Message}"); }
    }

    private async Task StartLoopAsync()
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(ClaudeApiKey))
        {
            Append("[error] Claude API key is empty — set it on the AI Bot tab.");
            return;
        }

        _service = BuildService();
        _loopCts = new CancellationTokenSource();
        IsRunning = true;
        Append($"▶ loop started · every {IntervalSeconds}s · {ModeLabel} · {SelectedExchange}");
        App.Tray?.ShowInfo("AI Trader started", $"{SelectedExchange} · {ModeLabel}");

        var svc = _service;
        var token = _loopCts.Token;
        _ = Task.Run(async () =>
        {
            try { await svc.StartLoopAsync(ClaudeApiKey, ClaudeModel, TimeSpan.FromSeconds(IntervalSeconds), token); }
            catch (Exception ex) { Append($"[loop error] {ex.Message}"); }
            finally { Dispatcher.UIThread.Post(() => IsRunning = false); }
        });
    }

    private void Stop()
    {
        _loopCts?.Cancel();
        _service?.Stop();
        IsRunning = false;
        Append("■ stopped");
    }

    /// <summary>Halt the loop synchronously during app shutdown (no UI marshalling).</summary>
    public void ShutdownStop()
    {
        try { _loopCts?.Cancel(); _service?.Kill(); } catch { /* swallow */ }
    }

    private void Kill()
    {
        _loopCts?.Cancel();
        _service?.Kill();
        IsRunning = false;
        Append("⛔ KILL-SWITCH — all trading halted");
        App.Tray?.ShowError("AI Trader", "Kill-switch activated.");
    }

    // ── Log ────────────────────────────────────────────────────────────────────

    private void AppendEvent(AgentEvent e)
    {
        var icon = e.Kind switch
        {
            AgentEventKind.Text => "🧠",
            AgentEventKind.ToolCall => "🔧",
            AgentEventKind.ToolResult => "←",
            AgentEventKind.Done => "✔",
            AgentEventKind.Error => "⚠",
            _ => "·"
        };
        Append($"{icon} {e.Title}: {e.Detail}");
    }

    private void Append(string line)
    {
        void Do()
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            AgentLog += (AgentLog.Length == 0 ? "" : "\n") + $"[{stamp}] {line}";
            // Cap log growth.
            if (AgentLog.Length > 16000) AgentLog = AgentLog[^12000..];
        }
        if (Dispatcher.UIThread.CheckAccess()) Do();
        else Dispatcher.UIThread.Post(Do);
    }
}
