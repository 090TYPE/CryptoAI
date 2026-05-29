using Avalonia.Media;
using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ════════════════════════════════════════════════════════════════════════════
//  Row VM — one detected opportunity
// ════════════════════════════════════════════════════════════════════════════

public sealed class FundingArbOpportunityRowVM
{
    public FundingArbitrageOpportunity Source { get; }

    public string Exchange    => Source.Exchange;
    public string Symbol      => Source.Symbol;

    public string RateLabel =>
        $"{Source.FundingRatePct:+0.0000;-0.0000}%";

    public string AnnualLabel =>
        $"{Source.AnnualizedPct:+0.0;-0.0}% p.a.";

    public string PriceLabel =>
        Source.MarkPrice > 0 ? $"${Source.MarkPrice:N0}" : "—";

    public string NextLabel
    {
        get
        {
            var rem = Source.NextFundingTime - DateTime.UtcNow;
            if (rem <= TimeSpan.Zero)           return "now";
            if (rem.TotalHours >= 1)            return $"{rem.TotalHours:F1}h";
            return $"{(int)rem.TotalMinutes}m";
        }
    }

    public IBrush RateBrush =>
        Source.FundingRatePct >= 0.10m ? new SolidColorBrush(Color.Parse("#21E6C1")) :
        Source.FundingRatePct >= 0.05m ? new SolidColorBrush(Color.Parse("#F4B860")) :
                                          new SolidColorBrush(Color.Parse("#8FA3B8"));

    public IBrush AnnualBrush =>
        Source.AnnualizedPct >= 50m ? new SolidColorBrush(Color.Parse("#21E6C1")) :
        Source.AnnualizedPct >= 20m ? new SolidColorBrush(Color.Parse("#F4B860")) :
                                       new SolidColorBrush(Color.Parse("#8FA3B8"));

    public string ExchangeBadgeColor => Exchange switch
    {
        "Binance" => "#F4B860",
        "Bybit"   => "#FFAA00",
        "OKX"     => "#3BBFFF",
        _         => "#5C6E82",
    };

    public FundingArbOpportunityRowVM(FundingArbitrageOpportunity src) => Source = src;
}

// ════════════════════════════════════════════════════════════════════════════
//  Row VM — one open arb position
// ════════════════════════════════════════════════════════════════════════════

public sealed class FundingArbPositionRowVM : ReactiveObject
{
    public FundingArbPosition Model { get; }

    public FundingArbPositionRowVM(FundingArbPosition pos) => Model = pos;

    // ── Labels ────────────────────────────────────────────────────────────────

    public string Exchange     => Model.Exchange;
    public string Symbol       => Model.Symbol;
    public string NotionalLabel => $"${Model.NotionalUsd:N0}";
    public string EntryRateLabel => $"{Model.EntryFundingRatePct:+0.0000;-0.0000}%";

    public string AgeLabel
    {
        get
        {
            var age = Model.Age;
            if (age.TotalDays  >= 1) return $"{age.TotalDays:F1}d";
            if (age.TotalHours >= 1) return $"{age.TotalHours:F1}h";
            return $"{(int)age.TotalMinutes}m";
        }
    }

    public decimal FundingCollectedUsd => Model.FundingCollectedUsd;
    public decimal TotalPnlUsd         => Model.TotalPnlUsd;

    public string FundingLabel  => FmtUsd(Model.FundingCollectedUsd);
    public string SpotPnlLabel  => FmtUsd(Model.SpotPnlUsd);
    public string PerpPnlLabel  => FmtUsd(Model.PerpPnlUsd);
    public string TotalPnlLabel => FmtUsd(Model.TotalPnlUsd);
    public string BasisLabel    => $"{Model.BasisDriftPct:+0.000;-0.000}%";

    public IBrush TotalPnlBrush =>
        Model.TotalPnlUsd >= 0
            ? new SolidColorBrush(Color.Parse("#21E6C1"))
            : new SolidColorBrush(Color.Parse("#FF5555"));

    public IBrush BasisBrush =>
        Math.Abs(Model.BasisDriftPct) > 0.5m
            ? new SolidColorBrush(Color.Parse("#FFAA33"))
            : new SolidColorBrush(Color.Parse("#5C6E82"));

    // ── Notify all computed props (called on every refresh tick) ─────────────

    public void Refresh() => this.RaisePropertyChanged(string.Empty);

    private static string FmtUsd(decimal v) =>
        v >= 0 ? $"+${v:N2}" : $"-${Math.Abs(v):N2}";
}

