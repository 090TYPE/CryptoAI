using System;
using System.Collections.Generic;
using System.Reactive;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Core.Trading;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Reusable "AI Trade Assistant" that reads the live chart, asks the user for their
/// preference (risk / horizon / bias + a free-text intent), builds a concrete setup
/// via <see cref="AiTradeSetupPlanner"/> and applies the numbers straight into the
/// host ticket. Hosted by both the CEX desk and the DEX desk through delegates, so the
/// same panel drives either venue.
/// </summary>
public sealed class AiTradeAssistantViewModel : ReactiveObject
{
    private readonly Func<IReadOnlyList<DexOhlcvPoint>> _candles;
    private readonly Func<decimal> _price;
    private readonly Action<TradeSetup> _apply;
    private readonly string _venueLabel;

    private string _riskProfile = "Balanced";
    private string _horizon = "Swing";
    private string _biasMode = "Auto";
    private string _intent = string.Empty;
    private TradeSetup? _setup;
    private string _statusMessage;

    public AiTradeAssistantViewModel(
        string venueLabel,
        Func<IReadOnlyList<DexOhlcvPoint>> candles,
        Func<decimal> price,
        Action<TradeSetup> apply)
    {
        _venueLabel = venueLabel;
        _candles = candles;
        _price = price;
        _apply = apply;
        _statusMessage = $"Tell me what you want, then Analyze the {venueLabel} chart.";

        SelectRiskCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) RiskProfile = v; }, outputScheduler: App.UiScheduler);
        SelectHorizonCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) Horizon = v; }, outputScheduler: App.UiScheduler);
        SelectBiasCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) BiasMode = v; }, outputScheduler: App.UiScheduler);
        AnalyzeCommand = ReactiveCommand.Create(Analyze, outputScheduler: App.UiScheduler);
        ApplyCommand = ReactiveCommand.Create(ApplySetup, outputScheduler: App.UiScheduler);
    }

    // ── Preferences (the "what do you want" the assistant asks) ───────────────
    public string RiskProfile { get => _riskProfile; set { this.RaiseAndSetIfChanged(ref _riskProfile, value); RaiseProfileState(); } }
    public string Horizon { get => _horizon; set { this.RaiseAndSetIfChanged(ref _horizon, value); RaiseProfileState(); } }
    public string BiasMode { get => _biasMode; set { this.RaiseAndSetIfChanged(ref _biasMode, value); RaiseProfileState(); } }
    public string Intent { get => _intent; set => this.RaiseAndSetIfChanged(ref _intent, value); }

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

    private void Analyze()
    {
        var candles = _candles() ?? Array.Empty<DexOhlcvPoint>();
        var price = _price();

        var setup = AiTradeSetupPlanner.Build(
            candles,
            price,
            ParseBias(_biasMode),
            ParseRisk(_riskProfile),
            ParseHorizon(_horizon),
            _intent);

        if (setup is null)
        {
            Setup = null;
            StatusMessage = candles.Count < 3
                ? "Not enough chart history yet — let the chart load, then Analyze."
                : "Could not read a valid price. Select a symbol and try again.";
            return;
        }

        Setup = setup;
        StatusMessage = $"{_venueLabel} setup ready — review and Apply to the ticket.";
    }

    private void ApplySetup()
    {
        if (_setup is null)
        {
            StatusMessage = "Analyze the chart first, then Apply.";
            return;
        }

        _apply(_setup);
        StatusMessage = $"Applied {_setup.Bias} setup to the {_venueLabel} ticket.";
    }

    // ── Result (bindable) ─────────────────────────────────────────────────────
    public TradeSetup? Setup
    {
        get => _setup;
        private set
        {
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
            this.RaisePropertyChanged(nameof(RationaleText));
        }
    }

    public bool HasSetup => _setup is not null;
    public string BiasLabel => _setup?.Bias ?? "—";
    public string BiasBrush => _setup?.Bias == "LONG" ? "#3ddc84" : _setup?.Bias == "SHORT" ? "#ff6b6b" : "#5a7a94";
    public string EntryLabel => _setup is null ? "--" : Num(_setup.Entry);
    public string TakeProfitLabel => _setup is null ? "--" : Num(_setup.TakeProfit);
    public string StopLossLabel => _setup is null ? "--" : Num(_setup.StopLoss);
    public string RiskRewardLabel => _setup is null ? "--" : $"{_setup.RiskReward:0.#}R";
    public string LeverageLabel => _setup is null ? "--" : $"{_setup.Leverage}×";
    public string SizeLabel => _setup is null ? "--" : $"{_setup.SizePercent:0}%";
    public string ConfidenceLabel => _setup is null ? "--" : $"{_setup.Confidence}%";
    public double ConfidenceValue => _setup?.Confidence ?? 0;
    public string RationaleText => _setup?.Rationale ?? "The assistant will explain its read of the chart here.";

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
