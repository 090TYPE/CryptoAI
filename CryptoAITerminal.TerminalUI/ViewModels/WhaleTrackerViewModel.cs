using Avalonia.Threading;
using CryptoAITerminal.TerminalUI;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.WhaleTracker;
using CryptoAITerminal.WhaleTracker.Models;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class WhaleTrackerViewModel : ReactiveObject, IDisposable
{
    private readonly WhaleTrackerService  _service;
    private readonly WhaleTokenEnricher?  _enricher;
    private IDisposable? _subscription;
    private bool _isTracking;
    private int _totalAlertsCount;
    private int _labeledAlertsCount;

    public ObservableCollection<WhaleAlertViewModel> RecentAlerts { get; } = new();

    public bool IsTracking
    {
        get => _isTracking;
        set => this.RaiseAndSetIfChanged(ref _isTracking, value);
    }

    public int TotalAlertsCount
    {
        get => _totalAlertsCount;
        set => this.RaiseAndSetIfChanged(ref _totalAlertsCount, value);
    }

    public int LabeledAlertsCount
    {
        get => _labeledAlertsCount;
        set => this.RaiseAndSetIfChanged(ref _labeledAlertsCount, value);
    }

    public decimal MinUsdValue => _service.MinUsdValue;
    public int KnownWalletCount => _service.LabeledWallets.Count;
    public IReadOnlyList<LabeledWallet> KnownWallets => _service.LabeledWallets;

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand  { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> AnalyzeWithAiCommand { get; }

    /// Set by MainWindowViewModel — called when user clicks Snipe on an alert.
    public Action<string>? RequestNavigateToSymbol { get; set; }

    // ── AI flow insight (#6) ────────────────────────────────────────────────────
    private readonly MarketInsightAiService _insight = new();
    private bool _insightRunning, _hasInsight;
    private string _insightSummary = "", _insightSignal = "", _insightBullets = "", _insightSource = "";

    public bool InsightRunning { get => _insightRunning; private set => this.RaiseAndSetIfChanged(ref _insightRunning, value); }
    public bool HasInsight { get => _hasInsight; private set => this.RaiseAndSetIfChanged(ref _hasInsight, value); }
    public string InsightSummary { get => _insightSummary; private set => this.RaiseAndSetIfChanged(ref _insightSummary, value); }
    public string InsightSignal { get => _insightSignal; private set => this.RaiseAndSetIfChanged(ref _insightSignal, value); }
    public string InsightBullets { get => _insightBullets; private set => this.RaiseAndSetIfChanged(ref _insightBullets, value); }
    public string InsightSource { get => _insightSource; private set => this.RaiseAndSetIfChanged(ref _insightSource, value); }
    public string InsightSignalBrush => _insightSignal switch
    {
        "ACCUMULATION" => "#3DDC84", "DISTRIBUTION" => "#FF6B6B", _ => "#8FA3B8"
    };

    public void ConfigureAi(string apiKey, string model)
    {
        _insight.ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(model)) _insight.Model = model;
    }

    private async System.Threading.Tasks.Task AnalyzeWithAiAsync()
    {
        if (InsightRunning) return;
        var alerts = RecentAlerts.Select(vm => vm.Alert).ToList();
        if (alerts.Count == 0) { InsightSummary = "No whale alerts yet."; HasInsight = true; return; }

        decimal inflow = 0m, outflow = 0m;
        var lines = new System.Collections.Generic.List<string>();
        foreach (var a in alerts.Take(40))
        {
            var toEx = string.Equals(a.ToLabel?.Category, "Exchange", StringComparison.OrdinalIgnoreCase);
            var fromEx = string.Equals(a.FromLabel?.Category, "Exchange", StringComparison.OrdinalIgnoreCase);
            if (toEx) inflow += a.Transfer.UsdValue;
            if (fromEx) outflow += a.Transfer.UsdValue;
            var dir = toEx ? "→exchange" : fromEx ? "exchange→" : "wallet→wallet";
            lines.Add($"{a.Transfer.TokenSymbol} ${a.Transfer.UsdValue:0} {dir}");
        }

        InsightRunning = true;
        try
        {
            var offline = () => InsightHeuristics.WhaleFlow(inflow, outflow, alerts.Count);
            var result = await _insight.InterpretAsync(
                "You are an on-chain whale-flow analyst. Coins moving TO exchanges hint at sell pressure; coins moving OFF hint at accumulation.",
                lines, ["ACCUMULATION", "DISTRIBUTION", "NEUTRAL"], offline).ConfigureAwait(true);
            ApplyInsight(result);
        }
        catch (Exception ex) { InsightSummary = $"AI failed: {ex.Message}"; HasInsight = true; }
        finally { InsightRunning = false; }
    }

    private void ApplyInsight(CryptoAITerminal.AIEngine.InsightResult r)
    {
        InsightSummary = r.Summary;
        InsightSignal = r.Signal;
        InsightBullets = r.Bullets.Length > 0 ? "• " + string.Join("\n• ", r.Bullets) : "";
        InsightSource = r.Source;
        HasInsight = true;
        this.RaisePropertyChanged(nameof(InsightSignalBrush));
    }

    public WhaleTrackerViewModel(WhaleTrackerService service, WhaleTokenEnricher? enricher = null)
    {
        _service  = service;
        _enricher = enricher;

        AnalyzeWithAiCommand = ReactiveCommand.CreateFromTask(AnalyzeWithAiAsync, outputScheduler: App.UiScheduler);

        StartCommand = ReactiveCommand.Create(StartTracking,
            this.WhenAnyValue(x => x.IsTracking).Select(t => !t),
            outputScheduler: App.UiScheduler);
        StopCommand = ReactiveCommand.Create(StopTracking,
            this.WhenAnyValue(x => x.IsTracking),
            outputScheduler: App.UiScheduler);
        ClearCommand = ReactiveCommand.Create(
            () => RecentAlerts.Clear(),
            outputScheduler: App.UiScheduler);
    }

    private void StartTracking()
    {
        if (IsTracking) return;

        _subscription = _service.AlertStream
            .Subscribe(alert => Dispatcher.UIThread.Post(() => OnAlertReceived(alert)));

        _service.Start();
        IsTracking = true;
    }

    private void StopTracking()
    {
        _service.Stop();
        _subscription?.Dispose();
        _subscription = null;
        IsTracking = false;
    }

    private void OnAlertReceived(WhaleAlert alert)
    {
        var vm = new WhaleAlertViewModel(alert,
            symbol => RequestNavigateToSymbol?.Invoke(symbol));
        RecentAlerts.Insert(0, vm);
        TotalAlertsCount++;
        if (alert.IsLabeledWalletActivity) LabeledAlertsCount++;

        // Async price-trend enrichment (fire-and-forget — updates VM when data arrives)
        if (_enricher is not null && !string.IsNullOrEmpty(alert.Transfer.ContractAddress))
            _ = EnrichAlertAsync(vm, alert);

        // Keep list bounded to avoid unbounded growth
        while (RecentAlerts.Count > 200)
            RecentAlerts.RemoveAt(RecentAlerts.Count - 1);

        // OS-уведомление (только первые 3 за минуту — не спамим)
        SendTrayNotification(alert, vm);
    }

    private async Task EnrichAlertAsync(WhaleAlertViewModel vm, WhaleAlert alert)
    {
        var pct = await _enricher!.GetPriceChange1hAsync(
            alert.Transfer.Chain, alert.Transfer.ContractAddress).ConfigureAwait(false);
        if (pct.HasValue)
            await Dispatcher.UIThread.InvokeAsync(() => vm.SetPriceTrend(pct.Value));
    }

    private int _notifCountInWindow;
    private DateTime _notifWindowStart = DateTime.MinValue;

    private void SendTrayNotification(WhaleAlert alert, WhaleAlertViewModel alertVm)
    {
        var tray = App.Tray;
        if (tray is null) return;

        // Сброс окна раз в минуту
        if ((DateTime.UtcNow - _notifWindowStart).TotalSeconds >= 60)
        {
            _notifCountInWindow = 0;
            _notifWindowStart = DateTime.UtcNow;
        }
        if (_notifCountInWindow >= 3) return;
        _notifCountInWindow++;

        var isWatched = alert.IsLabeledWalletActivity;
        var title = isWatched
            ? $"Whale Tracker — Watched: {alert.Transfer.TokenSymbol}"
            : $"Whale Tracker — Large Move: {alert.Transfer.TokenSymbol}";
        var body = isWatched
            ? $"{alert.ActiveLabel ?? "Known wallet"}  {alertVm.AmountLabel}"
            : $"{alertVm.AmountLabel}  {alertVm.Description}";

        if (isWatched)
            tray.ShowWarning(title, body);
        else
            tray.ShowInfo(title, body);
    }

    public void Dispose()
    {
        StopTracking();
        _service.Dispose();
    }
}