// ════════════════════════════════════════════════════════════════════════════
//  Main ViewModel
// ════════════════════════════════════════════════════════════════════════════

public sealed class FundingArbitrageViewModel : ReactiveObject, IDisposable
{
    private readonly FundingArbitrageService _svc;
    private readonly DispatcherTimer         _timer;
    private CancellationTokenSource          _cts = new();
    private bool _disposed;

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<FundingArbOpportunityRowVM> Opportunities { get; } = [];
    public ObservableCollection<FundingArbPositionRowVM>    Positions      { get; } = [];

    // ── Configuration (mirrored to service) ───────────────────────────────────

    private decimal _thresholdPct;
    private decimal _notionalUsd;
    private int     _leverage;

    public decimal ThresholdPct
    {
        get => _thresholdPct;
        set
        {
            this.RaiseAndSetIfChanged(ref _thresholdPct, Math.Max(0.01m, Math.Round(value, 2)));
            _svc.ThresholdPct = _thresholdPct;
        }
    }

    public decimal NotionalUsd
    {
        get => _notionalUsd;
        set
        {
            this.RaiseAndSetIfChanged(ref _notionalUsd, Math.Max(10m, Math.Round(value, 0)));
            _svc.NotionalUsd = _notionalUsd;
        }
    }

    public int Leverage
    {
        get => _leverage;
        set
        {
            this.RaiseAndSetIfChanged(ref _leverage, Math.Clamp(value, 1, 10));
            _svc.Leverage = _leverage;
        }
    }

    private bool    _autoReinvest;
    private decimal _reinvestThreshold = 10m;

