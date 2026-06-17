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

    public string VenueLabel => Trade.Venue;
    public string VenueBrush => Trade.Venue == "DEX" ? "#B084F5" : "#5AA9E6";

    // CEX is anonymous; DEX carries the originating wallet — show a short form.
    public string TraderLabel => Trade.Trader is { Length: > 10 } w
        ? $"{w[..6]}…{w[^4..]}"
        : Trade.Trader ?? "—";

    public string SideBrush  => Trade.Side == "SELL" ? "#FF6B6B" : "#3DDC84";
    public string RowBrush   => IsLarge ? "#1A2A3A" : "Transparent";
    public string Weight     => IsLarge ? "Bold" : "Normal";
}

// ── Master VM ─────────────────────────────────────────────────────────────────

/// <summary>
/// Live public-trade tape for one venue at a time. CEX shows a symbol's anonymous
/// fills; DEX shows a pool's on-chain swaps (with the originating wallet). Highlights
/// large prints and summarises buy/sell pressure.
/// </summary>
public sealed class MarketTapeViewModel : ReactiveObject, IDisposable
{
    private const int RowCap = 60;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);

    private readonly MarketTapeService _service;
    private readonly HashSet<string> _seen = new();

    private System.Timers.Timer? _timer;
    private CancellationTokenSource? _cts;
    private bool _polling;

    public ObservableCollection<TapeRowVM> Rows { get; } = [];

    public MarketTapeViewModel(MarketTapeService service)
    {
        _service = service;
        RefreshCommand   = ReactiveCommand.Create(ResetFeed, outputScheduler: App.UiScheduler);
        SelectCexCommand = ReactiveCommand.Create(() => SetVenue("CEX"), outputScheduler: App.UiScheduler);
        SelectDexCommand = ReactiveCommand.Create(() => SetVenue("DEX"), outputScheduler: App.UiScheduler);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand   { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectCexCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectDexCommand { get; }

    // ── venue ─────────────────────────────────────────────────────────────────

    private string _venue = "CEX";
    public string Venue
    {
        get => _venue;
        private set => this.RaiseAndSetIfChanged(ref _venue, value);
    }

    public bool   IsCex          => Venue == "CEX";
    public bool   IsDex          => Venue == "DEX";
    public string CexTabBrush    => IsCex ? "#12293A" : "Transparent";
    public string DexTabBrush    => IsDex ? "#12293A" : "Transparent";
    public string CexTabFg       => IsCex ? "#21E6C1" : "#7A96AF";
    public string DexTabFg       => IsDex ? "#B084F5" : "#7A96AF";

    private void SetVenue(string venue)
    {
        if (Venue == venue) return;
        Venue = venue;
        this.RaisePropertyChanged(nameof(IsCex));
        this.RaisePropertyChanged(nameof(IsDex));
        this.RaisePropertyChanged(nameof(CexTabBrush));
        this.RaisePropertyChanged(nameof(DexTabBrush));
        this.RaisePropertyChanged(nameof(CexTabFg));
        this.RaisePropertyChanged(nameof(DexTabFg));
        ResetFeed();
    }

    // ── inputs ────────────────────────────────────────────────────────────────

    private string _symbol = "BTCUSDT";
    public string Symbol
    {
        get => _symbol;
        set => this.RaiseAndSetIfChanged(ref _symbol, value);
    }

    private string _network = "eth";
    public string Network
    {
        get => _network;
        set => this.RaiseAndSetIfChanged(ref _network, value);
    }

    // Default: Uniswap v3 WETH/USDC on Ethereum — a high-volume pool so the demo is lively.
    private string _poolAddress = "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640";
    public string PoolAddress
    {
        get => _poolAddress;
        set => this.RaiseAndSetIfChanged(ref _poolAddress, value);
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
        _seen.Clear();
        Rows.Clear();
        StatusLabel = $"Loading {SourceLabel()}…";
        if (_timer is null)
        {
            Start();
        }
    }

    private string SourceLabel() =>
        IsDex ? $"{Network.ToLowerInvariant()} pool" : Symbol.ToUpperInvariant();

    // ── polling ─────────────────────────────────────────────────────────────────

    private async Task PollAsync()
    {
        if (_polling) return; // skip overlapping ticks
        _polling = true;
        var token = _cts?.Token ?? CancellationToken.None;
        var venue = Venue;

        try
        {
            var trades = venue == "DEX"
                ? await _service.GetDexPoolTradesAsync(Network, PoolAddress, token).ConfigureAwait(false)
                : await _service.GetRecentTradesAsync(Symbol, 50, token).ConfigureAwait(false);

            if (token.IsCancellationRequested || venue != Venue) return;

            // Only trades we have not shown yet, oldest-first so the newest ends up on top.
            var fresh = trades
                .Where(t => _seen.Add(t.DedupKey))
                .OrderBy(t => t.TimeUtc)
                .ThenBy(t => t.Id)
                .ToList();

            var threshold = LargePrintThreshold;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var t in fresh)
                {
                    Rows.Insert(0, new TapeRowVM(t, threshold > 0m && t.QuoteQty >= threshold));
                }

                while (Rows.Count > RowCap)
                {
                    Rows.RemoveAt(Rows.Count - 1);
                }

                UpdateStats(threshold);
                StatusLabel = $"{venue} · {SourceLabel()} · {Rows.Count} trades · live";
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
