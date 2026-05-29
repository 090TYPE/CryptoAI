using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class PnlDashboardViewModel : ReactiveObject
{
    private readonly PnlDashboardService _service;

    private string _selectedPeriod = "All";
    private string _selectedSource = "All";
    private string _statusMessage  = string.Empty;
    private bool   _hasData;

    // ── Filters ───────────────────────────────────────────────────────────────

    public IReadOnlyList<string> AvailablePeriods { get; } = ["All", "Today", "Week", "Month", "Year"];
    public IReadOnlyList<string> AvailableSources { get; } = ["All", "Bot", "Sniper", "Manual", "DEX"];

    public string SelectedPeriod
    {
        get => _selectedPeriod;
        set { this.RaiseAndSetIfChanged(ref _selectedPeriod, value); Refresh(); }
    }

    public string SelectedSource
    {
        get => _selectedSource;
        set { this.RaiseAndSetIfChanged(ref _selectedSource, value); Refresh(); }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public bool HasData
    {
        get => _hasData;
        private set => this.RaiseAndSetIfChanged(ref _hasData, value);
    }

    // ── Top KPI cards ─────────────────────────────────────────────────────────

    private decimal _totalPnlUsd;
    private decimal _winRate;
    private decimal _avgWinUsd;
    private decimal _avgLossUsd;
    private decimal _maxDrawdownPct;
    private decimal _profitFactor;
    private int     _totalTrades;
    private int     _winCount;
    private int     _lossCount;

    public string TotalPnlLabel    => $"{_totalPnlUsd:+0.00;-0.00;0.00} USDT";
    public string TotalPnlBrush    => _totalPnlUsd >= 0 ? "#3DDC84" : "#FF5D73";
    public string WinRateLabel     => _totalTrades == 0 ? "--" : $"{_winRate:N1}%";
    public string AvgWinLabel      => _winCount  == 0 ? "--" : $"+{_avgWinUsd:N2}";
    public string AvgLossLabel     => _lossCount == 0 ? "--" : $"-{_avgLossUsd:N2}";
    public string MaxDrawdownLabel => _maxDrawdownPct == 0 ? "--" : $"{_maxDrawdownPct:N1}%";
    public string ProfitFactorLabel => _totalTrades == 0 ? "--" : $"{_profitFactor:N2}";
    public string TradeCountLabel  => $"{_winCount}W / {_lossCount}L / {_totalTrades}T";

    // ── Equity curve ──────────────────────────────────────────────────────────

    private string _equityCurvePathData = string.Empty;
    private string _drawdownPathData    = string.Empty;
    private string _holdPathData        = string.Empty;
    private double _zeroLineY           = 65;
    private string _equityCurveMinLabel = string.Empty;
    private string _equityCurveMaxLabel = string.Empty;
    private string _holdDeltaLabel      = string.Empty;

    public string EquityCurvePathData  => _equityCurvePathData;
    public string DrawdownPathData     => _drawdownPathData;
    public string HoldPathData         => _holdPathData;
    public double ZeroLineY            => _zeroLineY;
    public string EquityCurveMinLabel  => _equityCurveMinLabel;
    public string EquityCurveMaxLabel  => _equityCurveMaxLabel;
    public string HoldDeltaLabel       => _holdDeltaLabel;
    public bool   EquityCurveIsEmpty   => string.IsNullOrEmpty(_equityCurvePathData);
    public bool   EquityCurveHasData   => !EquityCurveIsEmpty;
    public bool   HasHoldLine          => !string.IsNullOrEmpty(_holdPathData);

    // ── Tables ────────────────────────────────────────────────────────────────

    public ObservableCollection<PeriodRowViewModel>   ByDayRows      { get; } = new();
    public ObservableCollection<SourceRowViewModel>   BySourceRows   { get; } = new();
    public ObservableCollection<SourceRowViewModel>   ByBotRows      { get; } = new();
    public ObservableCollection<SourceRowViewModel>   ByExchangeRows { get; } = new();
    public ObservableCollection<SourceRowViewModel>   ByAssetRows    { get; } = new();
    public ObservableCollection<TradeRowViewModel>    TradeRows      { get; } = new();

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RefreshCommand   { get; }
    public ReactiveCommand<Unit, Unit> ExportCsvCommand { get; }

    // ── Ctor ──────────────────────────────────────────────────────────────────

    public PnlDashboardViewModel(PnlDashboardService service)
    {
        _service = service;
        RefreshCommand   = ReactiveCommand.Create(Refresh);
        ExportCsvCommand = ReactiveCommand.Create(ExportCsv);
    }

    // ── Core refresh ──────────────────────────────────────────────────────────

    public void Refresh()
    {
        var filtered = _service.Filter(_selectedPeriod, _selectedSource);
        var metrics  = _service.ComputeMetrics(filtered);

        _totalPnlUsd    = metrics.TotalPnlUsd;
        _winRate        = metrics.WinRate;
        _avgWinUsd      = metrics.AvgWinUsd;
        _avgLossUsd     = metrics.AvgLossUsd;
        _maxDrawdownPct = metrics.MaxDrawdownPct;
        _profitFactor   = metrics.ProfitFactor;
        _totalTrades    = metrics.TotalTrades;
        _winCount       = metrics.WinCount;
        _lossCount      = metrics.LossCount;
        HasData = _totalTrades > 0;

        RaiseMetricProperties();

        // Equity curve
        var curvePoints = _service.ComputeEquityCurve(filtered);
        UpdateEquityCurve(curvePoints, filtered);

        // Period breakdown (by day)
        ByDayRows.Clear();
        foreach (var row in _service.ComputeByDay(filtered))
            ByDayRows.Add(new PeriodRowViewModel(row));

        // Source breakdown
        BySourceRows.Clear();
        foreach (var row in _service.ComputeBySource(filtered))
            BySourceRows.Add(new SourceRowViewModel(row));

        // Bot breakdown
        ByBotRows.Clear();
        foreach (var row in _service.ComputeByBot(filtered))
            ByBotRows.Add(new SourceRowViewModel(row));

        // Exchange breakdown
        ByExchangeRows.Clear();
        foreach (var row in _service.ComputeByExchange(filtered))
            ByExchangeRows.Add(new SourceRowViewModel(row));

        // Asset breakdown
        ByAssetRows.Clear();
        foreach (var row in _service.ComputeByAsset(filtered))
            ByAssetRows.Add(new SourceRowViewModel(row));

        // Trade rows (most recent first, cap to 200 for UI performance)
        TradeRows.Clear();
        foreach (var t in filtered.Take(200))
            TradeRows.Add(new TradeRowViewModel(t));

        StatusMessage = _totalTrades == 0
            ? "No trades in selected period. Start the bot or place sniper trades to see history."
            : $"{_totalTrades} trades · updated {DateTime.Now:HH:mm:ss}";
    }

    private void RaiseMetricProperties()
    {
        this.RaisePropertyChanged(nameof(TotalPnlLabel));
        this.RaisePropertyChanged(nameof(TotalPnlBrush));
        this.RaisePropertyChanged(nameof(WinRateLabel));
        this.RaisePropertyChanged(nameof(AvgWinLabel));
        this.RaisePropertyChanged(nameof(AvgLossLabel));
        this.RaisePropertyChanged(nameof(MaxDrawdownLabel));
        this.RaisePropertyChanged(nameof(ProfitFactorLabel));
        this.RaisePropertyChanged(nameof(TradeCountLabel));
        this.RaisePropertyChanged(nameof(EquityCurvePathData));
        this.RaisePropertyChanged(nameof(DrawdownPathData));
        this.RaisePropertyChanged(nameof(HoldPathData));
        this.RaisePropertyChanged(nameof(ZeroLineY));
        this.RaisePropertyChanged(nameof(EquityCurveMinLabel));
        this.RaisePropertyChanged(nameof(EquityCurveMaxLabel));
        this.RaisePropertyChanged(nameof(HoldDeltaLabel));
        this.RaisePropertyChanged(nameof(EquityCurveIsEmpty));
        this.RaisePropertyChanged(nameof(EquityCurveHasData));
        this.RaisePropertyChanged(nameof(HasHoldLine));
        this.RaisePropertyChanged(nameof(HasData));
    }

    // ── Equity curve path ─────────────────────────────────────────────────────

    private void UpdateEquityCurve(IReadOnlyList<PnlEquityPoint> points,
                                   IReadOnlyList<TradeRecord> trades)
    {
        if (points.Count < 2)
        {
            _equityCurvePathData = string.Empty;
            _drawdownPathData    = string.Empty;
            _holdPathData        = string.Empty;
            _zeroLineY           = 65;
            _equityCurveMinLabel = string.Empty;
            _equityCurveMaxLabel = string.Empty;
            _holdDeltaLabel      = string.Empty;
            return;
        }

        decimal holdBenchmark = ComputeHoldBenchmark(trades);
        var (main, ddFill, zeroY, holdPath) =
            _service.ComputeEquityCurvePaths(points, holdBenchmark);

        _equityCurvePathData = main;
        _drawdownPathData    = ddFill;
        _holdPathData        = holdPath;
        _zeroLineY           = zeroY;
        _equityCurveMinLabel = FormatPnl(points.Min(p => p.Equity));
        _equityCurveMaxLabel = FormatPnl(points.Max(p => p.Equity));

        // "Active vs Hold" badge text
        if (holdBenchmark != 0m)
        {
            decimal activeFinal = points[^1].Equity;
            decimal diff        = activeFinal - holdBenchmark;
            _holdDeltaLabel     = diff >= 0
                ? $"Active outperforms Hold by {FormatPnl(diff)} USDT"
                : $"Hold outperforms Active by {FormatPnl(-diff)} USDT";
        }
        else
        {
            _holdDeltaLabel = string.Empty;
        }
    }

    // ── Hold benchmark ────────────────────────────────────────────────────────

    /// <summary>
    /// Estimates "hold" P&L: if the user had put all deployed notional into the
    /// most-traded symbol at the first trade's entry price and exited at the last
    /// trade's exit price for that symbol.
    /// Returns 0 if insufficient data.
    /// </summary>
    private static decimal ComputeHoldBenchmark(IReadOnlyList<TradeRecord> trades)
    {
        if (trades.Count == 0) return 0m;

        // Find the symbol with most trades
        var topGroup = trades
            .Where(t => t.EntryPrice > 0 && t.ExitPrice > 0 && t.Quantity > 0)
            .GroupBy(t => t.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (topGroup is null) return 0m;

        var sorted     = topGroup.OrderBy(t => t.OpenedAtUtc).ToList();
        decimal firstEntry = sorted[0].EntryPrice;
        decimal lastExit   = sorted[^1].ExitPrice;
        if (firstEntry <= 0) return 0m;

        // Total notional deployed in this symbol
        decimal totalNotional = sorted.Sum(t => t.EntryPrice * t.Quantity);
        decimal holdQty       = totalNotional / firstEntry;

        return holdQty * (lastExit - firstEntry);
    }

    private static string FormatPnl(decimal v) => $"{v:+0.00;-0.00;0.00}";

    // ── CSV export ────────────────────────────────────────────────────────────

    private void ExportCsv()
    {
        try
        {
            var filtered  = _service.Filter(_selectedPeriod, _selectedSource);
            var csv       = _service.BuildCsv(filtered);
            var dir       = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CryptoAITerminal");
            Directory.CreateDirectory(dir);
            var filename  = $"pnl-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            var fullPath  = Path.Combine(dir, filename);
            File.WriteAllText(fullPath, csv, Encoding.UTF8);
            StatusMessage = $"CSV exported → {fullPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }
}

// ── Sub-view models ───────────────────────────────────────────────────────────

public sealed class PeriodRowViewModel
{
    private readonly PeriodRow _row;
    public string Label    => _row.Label;
    public string Trades   => _row.Trades.ToString();
    public string PnlLabel => $"{_row.PnlUsd:+0.00;-0.00;0.00}";
    public string PnlBrush => _row.PnlUsd >= 0 ? "#3DDC84" : "#FF5D73";
    public string WinRate  => $"{_row.WinRate:N0}%";
    public PeriodRowViewModel(PeriodRow row) => _row = row;
}

public sealed class SourceRowViewModel
{
    private readonly SourceRow _row;
    public string Label    => _row.Label;
    public string Trades   => $"{_row.Wins}W / {_row.Trades}T";
    public string PnlLabel => $"{_row.PnlUsd:+0.00;-0.00;0.00}";
    public string PnlBrush => _row.PnlUsd >= 0 ? "#3DDC84" : "#FF5D73";
    public string WinRate  => _row.Trades == 0 ? "--" : $"{_row.WinRate:N0}%";
    public SourceRowViewModel(SourceRow row) => _row = row;
}

public sealed class TradeRowViewModel
{
    private readonly TradeRecord _t;
    public string DateLabel   => _t.ClosedAtUtc.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
    public string Symbol      => _t.Symbol;
    public string Source      => _t.BotName ?? _t.SourceLabel;
    public string Direction   => _t.Direction.ToString();
    public string PnlLabel    => $"{_t.PnlUsd:+0.00;-0.00;0.00}";
    public string PnlPctLabel => $"{_t.PnlPercent:+0.##;-0.##;0}%";
    public string PnlBrush    => _t.PnlUsd >= 0 ? "#3DDC84" : "#FF5D73";
    public string Duration    => _t.DurationLabel;
    public string ExitReason  => _t.ExitReason;
    public TradeRowViewModel(TradeRecord t) => _t = t;
}