    public bool AutoReinvest
    {
        get => _autoReinvest;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoReinvest, value);
            _svc.AutoReinvest = value;
        }
    }

    public decimal ReinvestThreshold
    {
        get => _reinvestThreshold;
        set
        {
            this.RaiseAndSetIfChanged(ref _reinvestThreshold, Math.Max(1m, value));
            _svc.ReinvestThreshold = _reinvestThreshold;
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private bool   _isLoading;
    private string _statusLabel = "Click Refresh to scan all exchanges";

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => this.RaiseAndSetIfChanged(ref _statusLabel, value);
    }

    // ── Aggregate P&L ─────────────────────────────────────────────────────────

    public string TotalFundingLabel
    {
        get
        {
            var sum = Positions.Sum(p => p.FundingCollectedUsd);
            return sum >= 0 ? $"+${sum:N2}" : $"-${Math.Abs(sum):N2}";
        }
    }

    public string TotalPnlLabel
    {
        get
        {
            var sum = Positions.Sum(p => p.TotalPnlUsd);
            return sum >= 0 ? $"+${sum:N2}" : $"-${Math.Abs(sum):N2}";
        }
    }

    public IBrush TotalPnlBrush =>
        Positions.Sum(p => p.TotalPnlUsd) >= 0
            ? new SolidColorBrush(Color.Parse("#21E6C1"))
            : new SolidColorBrush(Color.Parse("#FF5555"));

    public int  OpenPositionCount  => Positions.Count;
    public bool HasOpportunities   => Opportunities.Count > 0;

    /// <summary>Best APR among current opportunities (rate × 3 × 365).</summary>
    public string BestAprLabel
    {
        get
        {
            if (Opportunities.Count == 0) return "—";
            var best = Opportunities.Max(o => o.Source.AnnualizedPct);
            return $"{best:N1}% p.a.";
        }
    }

    /// <summary>Total reinvest count across all positions.</summary>
    public string ReinvestCountLabel =>
        _svc.AllPositions.Sum(p => p.ReinvestCount) is var n and > 0
            ? $"{n}×"
            : "—";

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>                    RefreshCommand       { get; }
    public ReactiveCommand<FundingArbOpportunityRowVM, Unit> OpenCommand       { get; }
    public ReactiveCommand<FundingArbPositionRowVM,    Unit> CloseCommand      { get; }

    // ── Toast relay ───────────────────────────────────────────────────────────

    public event Action<string>? ToastRequested;

    /// <summary>
    /// Fired after a funding arb position is successfully closed.
    /// Set by MainWindowViewModel to record into P&L dashboard.
    /// </summary>
    public Action<FundingArbPositionRowVM>? OnPositionClosed;

    // ── Constructor ───────────────────────────────────────────────────────────

    public FundingArbitrageViewModel(FundingArbitrageService svc)
    {
        _svc = svc;

        _thresholdPct = svc.ThresholdPct;
        _notionalUsd  = svc.NotionalUsd;
        _leverage     = svc.Leverage;

        RefreshCommand = ReactiveCommand.CreateFromTask(
            _ => DoRefreshAsync(), outputScheduler: App.UiScheduler);

        OpenCommand = ReactiveCommand.CreateFromTask<FundingArbOpportunityRowVM>(
            row => DoOpenAsync(row.Source), outputScheduler: App.UiScheduler);

        CloseCommand = ReactiveCommand.CreateFromTask<FundingArbPositionRowVM>(
            row => DoCloseAsync(row), outputScheduler: App.UiScheduler);

        // Auto-refresh every 30 s
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await DoRefreshAsync();
        _timer.Start();

        _ = DoRefreshAsync();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task DoRefreshAsync()
    {
        if (_isLoading) return;

        IsLoading   = true;
        StatusLabel = "Fetching funding rates from Binance · Bybit · OKX…";

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var opps = await _svc.FetchOpportunitiesAsync(ct);
            if (ct.IsCancellationRequested) return;

            _svc.UpdateFundingEstimates(opps);

            Dispatcher.UIThread.Post(() =>
            {
                // Rebuild opportunities table
                Opportunities.Clear();
                foreach (var o in opps)
                    Opportunities.Add(new FundingArbOpportunityRowVM(o));

                // Refresh position row labels
                foreach (var row in Positions)
                    row.Refresh();

                RaiseAggregates();
                this.RaisePropertyChanged(nameof(HasOpportunities));

                var aboveThreshold = opps.Count(o => o.FundingRatePct >= _thresholdPct);
                StatusLabel = $"Updated {DateTime.Now:HH:mm:ss}  ·  "
                            + $"{opps.Count} symbols  ·  "
                            + $"{aboveThreshold} above threshold";
                IsLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => IsLoading = false);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusLabel = $"Error: {ex.Message}";
                IsLoading   = false;
            });
        }
    }

    // ── Open position ─────────────────────────────────────────────────────────

    private async Task DoOpenAsync(FundingArbitrageOpportunity opp)
    {
        StatusLabel = $"Opening arb on {opp.Exchange} {opp.Symbol}…";
        var (ok, error) = await _svc.OpenPositionAsync(opp);

        Dispatcher.UIThread.Post(() =>
        {
            if (ok)
            {
                var pos = _svc.AllPositions.Last(p =>
                    p.Exchange == opp.Exchange && p.Symbol == opp.Symbol);

                Positions.Add(new FundingArbPositionRowVM(pos));
                RaiseAggregates();

                StatusLabel = $"✓ Arb opened: {opp.Exchange} {opp.Symbol} · ${_svc.NotionalUsd:N0} · {opp.FundingRatePct:F4}%/8h";
                ToastRequested?.Invoke(
                    $"⚡ Funding Arb opened\n{opp.Exchange} {opp.Symbol}\n{opp.FundingRatePct:F4}%/8h  ({opp.AnnualizedPct:F1}% p.a.)");
            }
            else
            {
                StatusLabel = $"✗ Open failed: {error}";
                ToastRequested?.Invoke($"Arb open failed: {error}");
            }
        });
    }

    // ── Close position ────────────────────────────────────────────────────────

    private async Task DoCloseAsync(FundingArbPositionRowVM row)
    {
        var pos = row.Model;
        StatusLabel = $"Closing arb on {pos.Exchange} {pos.Symbol}…";
        var (ok, error) = await _svc.ClosePositionAsync(pos);

        Dispatcher.UIThread.Post(() =>
        {
            if (ok)
            {
                Positions.Remove(row);
                RaiseAggregates();
                StatusLabel = $"✓ Arb closed: {pos.Exchange} {pos.Symbol} · PnL {row.TotalPnlLabel}";
                ToastRequested?.Invoke(
                    $"Arb closed: {pos.Exchange} {pos.Symbol}\nTotal PnL: {row.TotalPnlLabel}");
                OnPositionClosed?.Invoke(row);
            }
            else
            {
                StatusLabel = $"✗ Close error: {error}";
            }
        });
    }

    private void RaiseAggregates()
    {
        this.RaisePropertyChanged(nameof(TotalFundingLabel));
        this.RaisePropertyChanged(nameof(TotalPnlLabel));
        this.RaisePropertyChanged(nameof(TotalPnlBrush));
        this.RaisePropertyChanged(nameof(OpenPositionCount));
        this.RaisePropertyChanged(nameof(BestAprLabel));
        this.RaisePropertyChanged(nameof(ReinvestCountLabel));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _cts.Cancel();
        _cts.Dispose();
        _svc.Dispose();
    }
}
