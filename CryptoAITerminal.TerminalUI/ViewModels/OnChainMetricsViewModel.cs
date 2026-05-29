using System;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Displays on-chain metrics (MVRV, NUPL, Exchange Net Flow, Realised Price)
/// with colour-coded market phase labels.
/// </summary>
public sealed class OnChainMetricsViewModel : ReactiveObject, IDisposable
{
    private readonly OnChainMetricsService _service;
    private OnChainSnapshot? _latest;

    // ── Backing fields ────────────────────────────────────────────────────────
    private string _btcMvrvLabel        = "–";
    private string _ethMvrvLabel        = "–";
    private string _btcNuplLabel        = "–";
    private string _btcNetFlowLabel     = "–";
    private string _ethNetFlowLabel     = "–";
    private string _btcRealisedLabel    = "–";
    private string _btcMvrvPhase        = "–";
    private string _btcNuplPhase        = "–";
    private string _btcMvrvColor        = "#8FA3B8";
    private string _ethMvrvColor        = "#8FA3B8";
    private string _btcNuplColor        = "#8FA3B8";
    private string _btcNetFlowColor     = "#8FA3B8";
    private string _ethNetFlowColor     = "#8FA3B8";
    private string _lastUpdatedLabel    = "–";
    private bool   _isLoading           = true;
    private bool   _hasApiKey;
    private string _apiKeyStatus        = "";

    // ── Properties ────────────────────────────────────────────────────────────

