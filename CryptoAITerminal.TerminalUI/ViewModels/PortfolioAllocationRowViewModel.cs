using Avalonia.Media;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Represents one row in the portfolio rebalancer table.
/// <para>
/// <see cref="TargetPct"/> is user-editable; all other properties are updated
/// via <see cref="ApplySnapshot"/> after each balance refresh.
/// </para>
/// </summary>
public class PortfolioAllocationRowViewModel : ReactiveObject
{
    // ── Editable fields ───────────────────────────────────────────────────────

    private string  _symbol            = "";
    private double  _targetPct         = 0.0;
    private bool    _includeCex        = true;
    private bool    _includeDex        = true;
    private double  _alertThresholdPct = 5.0;

    // ── Snapshot fields (set by ApplySnapshot) ────────────────────────────────

    private decimal _cexBalance         = 0m;
    private decimal _dexBalance         = 0m;
    private decimal _priceUsd           = 0m;
    private decimal _valueUsd           = 0m;
    private double  _actualPct          = 0.0;
    private double  _deviationPct       = 0.0;
    private decimal _rebalanceDeltaUsd  = 0m;
    private decimal _rebalanceDeltaUnits = 0m;

    // ── Editable public properties ────────────────────────────────────────────

    public string Symbol
    {
        get => _symbol;
        set => this.RaiseAndSetIfChanged(ref _symbol, value?.ToUpperInvariant() ?? "");
    }

    public double TargetPct
    {
        get => _targetPct;
        set
        {
            this.RaiseAndSetIfChanged(ref _targetPct, Math.Clamp(Math.Round(value, 1), 0.0, 100.0));
            RaiseComputedLabels();
        }
    }

    public bool IncludeCex
    {
        get => _includeCex;
        set => this.RaiseAndSetIfChanged(ref _includeCex, value);
    }

    public bool IncludeDex
    {
        get => _includeDex;
        set => this.RaiseAndSetIfChanged(ref _includeDex, value);
    }

    public double AlertThresholdPct
    {
        get => _alertThresholdPct;
        set { this.RaiseAndSetIfChanged(ref _alertThresholdPct, Math.Max(0.1, value)); RaiseAlertProps(); }
    }

    // ── Snapshot read-only properties ─────────────────────────────────────────

    public decimal CexBalance
    {
        get => _cexBalance;
        private set => this.RaiseAndSetIfChanged(ref _cexBalance, value);
    }

    public decimal DexBalance
    {
        get => _dexBalance;
        private set => this.RaiseAndSetIfChanged(ref _dexBalance, value);
    }

    public decimal TotalBalance => _cexBalance + _dexBalance;

    public decimal PriceUsd
    {
        get => _priceUsd;
        private set => this.RaiseAndSetIfChanged(ref _priceUsd, value);
    }

    public decimal ValueUsd
    {
        get => _valueUsd;
        private set => this.RaiseAndSetIfChanged(ref _valueUsd, value);
    }

    public double ActualPct
    {
        get => _actualPct;
        private set { this.RaiseAndSetIfChanged(ref _actualPct, value); RaiseAlertProps(); }
    }

    public double DeviationPct
    {
        get => _deviationPct;
        private set { this.RaiseAndSetIfChanged(ref _deviationPct, value); RaiseAlertProps(); }
    }

    public decimal RebalanceDeltaUsd
    {
        get => _rebalanceDeltaUsd;
        private set => this.RaiseAndSetIfChanged(ref _rebalanceDeltaUsd, value);
    }

    public decimal RebalanceDeltaUnits
    {
        get => _rebalanceDeltaUnits;
        private set => this.RaiseAndSetIfChanged(ref _rebalanceDeltaUnits, value);
    }

    // ── Computed display labels ───────────────────────────────────────────────

    public string TargetPctLabel => $"{_targetPct:F1}%";
    public string ActualPctLabel => $"{_actualPct:F1}%";

    public string DeviationLabel =>
        _deviationPct > 0 ? $"+{_deviationPct:F1}%" : $"{_deviationPct:F1}%";

    public string PriceLabel  => FormatPrice(_priceUsd);
    public string ValueLabel  => FormatMoney(_valueUsd);
    public string BalanceLabel => FormatBalance(TotalBalance, _symbol);

    public string SourcesLabel
    {
        get
        {
            var parts = new List<string>(2);
            if (_cexBalance > 0) parts.Add($"CEX {FormatBalance(_cexBalance, _symbol)}");
            if (_dexBalance > 0) parts.Add($"DEX {FormatBalance(_dexBalance, _symbol)}");
            return parts.Count > 0 ? string.Join(" + ", parts) : "—";
        }
    }

    // ── Alert / status indicators ─────────────────────────────────────────────

    public bool IsOverweight  => _deviationPct >  _alertThresholdPct;
    public bool IsUnderweight => _deviationPct < -_alertThresholdPct;
    public bool IsInBalance   => Math.Abs(_deviationPct) < _alertThresholdPct;
    public bool NeedsAlert    => !IsInBalance;