// ── Per-alert view model ──────────────────────────────────────────────────────

public class WhaleAlertViewModel : ReactiveObject
{
    private readonly WhaleAlert _alert;

    /// <summary>Underlying alert — used by the AI flow-insight aggregation.</summary>
    public WhaleAlert Alert => _alert;

    // ── Alchemy price-trend enrichment (populated asynchronously) ─────────────
    private string _priceTrendLabel = "";
    private string _priceTrendBrush = "#8FA3B8";
    private bool   _hasPriceTrend;

    public string PriceTrendLabel
    {
        get => _priceTrendLabel;
        private set => this.RaiseAndSetIfChanged(ref _priceTrendLabel, value);
    }

    public string PriceTrendBrush
    {
        get => _priceTrendBrush;
        private set => this.RaiseAndSetIfChanged(ref _priceTrendBrush, value);
    }

    public bool HasPriceTrend
    {
        get => _hasPriceTrend;
        private set => this.RaiseAndSetIfChanged(ref _hasPriceTrend, value);
    }

    /// <summary>Called by <see cref="WhaleTrackerViewModel.EnrichAlertAsync"/> on the UI thread.</summary>
    public void SetPriceTrend(decimal changePct)
    {
        var sign = changePct >= 0 ? "+" : "";
        PriceTrendLabel = $"{sign}{changePct:N2}%  1h";
        PriceTrendBrush = changePct >=  0.5m ? "#21E6C1"
                        : changePct <= -0.5m ? "#FF6B6B"
                        : "#F4D03F";
        HasPriceTrend = true;
    }