    public string BtcMvrvLabel      { get => _btcMvrvLabel;     private set => this.RaiseAndSetIfChanged(ref _btcMvrvLabel, value); }
    public string EthMvrvLabel      { get => _ethMvrvLabel;     private set => this.RaiseAndSetIfChanged(ref _ethMvrvLabel, value); }
    public string BtcNuplLabel      { get => _btcNuplLabel;     private set => this.RaiseAndSetIfChanged(ref _btcNuplLabel, value); }
    public string BtcNetFlowLabel   { get => _btcNetFlowLabel;  private set => this.RaiseAndSetIfChanged(ref _btcNetFlowLabel, value); }
    public string EthNetFlowLabel   { get => _ethNetFlowLabel;  private set => this.RaiseAndSetIfChanged(ref _ethNetFlowLabel, value); }
    public string BtcRealisedLabel  { get => _btcRealisedLabel; private set => this.RaiseAndSetIfChanged(ref _btcRealisedLabel, value); }
    public string BtcMvrvPhase      { get => _btcMvrvPhase;     private set => this.RaiseAndSetIfChanged(ref _btcMvrvPhase, value); }
    public string BtcNuplPhase      { get => _btcNuplPhase;     private set => this.RaiseAndSetIfChanged(ref _btcNuplPhase, value); }
    public string BtcMvrvColor      { get => _btcMvrvColor;     private set => this.RaiseAndSetIfChanged(ref _btcMvrvColor, value); }
    public string EthMvrvColor      { get => _ethMvrvColor;     private set => this.RaiseAndSetIfChanged(ref _ethMvrvColor, value); }
    public string BtcNuplColor      { get => _btcNuplColor;     private set => this.RaiseAndSetIfChanged(ref _btcNuplColor, value); }
    public string BtcNetFlowColor   { get => _btcNetFlowColor;  private set => this.RaiseAndSetIfChanged(ref _btcNetFlowColor, value); }
    public string EthNetFlowColor   { get => _ethNetFlowColor;  private set => this.RaiseAndSetIfChanged(ref _ethNetFlowColor, value); }
    public string LastUpdatedLabel  { get => _lastUpdatedLabel; private set => this.RaiseAndSetIfChanged(ref _lastUpdatedLabel, value); }
    public bool   IsLoading         { get => _isLoading;        private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    public bool   HasApiKey         { get => _hasApiKey;        private set => this.RaiseAndSetIfChanged(ref _hasApiKey, value); }
    public string ApiKeyStatus      { get => _apiKeyStatus;     private set => this.RaiseAndSetIfChanged(ref _apiKeyStatus, value); }

    // Computed gauge percentage (0–100) for MVRV and NUPL progress bars
    public double BtcMvrvGaugePct   => _latest?.BtcMvrv is { } v  ? Math.Clamp((double)v / 5.0 * 100, 0, 100) : 0;
    public double BtcNuplGaugePct   => _latest?.BtcNupl is { } v  ? Math.Clamp(((double)v + 1.0) / 2.0 * 100, 0, 100) : 50;

    /// <summary>Fired (on UI thread) when a critical on-chain signal fires.</summary>
    public event Action<string>? AlertTriggered;

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public OnChainMetricsViewModel(OnChainMetricsService service)
    {
        _service   = service;
        HasApiKey  = service.HasApiKey;
        var glassnodeKey = Environment.GetEnvironmentVariable("GLASSNODE_API_KEY");
        ApiKeyStatus = !string.IsNullOrWhiteSpace(glassnodeKey)
            ? "✓ GLASSNODE_API_KEY detected — live data active"
            : "CoinMetrics Community API — free, no key required";

        _service.SnapshotReceived += OnSnapshotReceived;

        RefreshCommand = ReactiveCommand.Create(() =>
        {
            IsLoading = true;
            _service.Stop();
            _service.Start();
        }, outputScheduler: App.UiScheduler);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void OnSnapshotReceived(OnChainSnapshot snap)
    {
        Dispatcher.UIThread.Post(() => ApplySnapshot(snap), DispatcherPriority.Background);
    }

    private void ApplySnapshot(OnChainSnapshot s)
    {
        _latest   = s;
        IsLoading = false;

        BtcMvrvLabel   = s.BtcMvrv.HasValue     ? $"{s.BtcMvrv:N2}"     : "–";
        EthMvrvLabel   = s.EthMvrv.HasValue      ? $"{s.EthMvrv:N2}"     : "–";
        BtcNuplLabel   = s.BtcNupl.HasValue      ? $"{s.BtcNupl:+0.####;-0.####;0}" : "–";
        BtcNetFlowLabel = s.BtcNetFlow.HasValue  ? FormatFlow(s.BtcNetFlow.Value) : "–";
        EthNetFlowLabel = s.EthNetFlow.HasValue  ? FormatFlow(s.EthNetFlow.Value) : "–";
        BtcRealisedLabel = s.BtcRealisedPrice.HasValue ? $"${s.BtcRealisedPrice:N0}" : "–";

        BtcMvrvPhase = MvrvPhase(s.BtcMvrv);
        BtcNuplPhase = NuplPhase(s.BtcNupl);

        BtcMvrvColor    = MvrvColor(s.BtcMvrv);
        EthMvrvColor    = MvrvColor(s.EthMvrv);
        BtcNuplColor    = NuplColor(s.BtcNupl);
        BtcNetFlowColor = FlowColor(s.BtcNetFlow);
        EthNetFlowColor = FlowColor(s.EthNetFlow);

        LastUpdatedLabel = $"Updated {s.Timestamp.ToLocalTime():HH:mm:ss}";

        this.RaisePropertyChanged(nameof(BtcMvrvGaugePct));
        this.RaisePropertyChanged(nameof(BtcNuplGaugePct));

        CheckAlerts(s);
    }

    private void CheckAlerts(OnChainSnapshot s)
    {
        if (s.BtcMvrv >= 3.5m)
            AlertTriggered?.Invoke($"⚠ BTC MVRV = {s.BtcMvrv:N2} — market historically overheated (> 3.5)");
        if (s.BtcMvrv <= 1.0m && s.BtcMvrv > 0)
            AlertTriggered?.Invoke($"🟢 BTC MVRV = {s.BtcMvrv:N2} — historically undervalued (< 1.0)");
        if (s.BtcNupl >= 0.75m)
            AlertTriggered?.Invoke($"⚠ BTC NUPL = {s.BtcNupl:N3} — euphoria zone (> 0.75), historically near top");
        if (s.BtcNupl <= 0m)
            AlertTriggered?.Invoke($"🟢 BTC NUPL = {s.BtcNupl:N3} — capitulation / fear zone (< 0)");
        if (s.BtcNetFlow >= 5000m)
            AlertTriggered?.Invoke($"🔴 BTC Exchange Inflow +{s.BtcNetFlow:N0} BTC/day — sell pressure building");
        if (s.BtcNetFlow <= -5000m)
            AlertTriggered?.Invoke($"🟢 BTC Exchange Outflow {s.BtcNetFlow:N0} BTC/day — accumulation signal");
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static string FormatFlow(decimal btc) =>
        btc >= 0 ? $"+{btc:N0} BTC/day" : $"{btc:N0} BTC/day";

    private static string MvrvPhase(decimal? v) => v switch
    {
        null      => "–",
        <= 1.0m   => "Undervalued",
        <= 2.0m   => "Fair Value",
        <= 3.5m   => "Overvalued",
        _         => "Euphoria"
    };

    private static string NuplPhase(decimal? v) => v switch
    {
        null     => "–",
        < 0m     => "Capitulation",
        < 0.25m  => "Hope / Fear",
        < 0.50m  => "Optimism",
        < 0.75m  => "Belief",
        _        => "Euphoria"
    };

    private static string MvrvColor(decimal? v) => v switch
    {
        null     => "#8FA3B8",
        <= 1.0m  => "#21E6C1",
        <= 2.0m  => "#3DDC84",
        <= 3.5m  => "#F4D03F",
        _        => "#FF6B6B"
    };

    private static string NuplColor(decimal? v) => v switch
    {
        null     => "#8FA3B8",
        < 0m     => "#21E6C1",
        < 0.25m  => "#8FA3B8",
        < 0.50m  => "#F4D03F",
        < 0.75m  => "#FF9500",
        _        => "#FF6B6B"
    };

    private static string FlowColor(decimal? v) => v switch
    {
        null    => "#8FA3B8",
        > 5000m => "#FF6B6B",   // large inflow = sell pressure
        > 0m    => "#F4D03F",
        > -5000m => "#3DDC84",
        _        => "#21E6C1"   // large outflow = accumulation
    };

    public void Dispose()
    {
        _service.SnapshotReceived -= OnSnapshotReceived;
    }
}
