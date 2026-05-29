using Avalonia.Media;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class SentimentViewModel : ReactiveObject, IDisposable
{
    private readonly SentimentService      _svc;
    private readonly DispatcherTimer       _refreshTimer;
    private readonly DeribitOptionsService _deribit = new();
    private CancellationTokenSource        _cts = new();

    // ── Deribit options data ──────────────────────────────────────────────────
    private DeribitOptionsSnapshot? _deribitSnap;

    public string DeribitBtcIv        => _deribitSnap?.Btc.IvLabel       ?? "--";
    public string DeribitEthIv        => _deribitSnap?.Eth.IvLabel       ?? "--";
    public string DeribitBtcPcRatio   => _deribitSnap?.Btc.PcRatioLabel  ?? "--";
    public string DeribitEthPcRatio   => _deribitSnap?.Eth.PcRatioLabel  ?? "--";
    public string DeribitBtcSkew      => _deribitSnap?.Btc.SkewLabel     ?? "--";
    public string DeribitEthSkew      => _deribitSnap?.Eth.SkewLabel     ?? "--";
    public string DeribitBtcSkewBrush => _deribitSnap?.Btc.SkewBrush     ?? "#8FA3B8";
    public string DeribitEthSkewBrush => _deribitSnap?.Eth.SkewBrush     ?? "#8FA3B8";
    public string DeribitSentiment    => _deribitSnap?.MarketSentiment   ?? "No data";
    public string DeribitSentimentBrush => _deribitSnap?.MarketSentimentBrush ?? "#8FA3B8";
    public string DeribitUpdated      => _deribitSnap?.UpdatedLabel      ?? "Not loaded";
    public bool   HasDeribitData      => _deribitSnap is not null;

    // ── Backing fields ────────────────────────────────────────────────────────
    private string  _symbol              = "BTCUSDT";
    private int     _fearGreedValue      = 50;
    private int     _fearGreedPrevious   = 50;
    private string  _fearGreedLabel      = "Neutral";
    private double  _longRatio           = 0.5;
    private double  _shortRatio          = 0.5;
    private decimal _openInterest        = 0m;
    private decimal _openInterestChange  = 0m;
    private string  _statusLabel         = "Loading…";

    // ── Public properties ─────────────────────────────────────────────────────

    public string Symbol
    {
        get => _symbol;
        set
        {
            this.RaiseAndSetIfChanged(ref _symbol, value?.ToUpperInvariant() ?? "BTCUSDT");
            _ = LoadAsync();
        }
    }

    public int FearGreedValue
    {
        get => _fearGreedValue;
        private set
        {
            this.RaiseAndSetIfChanged(ref _fearGreedValue, value);
            this.RaisePropertyChanged(nameof(FearGreedBrush));
            this.RaisePropertyChanged(nameof(FearGreedChangeLabel));
            this.RaisePropertyChanged(nameof(FearGreedChangeBrush));
        }
    }

    public int FearGreedPrevious
    {
        get => _fearGreedPrevious;
        private set
        {
            this.RaiseAndSetIfChanged(ref _fearGreedPrevious, value);
            this.RaisePropertyChanged(nameof(FearGreedChangeLabel));
            this.RaisePropertyChanged(nameof(FearGreedChangeBrush));
        }
    }

    public string FearGreedLabel
    {
        get => _fearGreedLabel;
        private set => this.RaiseAndSetIfChanged(ref _fearGreedLabel, value);
    }

    public double LongRatio
    {
        get => _longRatio;
        private set
        {
            this.RaiseAndSetIfChanged(ref _longRatio, value);
            this.RaisePropertyChanged(nameof(LongLabel));
            this.RaisePropertyChanged(nameof(LongBarWidth));
        }
    }

    public double ShortRatio
    {
        get => _shortRatio;
        private set
        {
            this.RaiseAndSetIfChanged(ref _shortRatio, value);
            this.RaisePropertyChanged(nameof(ShortLabel));
        }
    }

    public decimal OpenInterest
    {
        get => _openInterest;
        private set
        {
            this.RaiseAndSetIfChanged(ref _openInterest, value);
            this.RaisePropertyChanged(nameof(OpenInterestLabel));
        }
    }

    public decimal OpenInterestChange24h
    {
        get => _openInterestChange;
        private set
        {
            this.RaiseAndSetIfChanged(ref _openInterestChange, value);
            this.RaisePropertyChanged(nameof(OpenInterestChangeBrush));
            this.RaisePropertyChanged(nameof(OpenInterestChangeLabel));
        }
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => this.RaiseAndSetIfChanged(ref _statusLabel, value);
    }

    // ── Computed IBrush properties ────────────────────────────────────────────

    public IBrush FearGreedBrush =>
        _fearGreedValue <= 24 ? new SolidColorBrush(Color.Parse("#FF4444")) :
        _fearGreedValue <= 44 ? new SolidColorBrush(Color.Parse("#FF8C42")) :
        _fearGreedValue <= 55 ? new SolidColorBrush(Color.Parse("#F4B860")) :
        _fearGreedValue <= 74 ? new SolidColorBrush(Color.Parse("#4CAF50")) :
                                new SolidColorBrush(Color.Parse("#21E6C1"));

    public IBrush FearGreedChangeBrush =>
        _fearGreedValue >= _fearGreedPrevious
            ? new SolidColorBrush(Color.Parse("#4CAF50"))
            : new SolidColorBrush(Color.Parse("#FF4444"));

    public IBrush OpenInterestChangeBrush =>
        _openInterestChange >= 0
            ? new SolidColorBrush(Color.Parse("#4CAF50"))
            : new SolidColorBrush(Color.Parse("#FF4444"));

    // ── Computed string labels ────────────────────────────────────────────────

    public string FearGreedChangeLabel
    {
        get
        {
            var diff = _fearGreedValue - _fearGreedPrevious;
            if (diff == 0) return "";
            return diff > 0 ? $"▲ {diff}" : $"▼ {Math.Abs(diff)}";
        }
    }

    public string LongLabel  => $"{_longRatio  * 100.0:F1}% Long";
    public string ShortLabel => $"{_shortRatio * 100.0:F1}% Short";

    public string OpenInterestLabel =>
        _openInterest >= 1_000_000_000m ? $"${_openInterest / 1_000_000_000m:N1}B" :
        _openInterest >= 1_000_000m     ? $"${_openInterest / 1_000_000m:N1}M" :
        _openInterest >= 1_000m         ? $"${_openInterest / 1_000m:N0}K" :
                                          $"${_openInterest:N0}";

    public string OpenInterestChangeLabel
    {
        get
        {
            var abs = Math.Abs(_openInterestChange);
            return _openInterestChange >= 0
                ? $"▲ {abs:F1}% 24h"
                : $"▼ {abs:F1}% 24h";
        }
    }

    /// <summary>Width of the Long bar in the 200px total bar visual.</summary>
    public double LongBarWidth => _longRatio * 200.0;

    // ── Constructor ───────────────────────────────────────────────────────────

    public SentimentViewModel(SentimentService svc)
    {
        _svc = svc;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _refreshTimer.Tick += async (_, _) => await LoadAsync();
        _refreshTimer.Start();

        // Deribit options feed
        _deribit.SnapshotUpdated += snap =>
        {
            _deribitSnap = snap;
            this.RaisePropertyChanged(nameof(DeribitBtcIv));
            this.RaisePropertyChanged(nameof(DeribitEthIv));
            this.RaisePropertyChanged(nameof(DeribitBtcPcRatio));
            this.RaisePropertyChanged(nameof(DeribitEthPcRatio));
            this.RaisePropertyChanged(nameof(DeribitBtcSkew));
            this.RaisePropertyChanged(nameof(DeribitEthSkew));
            this.RaisePropertyChanged(nameof(DeribitBtcSkewBrush));
            this.RaisePropertyChanged(nameof(DeribitEthSkewBrush));
            this.RaisePropertyChanged(nameof(DeribitSentiment));
            this.RaisePropertyChanged(nameof(DeribitSentimentBrush));
            this.RaisePropertyChanged(nameof(DeribitUpdated));
            this.RaisePropertyChanged(nameof(HasDeribitData));
        };
        _deribit.Start();
        _ = _deribit.RefreshAsync();

        _ = LoadAsync();
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        StatusLabel = "Loading…";
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var snap = await _svc.FetchAsync(_symbol, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            // All property updates must happen on the UI thread
            Dispatcher.UIThread.Post(() =>
            {
                FearGreedPrevious   = snap.FearGreedPrevious;
                FearGreedValue      = snap.FearGreedValue;
                FearGreedLabel      = snap.FearGreedLabel;
                LongRatio           = snap.LongRatio;
                ShortRatio          = snap.ShortRatio;
                OpenInterest        = snap.OpenInterest;
                OpenInterestChange24h = snap.OpenInterestChange24h;
                StatusLabel         = $"Updated {DateTime.Now:HH:mm}";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => StatusLabel = $"Error: {ex.Message}");
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _refreshTimer.Stop();
        _cts.Cancel();
        _svc.Dispose();
        _deribit.Dispose();
    }
}
