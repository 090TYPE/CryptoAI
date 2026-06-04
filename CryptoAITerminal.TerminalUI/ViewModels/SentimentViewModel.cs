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

    /// <summary>Raw latest Deribit options snapshot (numeric IV/skew/PCR) for the options advisor.</summary>
    public DeribitOptionsSnapshot? OptionsSnapshot => _deribitSnap;

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

    // ── AI sentiment insight (#7) ───────────────────────────────────────────────
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AnalyzeWithAiCommand { get; }
    private readonly MarketInsightAiService _insight = new();
    private bool _insightRunning, _hasInsight;
    private string _insightSummary = "", _insightSignal = "", _insightBullets = "", _insightSource = "";

    public bool InsightRunning { get => _insightRunning; private set => this.RaiseAndSetIfChanged(ref _insightRunning, value); }
    public bool HasInsight { get => _hasInsight; private set => this.RaiseAndSetIfChanged(ref _hasInsight, value); }
    public string InsightSummary { get => _insightSummary; private set => this.RaiseAndSetIfChanged(ref _insightSummary, value); }
    public string InsightSignal { get => _insightSignal; private set => this.RaiseAndSetIfChanged(ref _insightSignal, value); }
    public string InsightBullets { get => _insightBullets; private set => this.RaiseAndSetIfChanged(ref _insightBullets, value); }
    public string InsightSource { get => _insightSource; private set => this.RaiseAndSetIfChanged(ref _insightSource, value); }
    public string InsightSignalBrush => _insightSignal switch { "BULLISH" => "#3DDC84", "BEARISH" => "#FF6B6B", _ => "#8FA3B8" };

    public void ConfigureAi(string apiKey, string model)
    {
        _insight.ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(model)) _insight.Model = model;
    }

    private async System.Threading.Tasks.Task AnalyzeWithAiAsync()
    {
        if (InsightRunning) return;
        var lines = new System.Collections.Generic.List<string>
        {
            $"Fear & Greed: {FearGreedValue} ({FearGreedLabel}), prev {FearGreedPrevious}",
            $"Long/Short: {_longRatio:P0} long / {_shortRatio:P0} short",
            $"Open interest: {OpenInterest:N0} ({OpenInterestChange24h:+0.0;-0.0}% 24h)",
            $"Deribit sentiment: {DeribitSentiment}  BTC IV {DeribitBtcIv}  P/C {DeribitBtcPcRatio}  skew {DeribitBtcSkew}",
        };
        InsightRunning = true;
        try
        {
            var offline = () =>
            {
                var signal = FearGreedValue <= 25 ? "BULLISH"            // extreme fear → contrarian long
                           : FearGreedValue >= 75 ? "BEARISH"            // extreme greed → caution
                           : _longRatio > 0.65 ? "BEARISH"               // crowded longs
                           : _shortRatio > 0.65 ? "BULLISH" : "NEUTRAL";
                var summary = $"Fear & Greed {FearGreedValue} ({FearGreedLabel}); crowd is {_longRatio:P0} long. " +
                              (FearGreedValue <= 25 ? "Extreme fear often marks contrarian bottoms." :
                               FearGreedValue >= 75 ? "Extreme greed warns of froth." : "Positioning is balanced.");
                return new CryptoAITerminal.AIEngine.InsightResult(summary, signal, lines.ToArray(), "Heuristic (offline)", true);
            };
            var result = await _insight.InterpretAsync(
                "You are a market-sentiment analyst. Extreme fear is contrarian-bullish, extreme greed contrarian-bearish; crowded long positioning is a risk.",
                lines, ["BULLISH", "BEARISH", "NEUTRAL"], offline).ConfigureAwait(true);
            InsightSummary = result.Summary; InsightSignal = result.Signal;
            InsightBullets = result.Bullets.Length > 0 ? "• " + string.Join("\n• ", result.Bullets) : "";
            InsightSource = result.Source; HasInsight = true;
            this.RaisePropertyChanged(nameof(InsightSignalBrush));
        }
        catch (Exception ex) { InsightSummary = $"AI failed: {ex.Message}"; HasInsight = true; }
        finally { InsightRunning = false; }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SentimentViewModel(SentimentService svc)
    {
        _svc = svc;
        AnalyzeWithAiCommand = ReactiveCommand.CreateFromTask(AnalyzeWithAiAsync, outputScheduler: App.UiScheduler);

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