    public string StatusIcon =>
        IsOverweight  ? "▲" :
        IsUnderweight ? "▼" : "✓";

    public IBrush StatusBrush =>
        IsOverweight  ? new SolidColorBrush(Color.Parse("#FF5555")) :
        IsUnderweight ? new SolidColorBrush(Color.Parse("#FFAA33")) :
                        new SolidColorBrush(Color.Parse("#21E6C1"));

    public IBrush DeviationBrush =>
        IsOverweight  ? new SolidColorBrush(Color.Parse("#FF5555")) :
        IsUnderweight ? new SolidColorBrush(Color.Parse("#FFAA33")) :
                        new SolidColorBrush(Color.Parse("#21E6C1"));

    // ── Rebalancing order suggestion ──────────────────────────────────────────

    /// <summary>"BUY 0.012 BTC (~$500)" or "SELL 0.30 ETH (~$600)" or "—"</summary>
    public string RebalanceOrderLabel
    {
        get
        {
            if (Math.Abs(_rebalanceDeltaUsd) < 1m) return "—";
            var verb   = _rebalanceDeltaUsd > 0 ? "BUY" : "SELL";
            var units  = Math.Abs(_rebalanceDeltaUnits);
            var usd    = Math.Abs(_rebalanceDeltaUsd);
            return $"{verb}  {FormatBalance(units, _symbol)}  (~{FormatMoney(usd)})";
        }
    }

    public IBrush RebalanceOrderBrush =>
        _rebalanceDeltaUsd > 0 ? new SolidColorBrush(Color.Parse("#21E6C1")) :
        _rebalanceDeltaUsd < 0 ? new SolidColorBrush(Color.Parse("#FF5555")) :
                                  new SolidColorBrush(Color.Parse("#5C6E82"));

    // ── Constructor ───────────────────────────────────────────────────────────

    public PortfolioAllocationRowViewModel(
        string symbol,
        double targetPct,
        bool includeCex        = true,
        bool includeDex        = true,
        double alertThresholdPct = 5.0)
    {
        _symbol             = symbol.ToUpperInvariant();
        _targetPct          = Math.Clamp(targetPct, 0.0, 100.0);
        _includeCex         = includeCex;
        _includeDex         = includeDex;
        _alertThresholdPct  = Math.Max(0.1, alertThresholdPct);
    }

    // ── Update from service snapshot ─────────────────────────────────────────

    public void ApplySnapshot(PortfolioAssetSnapshot snap)
    {
        CexBalance          = snap.CexBalance;
        DexBalance          = snap.DexBalance;
        PriceUsd            = snap.PriceUsd;
        ValueUsd            = snap.ValueUsd;
        ActualPct           = snap.ActualPct;
        DeviationPct        = snap.DeviationPct;
        RebalanceDeltaUsd   = snap.RebalanceDeltaUsd;
        RebalanceDeltaUnits = snap.RebalanceDeltaUnits;

        this.RaisePropertyChanged(nameof(TotalBalance));
        this.RaisePropertyChanged(nameof(BalanceLabel));
        this.RaisePropertyChanged(nameof(SourcesLabel));
        this.RaisePropertyChanged(nameof(RebalanceOrderLabel));
        this.RaisePropertyChanged(nameof(RebalanceOrderBrush));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void RaiseAlertProps()
    {
        this.RaisePropertyChanged(nameof(IsOverweight));
        this.RaisePropertyChanged(nameof(IsUnderweight));
        this.RaisePropertyChanged(nameof(IsInBalance));
        this.RaisePropertyChanged(nameof(NeedsAlert));
        this.RaisePropertyChanged(nameof(StatusIcon));
        this.RaisePropertyChanged(nameof(StatusBrush));
        this.RaisePropertyChanged(nameof(DeviationBrush));
        this.RaisePropertyChanged(nameof(DeviationLabel));
        this.RaisePropertyChanged(nameof(ActualPctLabel));
        this.RaisePropertyChanged(nameof(RebalanceOrderLabel));
        this.RaisePropertyChanged(nameof(RebalanceOrderBrush));
    }

    private void RaiseComputedLabels()
    {
        this.RaisePropertyChanged(nameof(TargetPctLabel));
    }

    // ── Formatters ────────────────────────────────────────────────────────────

    internal static string FormatPrice(decimal p) =>
        p == 0m    ? "$?"    :
        p >= 10_000m ? $"${p:N0}" :
        p >= 1m      ? $"${p:N2}" :
        p > 0m       ? $"${p:G4}" : "$0";

    internal static string FormatMoney(decimal v) =>
        v >= 1_000_000m ? $"${v / 1_000_000m:N2}M" :
        v >= 1_000m     ? $"${v / 1_000m:N1}K"     :
        v > 0m          ? $"${v:N0}"                : "$0";

    internal static string FormatBalance(decimal b, string sym)
    {
        if (b <= 0m) return "—";
        var formatted = b >= 1_000m ? $"{b:N0}"
                      : b >= 1m     ? $"{b:N4}"
                      : $"{b:G4}";
        return $"{formatted} {sym}";
    }
}
