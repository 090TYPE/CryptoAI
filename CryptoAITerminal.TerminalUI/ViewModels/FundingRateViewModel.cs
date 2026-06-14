using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.TerminalUI;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ── Row & Alert sub-ViewModels ────────────────────────────────────────────────

public sealed class FundingRateRowViewModel : ReactiveObject
{
    private decimal  _rate;
    private decimal  _markPrice;
    private DateTime _nextFunding;

    public string Symbol { get; }

    public FundingRateRowViewModel(string symbol, decimal rate, decimal markPrice, DateTime nextFunding)
    {
        Symbol      = symbol;
        _rate       = rate;
        _markPrice  = markPrice;
        _nextFunding = nextFunding;
    }

    public decimal FundingRate
    {
        get => _rate;
        set
        {
            _rate = value;
            this.RaisePropertyChanged(nameof(FundingRate));
            this.RaisePropertyChanged(nameof(FundingRatePct));
            this.RaisePropertyChanged(nameof(RateColor));
            this.RaisePropertyChanged(nameof(IsExtreme));
            this.RaisePropertyChanged(nameof(AnnualizedLabel));
            this.RaisePropertyChanged(nameof(ExtremeTag));
        }
    }

    public decimal MarkPrice
    {
        get => _markPrice;
        set { _markPrice = value; this.RaisePropertyChanged(nameof(MarkPriceLabel)); }
    }

    public DateTime NextFundingTime
    {
        get => _nextFunding;
        set { _nextFunding = value; this.RaisePropertyChanged(nameof(NextFundingLabel)); }
    }

    // ── Computed display ──────────────────────────────────────────────────────

    /// e.g. "+0.0100%"  or "-0.0250%"
    public string FundingRatePct =>
        $"{_rate * 100m:+0.0000;-0.0000;0.0000}%";

    /// Color based on direction and extremity
    public string RateColor => _rate switch
    {
        > 0.001m  => "#FF5D73",   // positive extreme — longs pay → red
        > 0m      => "#C97B7B",   // positive mild
        < -0.001m => "#3DDC84",   // negative extreme — shorts pay → green (good for longs)
        < 0m      => "#7BC9A0",   // negative mild
        _         => "#8B949E"
    };

    /// True when |rate| >= 0.1 %
    public bool IsExtreme => Math.Abs(_rate) >= 0.001m;

    public string ExtremeTag => IsExtreme
        ? (_rate > 0 ? "🔴 HIGH" : "🟢 LOW")
        : string.Empty;

    /// Annualised: rate × 3 payments/day × 365 days
    public string AnnualizedLabel =>
        $"APR {_rate * 3m * 365m * 100m:+0.##;-0.##;0}%";

    public string MarkPriceLabel => _markPrice > 0m
        ? $"${_markPrice:N2}"
        : string.Empty;

    /// Countdown to next funding
    public string NextFundingLabel
    {
        get
        {
            if (_nextFunding <= DateTime.MinValue) return "--";
            var diff = _nextFunding.ToUniversalTime() - DateTime.UtcNow;
            if (diff <= TimeSpan.Zero) return "Now";
            return diff.TotalHours >= 1
                ? $"{(int)diff.TotalHours}h {diff.Minutes:D2}m"
                : $"{diff.Minutes}m {diff.Seconds:D2}s";
        }
    }

    public void Update(decimal rate, decimal markPrice, DateTime nextFunding)
    {
        FundingRate     = rate;
        MarkPrice       = markPrice;
        NextFundingTime = nextFunding;
    }
}

