using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Core.Trading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Reusable "AI Trade Assistant" that reads the live chart, asks the user for their
/// preference (risk / horizon / bias, optional per-trade risk %, and a free-text
/// intent), builds a concrete setup via <see cref="AiTradeSetupPlanner"/>, runs it
/// through the deterministic <see cref="PreTradeRiskService"/> gate, optionally enriches
/// the rationale with a live model, and applies the numbers straight into the host
/// ticket. Hosted by both the CEX and DEX desks through delegates. Works fully offline.
/// </summary>
public sealed class AiTradeAssistantViewModel : ReactiveObject
{
    private readonly Func<IReadOnlyList<DexOhlcvPoint>> _candles;
    private readonly Func<decimal> _price;
    private readonly Func<decimal> _equity;
    private readonly Action<TradeSetup> _apply;
    private readonly Action<TradeSetup>? _armAlerts;
    private readonly string _venueLabel;
    private readonly MarketInsightAiService _ai = new();
    private readonly DispatcherTimer _watchTimer;

    /// <summary>
    /// Как часто режим наблюдения пересчитывает разбор.
    ///
    /// Публично, чтобы стоимость этого цикла проверялась тестом: шесть секунд означали 600
    /// обращений к модели в час и съедали суточную квоту тарифа за минуты.
    /// </summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromMinutes(5);

    private string _riskProfile = "Balanced";
    private string _horizon = "Swing";
    private string _biasMode = "Auto";
    private string _intent = string.Empty;
    private decimal _riskPercentPerTrade;
    private bool _watchMode;
    private bool _isAnalyzing;
    private string? _aiError;
    private bool _riskOverridePrimed;
    private TradeSetup? _setup;
    private PreTradeRiskResult? _risk;
    private TradeBacktestResult? _backtest;
    private string _statusMessage;