    public string ChainBadge => _alert.Transfer.Chain switch
    {
        ChainType.Ethereum => "ETH",
        ChainType.BSC      => "BSC",
        ChainType.Solana   => "SOL",
        _                  => "?"
    };

    public string ChainBrush => _alert.Transfer.Chain switch
    {
        ChainType.Ethereum => "#8A92B2",
        ChainType.BSC      => "#F0B90B",
        ChainType.Solana   => "#9945FF",
        _                  => "#8FA3B8"
    };

    public string AlertBadge => _alert.AlertType == "WalletActivity" ? "WATCHED" : "LARGE";
    public string AlertBrush => _alert.AlertType == "WalletActivity" ? "#F4B860" : "#21E6C1";
    public bool   HasLabel   => _alert.IsLabeledWalletActivity;

    public string TokenSymbol => _alert.Transfer.TokenSymbol;

    public string AmountLabel
    {
        get
        {
            var usd = _alert.Transfer.UsdValue;
            if (usd >= 1_000_000_000m) return $"${usd / 1_000_000_000m:N1}B";
            if (usd >= 1_000_000m)     return $"${usd / 1_000_000m:N1}M";
            if (usd > 0)               return $"${usd / 1_000m:N0}K";
            return $"{_alert.Transfer.Amount:N0} {_alert.Transfer.TokenSymbol}";
        }
    }

    public string FromLabel => _alert.FromLabel?.Label ?? FormatAddress(_alert.Transfer.FromAddress);
    public string ToLabel   => _alert.ToLabel?.Label   ?? FormatAddress(_alert.Transfer.ToAddress);
    public string Description => $"{FromLabel}  →  {ToLabel}";
    public string CategoryLabel => _alert.ActiveCategory;
    public string ActiveWalletLabel => _alert.ActiveLabel;

    public string TimeLabel
    {
        get
        {
            var diff = DateTime.UtcNow - _alert.Transfer.Timestamp;
            if (diff.TotalMinutes < 1) return "just now";
            if (diff.TotalHours   < 1) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalDays    < 1) return $"{(int)diff.TotalHours}h ago";
            return $"{(int)diff.TotalDays}d ago";
        }
    }

    public string TxHashShort => _alert.Transfer.TxHash.Length >= 10
        ? _alert.Transfer.TxHash[..8] + "…"
        : _alert.Transfer.TxHash;

    public bool CanSnipe => !string.IsNullOrEmpty(GetSniperSymbol());

    public ReactiveCommand<Unit, Unit> SniperBuyCommand { get; }

    public WhaleAlertViewModel(WhaleAlert alert, Action<string>? sniperCallback)
    {
        _alert = alert;
        SniperBuyCommand = ReactiveCommand.Create(() =>
        {
            var symbol = GetSniperSymbol();
            if (!string.IsNullOrEmpty(symbol))
                sniperCallback?.Invoke(symbol);
        });
    }

    private string GetSniperSymbol()
    {
        var t = _alert.Transfer.TokenSymbol.ToUpperInvariant();
        return t switch
        {
            "USDT" or "USDC" or "DAI" or "BUSD" => string.Empty, // skip stablecoins
            "SOL"  => "SOLUSDT",
            "BNB"  => "BNBUSDT",
            "ETH"  => "ETHUSDT",
            "BTC"  or "WBTC" => "BTCUSDT",
            "AVAX" => "AVAXUSDT",
            "MATIC" or "POL" => "MATICUSDT",
            "LINK" => "LINKUSDT",
            "UNI"  => "UNIUSDT",
            "AAVE" => "AAVEUSDT",
            _      => t.EndsWith("USDT") || t.EndsWith("USDC") ? t : t + "USDT"
        };
    }

    private static string FormatAddress(string addr) =>
        addr.Length >= 10 ? addr[..6] + "…" + addr[^4..] : addr;
}

// ── Known-wallet row view model ───────────────────────────────────────────────

public class LabeledWalletRowViewModel
{
    private readonly LabeledWallet _wallet;

    public string Label    => _wallet.Label;
    public string Category => _wallet.Category;
    public string Chain    => _wallet.Chain.ToString();
    public string AddressShort => _wallet.Address.Length >= 10
        ? _wallet.Address[..6] + "…" + _wallet.Address[^4..]
        : _wallet.Address;

    public string CategoryBrush => _wallet.Category switch
    {
        "Exchange"    => "#21E6C1",
        "MarketMaker" => "#F4B860",
        "Fund"        => "#A78BFA",
        _             => "#8FA3B8"
    };

    public LabeledWalletRowViewModel(LabeledWallet wallet) => _wallet = wallet;
}