public sealed class FundingAlertViewModel
{
    public DateTime  AlertTime    { get; init; }
    public string    Symbol       { get; init; } = string.Empty;
    public string    RatePct      { get; init; } = string.Empty;
    public string    Direction    { get; init; } = string.Empty;  // "HIGH" | "LOW"
    public string    Color        { get; init; } = "#F4B860";
    public string    TimeLabel    => AlertTime.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string    DisplayLabel => $"{Symbol}  {RatePct}  ({Direction})";
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public class FundingRateViewModel : ReactiveObject, IDisposable
{
    private readonly BinanceFuturesGateway _gateway;
    private readonly DispatcherTimer       _pollTimer;
    private readonly DispatcherTimer       _countdownTimer;
    private CancellationTokenSource?       _cts;

    private string  _statusMessage     = "Not started — press Refresh or arm tracking.";
    private string  _selectedSymbol    = "BTCUSDT";
    private string  _filterText        = string.Empty;
    private decimal _alertThresholdPct = 0.10m;    // 0.1 %
    private bool    _showOnlyExtremes;
    private bool    _isTracking;
    private int     _extremeCount;
    private string  _avgRateLabel      = "--";
    private string  _topBullishLabel   = "--";
    private string  _topBearishLabel   = "--";

    // History chart
    private string  _historyPathData   = string.Empty;
    private string  _historyZeroLineY  = "65";
    private string  _historyMinLabel   = "--";
    private string  _historyMaxLabel   = "--";
    private string  _historySymbolLabel= string.Empty;

    // Internal map symbol → row
    private readonly Dictionary<string, FundingRateRowViewModel> _rowMap =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<FundingRateRowViewModel> Rates        { get; } = new();
    public ObservableCollection<FundingRateRowViewModel> DisplayRates { get; } = new();
    public ObservableCollection<FundingAlertViewModel>   Alerts       { get; } = new();

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>   RefreshCommand       { get; }
    public ReactiveCommand<Unit, Unit>   StartTrackingCommand { get; }
    public ReactiveCommand<Unit, Unit>   StopTrackingCommand  { get; }
    public ReactiveCommand<Unit, Unit>   ClearAlertsCommand   { get; }
    public ReactiveCommand<string, Unit> SelectSymbolCommand  { get; }

    // ── Properties ────────────────────────────────────────────────────────────

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string SelectedSymbol
    {
        get => _selectedSymbol;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _selectedSymbol, value) is { } val)
                _ = LoadHistoryAsync(val);
        }
    }

    public string FilterText
    {
        get => _filterText;
        set { this.RaiseAndSetIfChanged(ref _filterText, value); RebuildDisplayRates(); }
    }

    public decimal AlertThresholdPct
    {
        get => _alertThresholdPct;
        set => this.RaiseAndSetIfChanged(ref _alertThresholdPct, value);
    }

    public bool ShowOnlyExtremes
    {
        get => _showOnlyExtremes;
        set { this.RaiseAndSetIfChanged(ref _showOnlyExtremes, value); RebuildDisplayRates(); }
    }

    public bool IsTracking
    {
        get => _isTracking;
        private set => this.RaiseAndSetIfChanged(ref _isTracking, value);
    }

    public int ExtremeCount
    {
        get => _extremeCount;
        private set => this.RaiseAndSetIfChanged(ref _extremeCount, value);
    }

    public string AvgRateLabel
    {
        get => _avgRateLabel;
        private set => this.RaiseAndSetIfChanged(ref _avgRateLabel, value);
    }

    public string TopBullishLabel
    {
        get => _topBullishLabel;
        private set => this.RaiseAndSetIfChanged(ref _topBullishLabel, value);
    }

    public string TopBearishLabel
    {
        get => _topBearishLabel;
        private set => this.RaiseAndSetIfChanged(ref _topBearishLabel, value);
    }

    // ── History chart ─────────────────────────────────────────────────────────

    public string HistoryPathData
    {
        get => _historyPathData;
        private set => this.RaiseAndSetIfChanged(ref _historyPathData, value);
    }

    public string HistoryZeroLineY
    {
        get => _historyZeroLineY;
        private set => this.RaiseAndSetIfChanged(ref _historyZeroLineY, value);
    }

    public string HistoryMinLabel
    {
        get => _historyMinLabel;
        private set => this.RaiseAndSetIfChanged(ref _historyMinLabel, value);
    }

    public string HistoryMaxLabel
    {
        get => _historyMaxLabel;
        private set => this.RaiseAndSetIfChanged(ref _historyMaxLabel, value);
    }

    public string HistorySymbolLabel
    {
        get => _historySymbolLabel;
        private set => this.RaiseAndSetIfChanged(ref _historySymbolLabel, value);
    }

    public bool HistoryIsEmpty => string.IsNullOrEmpty(_historyPathData);
    public bool HistoryHasData => !HistoryIsEmpty;

    // ── Constructor ───────────────────────────────────────────────────────────

    public FundingRateViewModel(BinanceFuturesGateway gateway)
    {
        _gateway = gateway;

        RefreshCommand       = ReactiveCommand.CreateFromTask(RefreshOnceAsync, outputScheduler: App.UiScheduler);
        StartTrackingCommand = ReactiveCommand.Create(StartTracking,            outputScheduler: App.UiScheduler);
        StopTrackingCommand  = ReactiveCommand.Create(StopTracking,             outputScheduler: App.UiScheduler);
        ClearAlertsCommand   = ReactiveCommand.Create(() => Alerts.Clear(),     outputScheduler: App.UiScheduler);
        SelectSymbolCommand  = ReactiveCommand.CreateFromTask<string>(
            sym => { SelectedSymbol = sym; return Task.CompletedTask; },
            outputScheduler: App.UiScheduler);

        // Poll every 30 s while tracking
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _pollTimer.Tick += async (_, _) => await RefreshOnceAsync();

        // Update countdown labels every second without a full refresh
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => TickCountdowns();
        _countdownTimer.Start();
    }

    // ── Tracking control ──────────────────────────────────────────────────────

    public void StartTracking()
    {
        if (IsTracking) return;
        IsTracking = true;
        _pollTimer.Start();
        _ = RefreshOnceAsync();
        StatusMessage = "Tracking active — polling every 30 s";
    }

    public void StopTracking()
    {
        _pollTimer.Stop();
        IsTracking = false;
        StatusMessage = "Tracking stopped";
    }

    // ── Data refresh ──────────────────────────────────────────────────────────

    private async Task RefreshOnceAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            StatusMessage = "Refreshing…";
            var snapshots = await _gateway.GetCurrentFundingRatesAsync(_cts.Token);

            if (snapshots.Count == 0)
            {
                StatusMessage = "No data returned (Binance Futures API may be unavailable)";
                return;
            }

            MergeSnapshots(snapshots);
            RebuildDisplayRates();
            UpdateSummaryStats();
            FireAlerts(snapshots);

            StatusMessage = $"{Rates.Count} instruments · {ExtremeCount} extreme · updated {DateTime.Now:HH:mm:ss}";

            // Refresh history chart for currently selected symbol
            await LoadHistoryAsync(_selectedSymbol, _cts.Token);
        }
        catch (OperationCanceledException) { /* silently ignore */ }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private void MergeSnapshots(IReadOnlyList<FundingRateSnapshot> snapshots)
    {
        foreach (var s in snapshots)
        {
            if (_rowMap.TryGetValue(s.Symbol, out var row))
            {
                row.Update(s.FundingRate, s.MarkPrice, s.NextFundingTime);
            }
            else
            {
                var newRow = new FundingRateRowViewModel(
                    s.Symbol, s.FundingRate, s.MarkPrice, s.NextFundingTime);
                _rowMap[s.Symbol] = newRow;
                Rates.Add(newRow);
            }
        }
    }

    private void RebuildDisplayRates()
    {
        var filter    = _filterText.Trim();
        var threshold = _alertThresholdPct / 100m;

        var query = Rates.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter))
            query = query.Where(r => r.Symbol.Contains(filter, StringComparison.OrdinalIgnoreCase));

        if (_showOnlyExtremes)
            query = query.Where(r => Math.Abs(r.FundingRate) >= threshold);

        // Sort by |rate| descending
        var sorted = query.OrderByDescending(r => Math.Abs(r.FundingRate)).ToList();

        DisplayRates.Clear();
        foreach (var r in sorted)
            DisplayRates.Add(r);
    }

    private void UpdateSummaryStats()
    {
        if (Rates.Count == 0) return;

        decimal avg = Rates.Average(r => r.FundingRate);
        AvgRateLabel = $"{avg * 100m:+0.0000;-0.0000;0.0000}%";

        ExtremeCount = Rates.Count(r => Math.Abs(r.FundingRate) >= _alertThresholdPct / 100m);

        var mostPositive = Rates.OrderByDescending(r => r.FundingRate).FirstOrDefault();
        var mostNegative = Rates.OrderBy(r => r.FundingRate).FirstOrDefault();

        TopBullishLabel = mostNegative is not null
            ? $"{mostNegative.Symbol} {mostNegative.FundingRatePct}"
            : "--";

        TopBearishLabel = mostPositive is not null
            ? $"{mostPositive.Symbol} {mostPositive.FundingRatePct}"
            : "--";
    }

    private void FireAlerts(IReadOnlyList<FundingRateSnapshot> snapshots)
    {
        var threshold = _alertThresholdPct / 100m;
        foreach (var s in snapshots)
        {
            if (Math.Abs(s.FundingRate) < threshold) continue;

            // De-duplicate: skip if already alerted this exact rate recently
            var recent = Alerts.Take(5)
                .Any(a => a.Symbol == s.Symbol && a.RatePct == $"{s.FundingRate * 100m:+0.0000;-0.0000;0.0000}%");
            if (recent) continue;

            var ratePct = $"{s.FundingRate * 100m:+0.0000;-0.0000;0.0000}%";
            var alert = new FundingAlertViewModel
            {
                AlertTime = DateTime.UtcNow,
                Symbol    = s.Symbol,
                RatePct   = ratePct,
                Direction = s.FundingRate > 0 ? "HIGH" : "LOW",
                Color     = s.FundingRate > 0 ? "#FF5D73" : "#3DDC84"
            };

            Alerts.Insert(0, alert);

            // OS-уведомление об экстремальной ставке
            var dir = s.FundingRate > 0 ? "HIGH" : "LOW";
            App.Tray?.ShowWarning(
                $"Funding Rate Alert: {s.Symbol}",
                $"{dir} funding {ratePct} — check your positions!");
        }

        // Cap alert list at 100
        while (Alerts.Count > 100)
            Alerts.RemoveAt(Alerts.Count - 1);
    }

    // ── History chart ─────────────────────────────────────────────────────────

    private async Task LoadHistoryAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        try
        {
            var points = await _gateway.GetFundingHistoryAsync(symbol, 90, ct);
            RenderHistoryChart(symbol, points);
        }
        catch { /* non-fatal */ }
    }

    private void RenderHistoryChart(string symbol, IReadOnlyList<FundingHistoryPoint> points)
    {
        HistorySymbolLabel = symbol;
        this.RaisePropertyChanged(nameof(HistoryIsEmpty));
        this.RaisePropertyChanged(nameof(HistoryHasData));

        if (points.Count < 2)
        {
            HistoryPathData = string.Empty;
            HistoryMinLabel = "--";
            HistoryMaxLabel = "--";
            HistoryZeroLineY = "65";
            return;
        }

        const double W = 540, H = 130, Pad = 8;

        double minRate = (double)points.Min(p => p.Rate);
        double maxRate = (double)points.Max(p => p.Rate);

        // Expand range to include zero
        if (minRate > 0) minRate = 0;
        if (maxRate < 0) maxRate = 0;

        double range = Math.Max(maxRate - minRate, 0.000001);
        int n = points.Count;

        var sb = new StringBuilder("M");
        for (int i = 0; i < n; i++)
        {
            double x = Pad + i * (W - Pad * 2) / (n - 1);
            double y = (H - Pad) - ((double)points[i].Rate - minRate) / range * (H - Pad * 2) + Pad;
            if (i == 0) sb.Append($" {x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}");
            else        sb.Append($" L {x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        // Zero-line Y position
        double zeroY = (H - Pad) - (0.0 - minRate) / range * (H - Pad * 2) + Pad;
        HistoryZeroLineY = zeroY.ToString("F1", CultureInfo.InvariantCulture);

        HistoryPathData = sb.ToString();
        HistoryMinLabel = $"{minRate * 100:+0.0000;-0.0000;0.0000}%";
        HistoryMaxLabel = $"{maxRate * 100:+0.0000;-0.0000;0.0000}%";

        this.RaisePropertyChanged(nameof(HistoryIsEmpty));
        this.RaisePropertyChanged(nameof(HistoryHasData));
    }

    // ── Countdown tick (cheap — no API call) ─────────────────────────────────

    private void TickCountdowns()
    {
        foreach (var row in DisplayRates)
            row.RaisePropertyChanged(nameof(FundingRateRowViewModel.NextFundingLabel));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _pollTimer.Stop();
        _countdownTimer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
