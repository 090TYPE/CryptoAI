using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ── Row ─────────────────────────────────────────────────────────────────────

public sealed class TapeRowVM
{
    public TapeRowVM(TapeTrade trade, bool isLarge)
    {
        Trade   = trade;
        IsLarge = isLarge;
    }

    public TapeTrade Trade   { get; }
    public bool      IsLarge { get; }

    public string TimeLabel  => Trade.TimeUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Side       => Trade.Side;
    public string PriceLabel => Trade.Price.ToString("0.########");
    public string QtyLabel   => Trade.Quantity.ToString("0.######");
    public string QuoteLabel => Trade.QuoteQty >= 1000m ? $"{Trade.QuoteQty / 1000m:0.0}K" : Trade.QuoteQty.ToString("0");

    public string SideBrush  => Trade.Side == "SELL" ? "#FF6B6B" : "#3DDC84";
    public string RowBrush   => IsLarge ? "#1A2A3A" : "Transparent";
    public string Weight     => IsLarge ? "Bold" : "Normal";
}

// ── Master VM ─────────────────────────────────────────────────────────────────

/// <summary>
/// Live public-trade tape for a single symbol: shows every market participant's
/// anonymous fills, highlights large prints and summarises buy/sell pressure.
/// </summary>
public sealed class MarketTapeViewModel : ReactiveObject, IDisposable
{
    private const int RowCap = 60;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);

    private readonly MarketTapeService _service;
    private readonly HashSet<long> _seenIds = new();

    private System.Timers.Timer? _timer;
    private CancellationTokenSource? _cts;
    private bool _polling;
    private long _maxSeenId;

    public ObservableCollection<TapeRowVM> Rows { get; } = [];

    public MarketTapeViewModel(MarketTapeService service)
    {
        _service = service;
        RefreshCommand = ReactiveCommand.Create(() => { ResetFeed(); }, outputScheduler: App.UiScheduler);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }

    private string _symbol = "BTCUSDT";
    public string Symbol
    {
        get => _symbol;
        set => this.RaiseAndSetIfChanged(ref _symbol, value);
    }

    private decimal _largePrintThreshold = 50_000m;
    public decimal LargePrintThreshold
    {
        get => _largePrintThreshold;
        set => this.RaiseAndSetIfChanged(ref _largePrintThreshold, value);
    }

    private string _statusLabel = "Idle.";
    public string StatusLabel
    {
        get => _statusLabel;
        private set => this.RaiseAndSetIfChanged(ref _statusLabel, value);
    }

    private string _pressureLabel = "Buy/Sell pressure: —";
    public string PressureLabel
    {
        get => _pressureLabel;
        private set => this.RaiseAndSetIfChanged(ref _pressureLabel, value);
    }

    private string _pressureBrush = "#8FA3B8";
    public string PressureBrush
    {
        get => _pressureBrush;
        private set => this.RaiseAndSetIfChanged(ref _pressureBrush, value);
    }

    // ── lifecycle ───────────────────────────────────────────────────────────────

    /// <summary>Begins polling. Idempotent — safe to call each time the section is shown.</summary>
    public void Start()
    {
        if (_timer is not null) return;

        _cts = new CancellationTokenSource();
        _timer = new System.Timers.Timer(PollInterval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = PollAsync();
        _timer.Start();
        _ = PollAsync(); // immediate first fill
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void ResetFeed()
    {
        _seenIds.Clear();
        _maxSeenId = 0;
        Rows.Clear();
        StatusLabel = $"Loading {Symbol.ToUpperInvariant()}…";
        if (_timer is null)
        {
            Start();
        }
    }

    // ── polling ─────────────────────────────────────────────────────────────────

    private async Task PollAsync()
    {
        if (_polling) return; // skip overlapping ticks
        _polling = true;
        var token = _cts?.Token ?? CancellationToken.None;
        var symbol = Symbol;

        try
        {
            var trades = await _service.GetRecentTradesAsync(symbol, 50, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            // Only rows we have not shown yet, oldest-first so newest ends up on top.
            var fresh = trades
                .Where(t => t.Id > _maxSeenId && _seenIds.Add(t.Id))
                .OrderBy(t => t.Id)
                .ToList();

            var threshold = LargePrintThreshold;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var t in fresh)
                {
                    if (t.Id > _maxSeenId) _maxSeenId = t.Id;
                    Rows.Insert(0, new TapeRowVM(t, threshold > 0m && t.QuoteQty >= threshold));
                }

                while (Rows.Count > RowCap)
                {
                    Rows.RemoveAt(Rows.Count - 1);
                }

                UpdateStats(threshold);
                StatusLabel = $"{symbol.ToUpperInvariant()} · {Rows.Count} trades · live";
            });
        }
        catch (OperationCanceledException)
        {
            // section closed mid-flight — ignore
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => StatusLabel = $"Tape unavailable: {ex.Message}");
        }
        finally
        {
            _polling = false;
        }
    }

    private void UpdateStats(decimal threshold)
    {
        var stats = MarketTapeStats.Compute(Rows.Select(r => r.Trade), threshold);
        if (stats.TradeCount == 0)
        {
            PressureLabel = "Buy/Sell pressure: —";
            PressureBrush = "#8FA3B8";
            return;
        }

        var buyPct = (double)stats.BuyPressure * 100d;
        PressureLabel =
            $"Buy {buyPct:0}% / Sell {100d - buyPct:0}%  ·  {stats.LargePrintCount} large print(s) ≥ {threshold:N0}";
        PressureBrush = buyPct >= 60d ? "#3DDC84" : buyPct <= 40d ? "#FF6B6B" : "#F4B860";
    }

    public void Dispose() => Stop();
}
