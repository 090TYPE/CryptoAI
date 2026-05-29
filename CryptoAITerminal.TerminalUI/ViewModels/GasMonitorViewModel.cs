using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>One point in the ETH gas history sparkline.</summary>
public sealed record GasHistoryPoint(DateTime Time, decimal EthGwei);

/// <summary>
/// Displays live gas prices for Ethereum, BSC and Solana TPS.
/// Fires <see cref="AlertTriggered"/> when gas drops below user-defined thresholds.
/// </summary>
public sealed class GasMonitorViewModel : ReactiveObject, IDisposable
{
    private readonly GasMonitorService _service;
    private GasSnapshot? _latest;

    // ── Alert thresholds ──────────────────────────────────────────────────────
    private decimal _ethAlertGwei = 5m;
    private decimal _bscAlertGwei = 3m;

    // ── Backing fields ────────────────────────────────────────────────────────
    private string _ethSlowLabel     = "–";
    private string _ethStdLabel      = "–";
    private string _ethFastLabel     = "–";
    private string _bscSlowLabel     = "–";
    private string _bscStdLabel      = "–";
    private string _bscFastLabel     = "–";
    private string _solanaTpsLabel   = "–";
    private string _lastUpdatedLabel = "–";
    private bool   _isEthAlertActive;
    private bool   _isBscAlertActive;
    private string _ethSlowColor  = "#8FA3B8";
    private string _ethStdColor   = "#8FA3B8";
    private string _ethFastColor  = "#8FA3B8";
    private string _bscSlowColor  = "#8FA3B8";
    private string _bscStdColor   = "#8FA3B8";
    private string _bscFastColor  = "#8FA3B8";
    private string _solanaColor   = "#8FA3B8";
    private string _alertMessage  = "";
    private bool   _isLoading     = true;

    // ── Observable history (kept as ≤ 60 points for a sparkline) ─────────────
    public ObservableCollection<GasHistoryPoint> EthHistory { get; } = [];

    // ── Public properties ─────────────────────────────────────────────────────

    public string EthSlowLabel     { get => _ethSlowLabel;     private set => this.RaiseAndSetIfChanged(ref _ethSlowLabel, value); }
    public string EthStdLabel      { get => _ethStdLabel;      private set => this.RaiseAndSetIfChanged(ref _ethStdLabel, value); }
    public string EthFastLabel     { get => _ethFastLabel;     private set => this.RaiseAndSetIfChanged(ref _ethFastLabel, value); }
    public string BscSlowLabel     { get => _bscSlowLabel;     private set => this.RaiseAndSetIfChanged(ref _bscSlowLabel, value); }
    public string BscStdLabel      { get => _bscStdLabel;      private set => this.RaiseAndSetIfChanged(ref _bscStdLabel, value); }
    public string BscFastLabel     { get => _bscFastLabel;     private set => this.RaiseAndSetIfChanged(ref _bscFastLabel, value); }
    public string SolanaTpsLabel   { get => _solanaTpsLabel;   private set => this.RaiseAndSetIfChanged(ref _solanaTpsLabel, value); }
    public string LastUpdatedLabel { get => _lastUpdatedLabel; private set => this.RaiseAndSetIfChanged(ref _lastUpdatedLabel, value); }
    public bool   IsEthAlertActive { get => _isEthAlertActive; private set => this.RaiseAndSetIfChanged(ref _isEthAlertActive, value); }
    public bool   IsBscAlertActive { get => _isBscAlertActive; private set => this.RaiseAndSetIfChanged(ref _isBscAlertActive, value); }
    public string EthSlowColor     { get => _ethSlowColor;     private set => this.RaiseAndSetIfChanged(ref _ethSlowColor, value); }
    public string EthStdColor      { get => _ethStdColor;      private set => this.RaiseAndSetIfChanged(ref _ethStdColor, value); }
    public string EthFastColor     { get => _ethFastColor;     private set => this.RaiseAndSetIfChanged(ref _ethFastColor, value); }
    public string BscSlowColor     { get => _bscSlowColor;     private set => this.RaiseAndSetIfChanged(ref _bscSlowColor, value); }
    public string BscStdColor      { get => _bscStdColor;      private set => this.RaiseAndSetIfChanged(ref _bscStdColor, value); }
    public string BscFastColor     { get => _bscFastColor;     private set => this.RaiseAndSetIfChanged(ref _bscFastColor, value); }
    public string SolanaColor      { get => _solanaColor;      private set => this.RaiseAndSetIfChanged(ref _solanaColor, value); }
    public string AlertMessage     { get => _alertMessage;     private set => this.RaiseAndSetIfChanged(ref _alertMessage, value); }
    public bool   IsLoading        { get => _isLoading;        private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    /// <summary>User-configurable ETH gas alert threshold (gwei).</summary>
    public decimal EthAlertGwei
    {
        get => _ethAlertGwei;
        set { this.RaiseAndSetIfChanged(ref _ethAlertGwei, value); CheckAlerts(_latest); }
    }

    /// <summary>User-configurable BSC gas alert threshold (gwei).</summary>
    public decimal BscAlertGwei
    {
        get => _bscAlertGwei;
        set { this.RaiseAndSetIfChanged(ref _bscAlertGwei, value); CheckAlerts(_latest); }
    }

    // ── Derived time-estimate labels (recomputed each snapshot) ──────────────
    public string EthSlowTimeLabel => EthTimeEstimate(_latest?.EthSlow     ?? 0);
    public string EthStdTimeLabel  => EthTimeEstimate(_latest?.EthStandard ?? 0);
    public string EthFastTimeLabel => EthTimeEstimate(_latest?.EthFast     ?? 0);

    // ── Commands ──────────────────────────────────────────────────────────────
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }

