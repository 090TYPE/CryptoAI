using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class DexTrendingViewModel : ReactiveObject, IDisposable
{
    private readonly DexTrendingService _svc;
    private readonly DispatcherTimer    _refreshTimer;
    private CancellationTokenSource     _cts = new();
    private bool _disposed;

    // ── Backing fields ────────────────────────────────────────────────────────
    private decimal _minLiquidityUsd   = 10_000m;
    private decimal _maxPoolAgeHours   = 72m;
    private int     _minSecurityScore  = 0;
    private string  _selectedTimeframe = "1h";
    private string  _searchQuery       = string.Empty;

    private IReadOnlyList<DexTokenRowViewModel> _filteredTokens  = [];
    private IReadOnlyList<DexTrendingToken>     _allTokens       = [];
    private bool   _isLoading;
    private string _statusLabel = "Ready";
    private int    _totalCount;

    // ── Public properties ─────────────────────────────────────────────────────

    public decimal MinLiquidityUsd
    {
        get => _minLiquidityUsd;
        set { this.RaiseAndSetIfChanged(ref _minLiquidityUsd, value); ApplyFilters(); }
    }

    public decimal MaxPoolAgeHours
    {
        get => _maxPoolAgeHours;
        set { this.RaiseAndSetIfChanged(ref _maxPoolAgeHours, value); ApplyFilters(); }
    }

    /// <summary>Minimum heuristic security score 0-100. Tokens below this threshold are hidden.</summary>
    public int MinSecurityScore
    {
        get => _minSecurityScore;
        set { this.RaiseAndSetIfChanged(ref _minSecurityScore, Math.Clamp(value, 0, 100)); ApplyFilters(); }
    }

    public string SelectedTimeframe
    {
        get => _selectedTimeframe;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTimeframe, value ?? "1h");
            // Notify all row VMs to recompute their volume/change labels
            foreach (var row in _filteredTokens)
                row.UpdateTimeframe(_selectedTimeframe);
            // Raise active-button indicator properties
            this.RaisePropertyChanged(nameof(IsTf5m));
            this.RaisePropertyChanged(nameof(IsTf15m));
            this.RaisePropertyChanged(nameof(IsTf1h));
            this.RaisePropertyChanged(nameof(IsTf6h));
            this.RaisePropertyChanged(nameof(IsTf24h));
            ApplyFilters();
        }
    }

    // ── Timeframe active-state indicators (used by XAML to style the buttons) ─
    public bool IsTf5m  => _selectedTimeframe == "5m";
    public bool IsTf15m => _selectedTimeframe == "15m";
    public bool IsTf1h  => _selectedTimeframe == "1h";
    public bool IsTf6h  => _selectedTimeframe == "6h";
    public bool IsTf24h => _selectedTimeframe == "24h";

    public string SearchQuery
    {
        get => _searchQuery;
        set { this.RaiseAndSetIfChanged(ref _searchQuery, value ?? string.Empty); ApplyFilters(); }
    }

    public IReadOnlyList<DexTokenRowViewModel> FilteredTokens
    {
        get => _filteredTokens;
        private set => this.RaiseAndSetIfChanged(ref _filteredTokens, value);
    }

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

    public int TotalCount
    {
        get => _totalCount;
        private set => this.RaiseAndSetIfChanged(ref _totalCount, value);
    }

    public bool HasNoFilteredTokens => _filteredTokens.Count == 0;

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<string, Unit> SetTimeframeCommand { get; }
    public ReactiveCommand<DexTokenRowViewModel, Unit> OpenInSniperCommand { get; }

    /// <summary>Wired by MainWindowViewModel to handle navigation + pre-fill.</summary>
    public Action<string, string>? OnOpenInSniper { get; set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public DexTrendingViewModel(DexTrendingService svc)
    {
        _svc = svc;

        RefreshCommand = ReactiveCommand.CreateFromTask(
            async _ => await LoadAsync(),
            outputScheduler: App.UiScheduler);

        SetTimeframeCommand = ReactiveCommand.Create<string>(tf =>
        {
            SelectedTimeframe = tf ?? "1h";
        }, outputScheduler: App.UiScheduler);

        OpenInSniperCommand = ReactiveCommand.Create<DexTokenRowViewModel>(row =>
        {
            OnOpenInSniper?.Invoke(row.TokenAddress, row.Chain);
        }, outputScheduler: App.UiScheduler);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += async (_, _) => await LoadAsync();
        _refreshTimer.Start();

        _ = LoadAsync();
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (_isLoading) return;

        IsLoading   = true;
        StatusLabel = "Loading…";

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var tokens = await _svc.FetchTrendingAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() =>
            {
                _allTokens = tokens;
                TotalCount = tokens.Count;
                ApplyFilters();
                StatusLabel = $"Updated {DateTime.Now:HH:mm} · {tokens.Count} tokens";
                IsLoading   = false;
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => { IsLoading = false; });
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

    // ── Filtering ─────────────────────────────────────────────────────────────

    private void ApplyFilters()
    {
        var q          = _searchQuery.Trim();
        var tf         = _selectedTimeframe;
        var minScore   = _minSecurityScore;

        var filtered = _allTokens
            // ── Liquidity filter ─────────────────────────────────────────────
            .Where(t => t.LiquidityUsd >= _minLiquidityUsd)
            // ── Pool-age filter ──────────────────────────────────────────────
            .Where(t =>
            {
                if (t.PairCreatedAtMs <= 0) return true;
                var ageH = (DateTime.UtcNow -
                    DateTimeOffset.FromUnixTimeMilliseconds(t.PairCreatedAtMs).UtcDateTime).TotalHours;
                return (decimal)ageH <= _maxPoolAgeHours;
            })
            // ── Security score filter ────────────────────────────────────────
            .Where(t =>
            {
                if (minScore <= 0) return true;
                var ageH = t.PairCreatedAtMs <= 0
                    ? 999.0
                    : (DateTime.UtcNow -
                       DateTimeOffset.FromUnixTimeMilliseconds(t.PairCreatedAtMs).UtcDateTime).TotalHours;
                return DexTokenRowViewModel.ComputeSecurityScore(
                    t.LiquidityUsd, ageH, t.VolumeH1, t.PriceChangeH1) >= minScore;
            })
            // ── Text search ──────────────────────────────────────────────────
            .Where(t =>
            {
                if (string.IsNullOrEmpty(q)) return true;
                return t.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       t.Name.Contains(q,   StringComparison.OrdinalIgnoreCase);
            })
            // ── Sort by selected-timeframe volume ────────────────────────────
            .OrderByDescending(t => tf switch
            {
                "5m"  => t.VolumeM5,
                "15m" => t.VolumeM15,
                "6h"  => t.VolumeH24,
                "24h" => t.VolumeH24,
                _     => t.VolumeH1
            })
            .Select(t => new DexTokenRowViewModel(t, tf))
            .ToList();

        FilteredTokens = filtered;
        this.RaisePropertyChanged(nameof(HasNoFilteredTokens));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _cts.Cancel();
        _cts.Dispose();
        _svc.Dispose();
    }
}
