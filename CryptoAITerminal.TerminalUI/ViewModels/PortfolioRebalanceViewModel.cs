using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class PortfolioRebalanceViewModel : ReactiveObject, IDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly PortfolioRebalanceService _svc;
    private readonly DispatcherTimer           _autoRefreshTimer;
    private CancellationTokenSource            _cts = new();
    private bool _disposed;

    // ── Persisted storage ─────────────────────────────────────────────────────

    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "portfolio-allocations.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Backing fields ────────────────────────────────────────────────────────

    private bool    _isLoading;
    private string  _statusLabel         = "Ready";
    private decimal _totalValueUsd;
    private double  _globalAlertThresholdPct = 5.0;
    private string  _newAssetSymbol      = "";
    private double  _newAssetTargetPct   = 10.0;
    private string  _alertSummary        = "";
    private bool    _hasAlerts;
    private bool    _targetSumValid;
    private double  _targetSum;

    // ── Public observable collection ──────────────────────────────────────────

    /// <summary>All allocation rows — directly bound to the table in XAML.</summary>
    public ObservableCollection<PortfolioAllocationRowViewModel> Allocations { get; } = [];

    // ── Scalar properties ─────────────────────────────────────────────────────

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

    public decimal TotalValueUsd
    {
        get => _totalValueUsd;
        private set
        {
            this.RaiseAndSetIfChanged(ref _totalValueUsd, value);
            this.RaisePropertyChanged(nameof(TotalValueLabel));
        }
    }

    public string TotalValueLabel => PortfolioAllocationRowViewModel.FormatMoney(_totalValueUsd);

    /// <summary>
    /// Global deviation threshold in percentage points.
    /// Changing this propagates to every row immediately.
    /// </summary>
    public double GlobalAlertThresholdPct
    {
        get => _globalAlertThresholdPct;
        set
        {
            this.RaiseAndSetIfChanged(ref _globalAlertThresholdPct, Math.Max(0.5, Math.Round(value, 1)));
            foreach (var row in Allocations)
                row.AlertThresholdPct = _globalAlertThresholdPct;
            RefreshAlertState();
        }
    }

    public bool HasAlerts
    {
        get => _hasAlerts;
        private set => this.RaiseAndSetIfChanged(ref _hasAlerts, value);
    }

    public string AlertSummary
    {
        get => _alertSummary;
        private set => this.RaiseAndSetIfChanged(ref _alertSummary, value);
    }

    // ── Target-sum validation ─────────────────────────────────────────────────

    public double TargetSum
    {
        get => _targetSum;
        private set
        {
            this.RaiseAndSetIfChanged(ref _targetSum, value);
            this.RaisePropertyChanged(nameof(TargetSumLabel));
            this.RaisePropertyChanged(nameof(TargetSumBrush));
        }
    }

    public bool TargetSumValid
    {
        get => _targetSumValid;
        private set => this.RaiseAndSetIfChanged(ref _targetSumValid, value);
    }

    public string TargetSumLabel => $"Σ targets = {_targetSum:F1}%{(_targetSumValid ? " ✓" : " ≠ 100")}";

    public Avalonia.Media.IBrush TargetSumBrush =>
        _targetSumValid
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#21E6C1"))
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFAA33"));

    // ── "Add asset" form ─────────────────────────────────────────────────────

    public string NewAssetSymbol
    {
        get => _newAssetSymbol;
        set => this.RaiseAndSetIfChanged(ref _newAssetSymbol, value?.ToUpperInvariant() ?? "");
    }

    public double NewAssetTargetPct
    {
        get => _newAssetTargetPct;
        set => this.RaiseAndSetIfChanged(ref _newAssetTargetPct, Math.Clamp(value, 0.1, 100.0));
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>                                 RefreshCommand        { get; }
    public ReactiveCommand<Unit, Unit>                                 AddAssetCommand       { get; }
    public ReactiveCommand<PortfolioAllocationRowViewModel, Unit>      RemoveAssetCommand    { get; }
    public ReactiveCommand<PortfolioAllocationRowViewModel, Unit>      MoveUpCommand         { get; }
    public ReactiveCommand<PortfolioAllocationRowViewModel, Unit>      MoveDownCommand       { get; }

    /// <summary>Raised when the rebalance engine detects a deviation exceeding the alert threshold.</summary>
    public event Action<string>? AlertFired;

    // ── Constructor ───────────────────────────────────────────────────────────

    public PortfolioRebalanceViewModel(PortfolioRebalanceService svc)
    {
        _svc = svc;

        RefreshCommand = ReactiveCommand.CreateFromTask(
            _ => RefreshAsync(), outputScheduler: App.UiScheduler);

        AddAssetCommand = ReactiveCommand.Create(
            () => AddAsset(), outputScheduler: App.UiScheduler);

        RemoveAssetCommand = ReactiveCommand.Create<PortfolioAllocationRowViewModel>(
            row => RemoveAsset(row), outputScheduler: App.UiScheduler);

        MoveUpCommand = ReactiveCommand.Create<PortfolioAllocationRowViewModel>(
            row => MoveRow(row, -1), outputScheduler: App.UiScheduler);

        MoveDownCommand = ReactiveCommand.Create<PortfolioAllocationRowViewModel>(
            row => MoveRow(row, +1), outputScheduler: App.UiScheduler);

        // Load persisted allocations (or seed with BTC/ETH/SOL defaults)
        LoadAllocations();

        // Auto-refresh every 60 seconds
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAsync();
        _autoRefreshTimer.Start();

        _ = RefreshAsync();
    }

    // ── Add / Remove / Reorder ────────────────────────────────────────────────

    private void AddAsset()
    {
        var sym = _newAssetSymbol.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(sym)) return;
        if (Allocations.Any(r => string.Equals(r.Symbol, sym, StringComparison.OrdinalIgnoreCase)))
            return;   // duplicate

        var row = new PortfolioAllocationRowViewModel(sym, _newAssetTargetPct,
            alertThresholdPct: _globalAlertThresholdPct);
        Allocations.Add(row);
        NewAssetSymbol   = "";
        NewAssetTargetPct = 10.0;
        RecalcTargetSum();
        SaveAllocations();
    }

    private void RemoveAsset(PortfolioAllocationRowViewModel row)
    {
        Allocations.Remove(row);
        RecalcTargetSum();
        SaveAllocations();
    }

    private void MoveRow(PortfolioAllocationRowViewModel row, int delta)
    {
        var idx = Allocations.IndexOf(row);
        if (idx < 0) return;
        var newIdx = Math.Clamp(idx + delta, 0, Allocations.Count - 1);
        if (newIdx == idx) return;
        Allocations.Move(idx, newIdx);
        SaveAllocations();
    }

    // ── Data refresh ─────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        if (_isLoading) return;

        IsLoading   = true;
        StatusLabel = "Fetching balances and prices…";

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var allocations = BuildAllocationModels();
            var snapshots   = await _svc.FetchSnapshotAsync(allocations, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            // Apply results on the UI thread
            Dispatcher.UIThread.Post(() =>
            {
                var snapshotMap = snapshots.ToDictionary(
                    s => s.Symbol, StringComparer.OrdinalIgnoreCase);

                foreach (var row in Allocations)
                {
                    if (snapshotMap.TryGetValue(row.Symbol, out var snap))
                        row.ApplySnapshot(snap);
                }

                TotalValueUsd = snapshots.Sum(s => s.ValueUsd);
                RecalcTargetSum();
                RefreshAlertState();
                StatusLabel = $"Updated {DateTime.Now:HH:mm:ss}  ·  Total: {TotalValueLabel}";
                IsLoading   = false;

                SaveAllocations();
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

    // ── Alert detection ───────────────────────────────────────────────────────

    private void RefreshAlertState()
    {
        var alerts = Allocations.Where(r => r.NeedsAlert).ToList();
        HasAlerts = alerts.Count > 0;

        if (!HasAlerts)
        {
            AlertSummary = "All allocations within threshold.";
            return;
        }

        var lines = alerts.Select(r =>
        {
            var dir  = r.IsOverweight ? "overweight" : "underweight";
            var sign = r.DeviationPct > 0 ? "+" : "";
            return $"{r.Symbol} {dir} by {sign}{r.DeviationPct:F1}%  →  {r.RebalanceOrderLabel}";
        });

        AlertSummary = string.Join("\n", lines);
        AlertFired?.Invoke($"Portfolio drift detected:\n{AlertSummary}");
    }

    // ── Target-sum guard ─────────────────────────────────────────────────────

    private void RecalcTargetSum()
    {
        var sum = Allocations.Sum(r => r.TargetPct);
        TargetSum      = Math.Round(sum, 1);
        TargetSumValid = Math.Abs(sum - 100.0) < 0.05;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private IReadOnlyList<PortfolioAllocation> BuildAllocationModels() =>
        Allocations.Select(r => new PortfolioAllocation
        {
            Symbol     = r.Symbol,
            TargetPct  = r.TargetPct,
            IncludeCex = r.IncludeCex,
            IncludeDex = r.IncludeDex
        }).ToList();

    private void SaveAllocations()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            var wrapper = new PersistedPortfolio
            {
                GlobalAlertThresholdPct = _globalAlertThresholdPct,
                Allocations             = BuildAllocationModels().ToList()
            };
            File.WriteAllText(StoragePath, JsonSerializer.Serialize(wrapper, JsonOpts));
        }
        catch { /* persistence failure is non-fatal */ }
    }

    private void LoadAllocations()
    {
        try
        {
            if (!File.Exists(StoragePath)) { SeedDefaults(); return; }

            var json    = File.ReadAllText(StoragePath);
            var wrapper = JsonSerializer.Deserialize<PersistedPortfolio>(json, JsonOpts);
            if (wrapper is null || wrapper.Allocations.Count == 0) { SeedDefaults(); return; }

            _globalAlertThresholdPct = Math.Max(0.5, wrapper.GlobalAlertThresholdPct);

            foreach (var alloc in wrapper.Allocations)
            {
                if (string.IsNullOrWhiteSpace(alloc.Symbol)) continue;
                Allocations.Add(new PortfolioAllocationRowViewModel(
                    alloc.Symbol, alloc.TargetPct, alloc.IncludeCex, alloc.IncludeDex,
                    _globalAlertThresholdPct));
            }
        }
        catch
        {
            SeedDefaults();
        }

        RecalcTargetSum();
    }

    private void SeedDefaults()
    {
        Allocations.Clear();
        Allocations.Add(new PortfolioAllocationRowViewModel("BTC",  40.0, alertThresholdPct: _globalAlertThresholdPct));
        Allocations.Add(new PortfolioAllocationRowViewModel("ETH",  30.0, alertThresholdPct: _globalAlertThresholdPct));
        Allocations.Add(new PortfolioAllocationRowViewModel("SOL",  30.0, alertThresholdPct: _globalAlertThresholdPct));
        RecalcTargetSum();
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoRefreshTimer.Stop();
        _cts.Cancel();
        _cts.Dispose();
        _svc.Dispose();
    }

    // ── Serialization helpers ─────────────────────────────────────────────────

    private sealed class PersistedPortfolio
    {
        public double                    GlobalAlertThresholdPct { get; set; } = 5.0;
        public List<PortfolioAllocation> Allocations             { get; set; } = [];
    }
}