    public AiTradeAssistantViewModel(
        string venueLabel,
        Func<IReadOnlyList<DexOhlcvPoint>> candles,
        Func<decimal> price,
        Action<TradeSetup> apply,
        Func<decimal>? equity = null,
        Action<TradeSetup>? armAlerts = null)
    {
        _venueLabel = venueLabel;
        _candles = candles;
        _price = price;
        _equity = equity ?? (() => 0m);
        _apply = apply;
        _armAlerts = armAlerts;
        _statusMessage = $"Tell me what you want, then Analyze the {venueLabel} chart.";

        SelectRiskCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) RiskProfile = v; }, outputScheduler: App.UiScheduler);
        SelectHorizonCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) Horizon = v; }, outputScheduler: App.UiScheduler);
        SelectBiasCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) BiasMode = v; }, outputScheduler: App.UiScheduler);
        AnalyzeCommand = ReactiveCommand.CreateFromTask(AnalyzeAsync, outputScheduler: App.UiScheduler);
        ApplyCommand = ReactiveCommand.Create(ApplySetup, outputScheduler: App.UiScheduler);
        ToggleWatchCommand = ReactiveCommand.Create(ToggleWatch, outputScheduler: App.UiScheduler);
        BacktestCommand = ReactiveCommand.Create(RunBacktest, outputScheduler: App.UiScheduler);
        ArmAlertsCommand = ReactiveCommand.Create(ArmAlerts, outputScheduler: App.UiScheduler);

        // Шесть секунд означали 600 обращений к модели в час — включённый WATCH съедал суточную
        // квоту тарифа за считанные минуты. Пять минут дают 12 в час: наблюдение остаётся
        // наблюдением, но перестаёт быть самым дорогим действием в приложении. График за шесть
        // секунд всё равно не меняется настолько, чтобы это стоило пересчёта.
        _watchTimer = new DispatcherTimer { Interval = WatchInterval };
        _watchTimer.Tick += async (_, _) =>
        {
            // Квота исчерпана — наблюдение молча ждёт полуночи вместо отказа раз в минуту.
            if (!ChatClient.AutomaticCallsAllowed) return;
            await AnalyzeAsync();
        };
    }

    // ── Preferences (the "what do you want" the assistant asks) ───────────────
    public string RiskProfile { get => _riskProfile; set { this.RaiseAndSetIfChanged(ref _riskProfile, value); RaiseProfileState(); } }
    public string Horizon { get => _horizon; set { this.RaiseAndSetIfChanged(ref _horizon, value); RaiseProfileState(); } }
    public string BiasMode { get => _biasMode; set { this.RaiseAndSetIfChanged(ref _biasMode, value); RaiseProfileState(); } }
    public string Intent { get => _intent; set => this.RaiseAndSetIfChanged(ref _intent, value); }

    /// <summary>Optional per-trade risk budget (% of account). 0 = size by profile.</summary>
    public decimal RiskPercentPerTrade { get => _riskPercentPerTrade; set => this.RaiseAndSetIfChanged(ref _riskPercentPerTrade, Math.Max(0m, value)); }

    public bool WatchMode { get => _watchMode; private set { this.RaiseAndSetIfChanged(ref _watchMode, value); this.RaisePropertyChanged(nameof(WatchLabel)); } }
    public string WatchLabel => _watchMode ? "WATCHING" : "WATCH";

    /// <summary>True while a rationale call is in flight, so the view can disable Analyze.</summary>
    public bool IsAnalyzing { get => _isAnalyzing; private set => this.RaiseAndSetIfChanged(ref _isAnalyzing, value); }

    public bool IsRiskConservative => Is(_riskProfile, "Conservative");
    public bool IsRiskBalanced => Is(_riskProfile, "Balanced");
    public bool IsRiskAggressive => Is(_riskProfile, "Aggressive");
    public bool IsHorizonScalp => Is(_horizon, "Scalp");
    public bool IsHorizonSwing => Is(_horizon, "Swing");
    public bool IsHorizonPosition => Is(_horizon, "Position");
    public bool IsBiasAuto => Is(_biasMode, "Auto");
    public bool IsBiasLong => Is(_biasMode, "Long");
    public bool IsBiasShort => Is(_biasMode, "Short");

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── Commands ──────────────────────────────────────────────────────────────
    public ReactiveCommand<string, Unit> SelectRiskCommand { get; }
    public ReactiveCommand<string, Unit> SelectHorizonCommand { get; }
    public ReactiveCommand<string, Unit> SelectBiasCommand { get; }
    public ReactiveCommand<Unit, Unit> AnalyzeCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleWatchCommand { get; }
    public ReactiveCommand<Unit, Unit> BacktestCommand { get; }
    public ReactiveCommand<Unit, Unit> ArmAlertsCommand { get; }

    public bool CanArmAlerts => _armAlerts is not null;

    private void RunBacktest()
    {
        var candles = _candles() ?? Array.Empty<DexOhlcvPoint>();
        var result = AiTradeSetupBacktester.Run(candles, ParseRisk(_riskProfile), ParseHorizon(_horizon), ParseBias(_biasMode));
        Backtest = result;
        StatusMessage = result.Summary;
    }

    private void ArmAlerts()
    {
        if (_setup is null)
        {
            StatusMessage = "Analyze a setup first, then arm alerts.";
            return;
        }
        if (_armAlerts is null)
        {
            return;
        }

        _armAlerts(_setup);
        StatusMessage = "Armed price alerts at entry / target / stop.";
    }

    public TradeBacktestResult? Backtest
    {
        get => _backtest;
        private set
        {
            this.RaiseAndSetIfChanged(ref _backtest, value);
            this.RaisePropertyChanged(nameof(HasBacktest));
            this.RaisePropertyChanged(nameof(BacktestSummary));
            this.RaisePropertyChanged(nameof(BacktestWinRateLabel));
            this.RaisePropertyChanged(nameof(BacktestExpectancyLabel));
        }
    }

    public bool HasBacktest => _backtest is { Trades: > 0 };
    public string BacktestSummary => _backtest?.Summary ?? string.Empty;
    public string BacktestWinRateLabel => _backtest is null ? "--" : $"{_backtest.WinRate:P0}";
    public string BacktestExpectancyLabel => _backtest is null ? "--" : $"{_backtest.Expectancy:+0.00;-0.00;0.00}R";

    private void ToggleWatch()
    {
        WatchMode = !_watchMode;
        if (_watchMode)
        {
            _watchTimer.Start();
            _ = AnalyzeAsync();
        }
        else
        {
            _watchTimer.Stop();
        }
    }

    private async Task AnalyzeAsync()
    {
        // Watch mode ticks every 6 s while a rationale call can take 30 s — never stack two.
        if (_isAnalyzing) return;

        var candles = _candles() ?? Array.Empty<DexOhlcvPoint>();
        var price = _price();

        var setup = AiTradeSetupPlanner.Build(
            candles, price,
            ParseBias(_biasMode), ParseRisk(_riskProfile), ParseHorizon(_horizon),
            _intent, _riskPercentPerTrade);

        if (setup is null)
        {
            Setup = null;
            Risk = null;
            StatusMessage = candles.Count < 3
                ? "Not enough chart history yet — let the chart load, then Analyze."
                : "Could not read a valid price. Select a symbol and try again.";
            return;
        }

        // Deterministic pre-trade risk gate.
        var equity = _equity();
        var orderUsd = equity > 0m ? equity * (setup.SizePercent / 100m) * Math.Max(1, setup.Leverage) : 0m;
        Risk = PreTradeRiskService.Evaluate(new PreTradeRiskInput(
            Symbol: _venueLabel,
            Side: setup.Bias == "LONG" ? "buy" : "sell",
            OrderUsd: orderUsd,
            EquityUsd: equity,
            ExistingExposureUsd: 0m,
            SameSymbolExposureUsd: 0m,
            MaxExposureUsd: 0m,
            Leverage: setup.Leverage,
            DailyPnlUsd: 0m,
            MaxDailyLossUsd: 0m));
        _riskOverridePrimed = false;

        Setup = setup;

        // Optional live-model narrative (degrades to the deterministic one). "Ready" waits for it:
        // announcing it before a call that can take 30 s made the panel read finished mid-flight.
        if (_ai.UsesLiveModel)
        {
            IsAnalyzing = true;
            StatusMessage = $"{_venueLabel} setup drafted · asking the model for the rationale…";
            try
            {
                await EnrichRationaleAsync(setup, Risk);
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        StatusMessage = $"{_venueLabel} setup ready · risk {Risk!.Verdict} — review and Apply.";
    }

    private async Task EnrichRationaleAsync(TradeSetup setup, PreTradeRiskResult risk)
    {
        const string role =
            "You are a trading assistant. A deterministic engine already produced the setup numbers; "
            + "explain the idea and the risk in one or two crisp sentences. Do not change the numbers. "
            + "This is not financial advice.";

        var lines = new List<string>
        {
            $"Venue: {_venueLabel}",
            $"Bias: {setup.Bias} · entry {setup.Entry} · target {setup.TakeProfit} · stop {setup.StopLoss} · {setup.RiskReward:0.#}R",
            $"Leverage {setup.Leverage}x · size {setup.SizePercent:0}% · confidence {setup.Confidence}% · confluence {setup.Confluence}%",
            $"Risk gate: {risk.Verdict} ({risk.Score}/100)",
            $"User intent: {(string.IsNullOrWhiteSpace(_intent) ? "(none)" : _intent)}",
        };
        lines.AddRange(risk.Reasons.Select(r => "Flag: " + r));

        try
        {
            var insight = await _ai.InterpretAsync(role, lines, new[] { "LONG", "SHORT", "WAIT" },
                () => new InsightResult(setup.Rationale, setup.Bias, risk.Reasons.ToArray(), "offline", true));

            if (!ReferenceEquals(_setup, setup)) return;   // a newer analyze already replaced it

            if (insight is { IsFallback: false } && !string.IsNullOrWhiteSpace(insight.Summary))
            {
                LiveRationale = insight.Summary;
                RationaleSource = insight.Source;
                _aiError = null;
            }
            else
            {
                // The planner's own rationale stays on screen; without this the source slot went
                // blank and a deterministic read looked like the model's.
                _aiError = _ai.LastError;
            }

            this.RaisePropertyChanged(nameof(RationaleText));
            this.RaisePropertyChanged(nameof(RationaleSourceLabel));
        }
        catch (Exception ex)
        {
            // Keep the deterministic rationale, but label it and say why the model is missing.
            if (!ReferenceEquals(_setup, setup)) return;
            _aiError = AiFailure.Describe(ex);
            this.RaisePropertyChanged(nameof(RationaleSourceLabel));
        }
    }

    private void ApplySetup()
    {
        if (_setup is null)
        {
            StatusMessage = "Analyze the chart first, then Apply.";
            return;
        }

        // Advisory risk gate: a BLOCK verdict needs a second, explicit Apply.
        if (_risk?.Verdict == "BLOCK" && !_riskOverridePrimed)
        {
            _riskOverridePrimed = true;
            StatusMessage = "Risk gate says BLOCK — review the reasons, press Apply again to override.";
            return;
        }

        _apply(_setup);
        _riskOverridePrimed = false;
        StatusMessage = $"Applied {_setup.Bias} setup to the {_venueLabel} ticket.";
    }

    // ── Result (bindable) ─────────────────────────────────────────────────────
    public string? LiveRationale { get; private set; }
    public string? RationaleSource { get; private set; }

    public TradeSetup? Setup
    {
        get => _setup;
        private set
        {
            LiveRationale = null;
            RationaleSource = null;
            _aiError = null;
            this.RaiseAndSetIfChanged(ref _setup, value);
            this.RaisePropertyChanged(nameof(HasSetup));
            this.RaisePropertyChanged(nameof(BiasLabel));
            this.RaisePropertyChanged(nameof(BiasBrush));
            this.RaisePropertyChanged(nameof(EntryLabel));
            this.RaisePropertyChanged(nameof(TakeProfitLabel));
            this.RaisePropertyChanged(nameof(StopLossLabel));
            this.RaisePropertyChanged(nameof(RiskRewardLabel));
            this.RaisePropertyChanged(nameof(LeverageLabel));
            this.RaisePropertyChanged(nameof(SizeLabel));
            this.RaisePropertyChanged(nameof(ConfidenceLabel));
            this.RaisePropertyChanged(nameof(ConfidenceValue));
            this.RaisePropertyChanged(nameof(ConfluenceLabel));
            this.RaisePropertyChanged(nameof(RationaleText));
            this.RaisePropertyChanged(nameof(RationaleSourceLabel));
        }
    }

    public PreTradeRiskResult? Risk
    {
        get => _risk;
        private set
        {
            this.RaiseAndSetIfChanged(ref _risk, value);
            this.RaisePropertyChanged(nameof(HasRisk));
            this.RaisePropertyChanged(nameof(RiskVerdictLabel));
            this.RaisePropertyChanged(nameof(RiskVerdictBrush));
            this.RaisePropertyChanged(nameof(RiskReasonsText));
        }
    }

    public bool HasSetup => _setup is not null;
    public string BiasLabel => _setup?.Bias ?? "—";
    public string BiasBrush => _setup?.Bias == "LONG" ? SemanticColor.Positive : _setup?.Bias == "SHORT" ? SemanticColor.Negative : "#5a7a94";
    public string EntryLabel => _setup is null ? "--" : Num(_setup.Entry);
    public string TakeProfitLabel => _setup is null ? "--" : Num(_setup.TakeProfit);
    public string StopLossLabel => _setup is null ? "--" : Num(_setup.StopLoss);
    public string RiskRewardLabel => _setup is null ? "--" : $"{_setup.RiskReward:0.#}R";
    public string LeverageLabel => _setup is null ? "--" : $"{_setup.Leverage}×";
    public string SizeLabel => _setup is null ? "--" : $"{_setup.SizePercent:0.#}%";
    public string ConfidenceLabel => _setup is null ? "--" : $"{_setup.Confidence}%";
    public double ConfidenceValue => _setup?.Confidence ?? 0;
    public string ConfluenceLabel => _setup is null ? "--" : $"{_setup.Confluence}%";
    public string RationaleText => LiveRationale ?? _setup?.Rationale ?? "The assistant will explain its read of the chart here.";

    /// <summary>
    /// Names whose rationale is on screen. An empty slot used to be the only sign the model never
    /// answered, so a fallback is labelled and carries the reason when the service reported one.
    /// </summary>
    public string RationaleSourceLabel =>
        RationaleSource is not null ? $"— {RationaleSource}"
        : _setup is null            ? string.Empty
        : _aiError is not null      ? $"— planner rationale (AI unavailable)\n{_aiError}"
        :                             "— planner rationale";

    public bool HasRisk => _risk is not null;
    public string RiskVerdictLabel => _risk is null ? "--" : $"{_risk.Verdict} · {_risk.Score}/100";
    public string RiskVerdictBrush => _risk?.Verdict switch { "BLOCK" => SemanticColor.Negative, "CAUTION" => SemanticColor.Warning, "APPROVE" => SemanticColor.Positive, _ => "#5a7a94" };
    public string RiskReasonsText => _risk is null ? string.Empty : string.Join("  •  ", _risk.Reasons);

    public string StatusMessage { get => _statusMessage; private set => this.RaiseAndSetIfChanged(ref _statusMessage, value); }

    private static string Num(decimal v) => v switch
    {
        >= 1000m => v.ToString("N2"),
        >= 1m => v.ToString("N4"),
        >= 0.0001m => v.ToString("N6"),
        _ => v.ToString("N8"),
    };

    private static TradeRiskProfile ParseRisk(string v) => v.ToLowerInvariant() switch
    {
        "conservative" => TradeRiskProfile.Conservative,
        "aggressive" => TradeRiskProfile.Aggressive,
        _ => TradeRiskProfile.Balanced,
    };

    private static TradeHorizon ParseHorizon(string v) => v.ToLowerInvariant() switch
    {
        "scalp" => TradeHorizon.Scalp,
        "position" => TradeHorizon.Position,
        _ => TradeHorizon.Swing,
    };

    private static TradeBiasMode ParseBias(string v) => v.ToLowerInvariant() switch
    {
        "long" => TradeBiasMode.Long,
        "short" => TradeBiasMode.Short,
        _ => TradeBiasMode.Auto,
    };

    private void RaiseProfileState()
    {
        this.RaisePropertyChanged(nameof(IsRiskConservative));
        this.RaisePropertyChanged(nameof(IsRiskBalanced));
        this.RaisePropertyChanged(nameof(IsRiskAggressive));
        this.RaisePropertyChanged(nameof(IsHorizonScalp));
        this.RaisePropertyChanged(nameof(IsHorizonSwing));
        this.RaisePropertyChanged(nameof(IsHorizonPosition));
        this.RaisePropertyChanged(nameof(IsBiasAuto));
        this.RaisePropertyChanged(nameof(IsBiasLong));
        this.RaisePropertyChanged(nameof(IsBiasShort));
    }
}