    /// <summary>Fired (on UI thread) when gas crosses below the alert threshold for the first time.</summary>
    public event Action<string>? AlertTriggered;

    // ── Constructor ───────────────────────────────────────────────────────────

    public GasMonitorViewModel(GasMonitorService service)
    {
        _service = service;
        _service.SnapshotReceived += OnSnapshotReceived;

        RefreshCommand = ReactiveCommand.Create(() =>
        {
            IsLoading = true;
            _service.Stop();
            _service.Start();
        }, outputScheduler: App.UiScheduler);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void OnSnapshotReceived(GasSnapshot snap)
    {
        Dispatcher.UIThread.Post(() => ApplySnapshot(snap), DispatcherPriority.Background);
    }

    private void ApplySnapshot(GasSnapshot s)
    {
        _latest  = s;
        IsLoading = false;

        EthSlowLabel   = FormatGwei(s.EthSlow);
        EthStdLabel    = FormatGwei(s.EthStandard);
        EthFastLabel   = FormatGwei(s.EthFast);
        BscSlowLabel   = FormatGwei(s.BscSlow);
        BscStdLabel    = FormatGwei(s.BscStandard);
        BscFastLabel   = FormatGwei(s.BscFast);
        SolanaTpsLabel = s.SolanaTps > 0 ? $"{s.SolanaTps:N0} TPS" : "–";
        LastUpdatedLabel = $"Updated {s.Timestamp.ToLocalTime():HH:mm:ss}";

        EthSlowColor = GweiColor(s.EthSlow,     isEth: true);
        EthStdColor  = GweiColor(s.EthStandard, isEth: true);
        EthFastColor = GweiColor(s.EthFast,     isEth: true);
        BscSlowColor = GweiColor(s.BscSlow,     isEth: false);
        BscStdColor  = GweiColor(s.BscStandard, isEth: false);
        BscFastColor = GweiColor(s.BscFast,     isEth: false);
        SolanaColor  = SolanaTpsColor(s.SolanaTps);

        this.RaisePropertyChanged(nameof(EthSlowTimeLabel));
        this.RaisePropertyChanged(nameof(EthStdTimeLabel));
        this.RaisePropertyChanged(nameof(EthFastTimeLabel));

        // Rolling history (max 60 points ≈ 30 min at default poll rate)
        EthHistory.Add(new GasHistoryPoint(s.Timestamp, s.EthStandard));
        while (EthHistory.Count > 60) EthHistory.RemoveAt(0);

        CheckAlerts(s);
    }

    private void CheckAlerts(GasSnapshot? s)
    {
        if (s is null) return;

        bool prevEth = IsEthAlertActive;
        bool prevBsc = IsBscAlertActive;

        IsEthAlertActive = s.EthSlow > 0 && s.EthSlow < _ethAlertGwei;
        IsBscAlertActive = s.BscSlow > 0 && s.BscSlow < _bscAlertGwei;

        var parts = new List<string>(2);
        if (IsEthAlertActive)
            parts.Add($"⛽ ETH {s.EthSlow:N0} gwei — great time for large swaps!");
        if (IsBscAlertActive)
            parts.Add($"⛽ BSC {s.BscSlow:N1} gwei — cheap transactions available.");
        AlertMessage = string.Join("   |   ", parts);

        // Rising-edge only — don't spam the alert on every tick
        if (IsEthAlertActive && !prevEth)
            AlertTriggered?.Invoke(
                $"⛽ ETH Gas dropped to {s.EthSlow:N0} gwei — good moment for large swaps!");
        if (IsBscAlertActive && !prevBsc)
            AlertTriggered?.Invoke(
                $"⛽ BSC Gas dropped to {s.BscSlow:N1} gwei — cheap transactions now.");
    }

    // ── Static formatting helpers ─────────────────────────────────────────────

    private static string FormatGwei(decimal gwei) =>
        gwei > 0 ? (gwei < 1m ? $"{gwei:N3} gwei" : $"{gwei:N1} gwei") : "–";

    /// <summary>Colour-codes a gwei value (green = cheap, red = expensive).</summary>
    private static string GweiColor(decimal gwei, bool isEth) =>
        gwei <= 0 ? "#8FA3B8"
        : isEth
            ? gwei < 10 ? "#21E6C1"   // very cheap
            : gwei < 30 ? "#F4D03F"   // normal
            : gwei < 80 ? "#FF9500"   // expensive
            : "#FF6B6B"               // very expensive
        : gwei < 1  ? "#21E6C1"       // BSC cheap (sub-gwei or < 1 gwei)
        : gwei < 5  ? "#F4D03F"       // BSC normal
        : "#FF6B6B";                  // BSC expensive

    private static string SolanaTpsColor(double tps) =>
        tps <= 0     ? "#8FA3B8"
        : tps > 2000 ? "#21E6C1"   // healthy
        : tps > 500  ? "#F4D03F"   // moderate
        : "#FF6B6B";               // congested

    /// <summary>Rough confirmation-time estimate for an Ethereum gas price.</summary>
    public static string EthTimeEstimate(decimal gwei) => gwei switch
    {
        <= 0    => "",
        < 5     => "~15 min+",
        < 10    => "~5 min",
        < 30    => "~1–3 min",
        < 80    => "~30 sec",
        _       => "< 15 sec"
    };

    public void Dispose()
    {
        _service.SnapshotReceived -= OnSnapshotReceived;
    }
}
