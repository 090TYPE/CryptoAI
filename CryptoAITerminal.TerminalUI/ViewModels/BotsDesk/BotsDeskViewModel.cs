using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

/// <summary>Small {Value,Label} option for the status / venue selects.</summary>
public sealed class SelectOption
{
    public string Value { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>
/// The Bots desk. It owns no strategy state: the app has no bot registry, so the
/// desk synthesises one row per real engine singleton (grid, DCA, rule, AI trader,
/// autonomous agent, trailing stop) and mirrors their live state. Until
/// <see cref="Attach"/> is called the desk is empty.
/// </summary>
public partial class BotsDeskViewModel : ReactiveObject
{
    private readonly DispatcherTimer _toastTimer;

    public BotsDeskViewModel()
    {
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };

        StatusOptions = new[]
        {
            new SelectOption { Value = "all", Label = "status: all" },
            new SelectOption { Value = "running", Label = "running" },
            new SelectOption { Value = "paused", Label = "paused" },
            new SelectOption { Value = "stopped", Label = "stopped" },
        };
        _statusOption = StatusOptions[0];

        RebuildVenueOptions();

        // commands ---------------------------------------------------------
        RefreshCommand = new RelayCommand(ResyncNow);
        TogglePaperOnlyCommand = new RelayCommand(TogglePaperOnly);
        KillAllCommand = new RelayCommand(OpenKillAll);
        NewBotCommand = new RelayCommand(OpenWizard);
        OpenDailyCapCommand = new RelayCommand(OpenDailyCap);
        BriefGenerateCommand = new RelayCommand(BriefGenerate);
        BriefToggleAutoCommand = new RelayCommand(BriefToggleAuto);
        ToggleDensityCommand = new RelayCommand(() => { Dense = !Dense; });
        OpenColumnsCommand = new RelayCommand(OpenColumns);
        ClearSelectionCommand = new RelayCommand(() => { Selected.Clear(); AfterSelectionChanged(); });
        ToggleSelectAllCommand = new RelayCommand(ToggleSelectAll);
        SortPnl24Command = new RelayCommand(() => Sort("pnl24"));
        SortPnlTotalCommand = new RelayCommand(() => Sort("pnlTotal"));
        SortAllocCommand = new RelayCommand(() => Sort("alloc"));
        CloseModalCommand = new RelayCommand(CloseModal);
        ResetLayoutCommand = new RelayCommand(ResetLayout);

        InitDetail();
        InitRail();
        InitModals();
        Recompute();
        RebuildVisible();
        RebuildDetail();
    }

    // ── observable collections ──────────────────────────────────────────────
    public ObservableCollection<BotRowViewModel> Bots { get; } = new();
    public ObservableCollection<BotRowViewModel> VisibleBots { get; } = new();
    public ObservableCollection<TickerItem> Ticker { get; } = new();
    public ObservableCollection<string> Bullets { get; } = new();
    public ObservableCollection<TypeChip> TypeChips { get; } = new();
    public ObservableCollection<BulkAction> BulkActions { get; } = new();
    public ObservableCollection<TemplateCard> Templates { get; } = new();

    public SelectOption[] StatusOptions { get; }
    public ObservableCollection<SelectOption> VenueOptions { get; } = new();

    internal readonly HashSet<string> Selected = new();

    // ── layout config ───────────────────────────────────────────────────────
    private readonly Dictionary<string, bool> _cols = new()
    {
        ["venue"] = true, ["mode"] = true, ["status"] = true, ["pnl24"] = true, ["pnlTotal"] = true,
        ["spark"] = true, ["trades"] = true, ["alloc"] = true, ["dd"] = true, ["uptime"] = true
    };
    private List<string> _quick = new() { "start", "pause", "stop", "logs" };

    public bool ColVenue => _cols["venue"];
    public bool ColMode => _cols["mode"];
    public bool ColStatus => _cols["status"];
    public bool ColPnl24 => _cols["pnl24"];
    public bool ColPnlTotal => _cols["pnlTotal"];
    public bool ColSpark => _cols["spark"];
    public bool ColTrades => _cols["trades"];
    public bool ColAlloc => _cols["alloc"];
    public bool ColDd => _cols["dd"];
    public bool ColUptime => _cols["uptime"];

    // ── scalar UI state ─────────────────────────────────────────────────────
    /// <summary>Mirrors the wallet's global paper guard — the app's real live/paper switch.</summary>
    public bool PaperOnly
    {
        get => Wallet?.GlobalPaperOnlyMode ?? true;
        private set
        {
            if (Wallet is null) return;
            Wallet.GlobalPaperOnlyMode = value;
            this.RaisePropertyChanged();
        }
    }

    private string _search = "";
    public string Search { get => _search; set { this.RaiseAndSetIfChanged(ref _search, value ?? ""); RebuildVisible(); } }

    private string _typeFilter = "all";
    public string TypeFilter { get => _typeFilter; private set { this.RaiseAndSetIfChanged(ref _typeFilter, value); } }

    private SelectOption _statusOption;
    public SelectOption StatusOption
    {
        get => _statusOption;
        set { this.RaiseAndSetIfChanged(ref _statusOption, value ?? StatusOptions[0]); RebuildVisible(); }
    }

    private SelectOption? _venueOption;
    public SelectOption? VenueOption
    {
        get => _venueOption;
        set { this.RaiseAndSetIfChanged(ref _venueOption, value); RebuildVisible(); }
    }

    private string _sortKey = "pnl24";
    private int _sortDir = -1;

    private bool _dense;
    public bool Dense { get => _dense; set { this.RaiseAndSetIfChanged(ref _dense, value); this.RaisePropertyChanged(nameof(RowHeight)); this.RaisePropertyChanged(nameof(DensityLabel)); } }
    public double RowHeight => _dense ? 38 : 50;
    public string DensityLabel => _dense ? "↕ COMPACT" : "↕ COMFORTABLE";

    private string? _expandedId;
    private string _detailTab = "overview";
    private string? _menuForId;

    /// <summary>Model badge for the AI rail — the key/model shared from the AI Bot tab.</summary>
    public string AiProvider => _host?.AIBotVM is { } b && !string.IsNullOrWhiteSpace(b.ClaudeApiKey)
        ? b.ClaudeModel.ToUpperInvariant()
        : "OFFLINE";

    // ── commands ────────────────────────────────────────────────────────────
    public ICommand RefreshCommand { get; }
    public ICommand TogglePaperOnlyCommand { get; }
    public ICommand KillAllCommand { get; }
    public ICommand NewBotCommand { get; }
    public ICommand OpenDailyCapCommand { get; }
    public ICommand BriefGenerateCommand { get; }
    public ICommand BriefToggleAutoCommand { get; }
    public ICommand ToggleDensityCommand { get; }
    public ICommand OpenColumnsCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ToggleSelectAllCommand { get; }
    public ICommand SortPnl24Command { get; }
    public ICommand SortPnlTotalCommand { get; }
    public ICommand SortAllocCommand { get; }
    public ICommand CloseModalCommand { get; }
    public ICommand ResetLayoutCommand { get; }

    // ── formatting delegates (used by row/detail VMs) ───────────────────────
    public string Money(double v, bool sign = false) => BotsDeskData.Money(v, sign);
    public string Pct(double v) => BotsDeskData.Pct(v);
    public string Sgn(double v) => BotsDeskData.Sgn(v);
    public string Alpha(string hex, string aa) => BotsDeskData.Alpha(hex, aa);
    private static string Now() => DateTime.Now.ToString("HH:mm:ss");

    // ═════════════════════════ header ═══════════════════════════════════════
    private List<BotRowViewModel> Running => Bots.Where(b => b.Status == "running").ToList();

    /// <summary>Budget configured on the engines that are actually running.</summary>
    private double DeployedBudget => Bots.Where(b => b.Status != "stopped").Sum(b => b.BudgetUsd ?? 0);
    private double ConfiguredBudget => Bots.Sum(b => b.BudgetUsd ?? 0);

    private double DailyLossCap => Wallet is { } w ? (double)w.GlobalMaxDailyLossUsdt : 0;
    private int CapUsedPct => DailyLossCap <= 0 ? 0 : (int)Math.Min(100, Math.Round(LossToday / DailyLossCap * 100));

    public string EngineBadgeLabel => Bots.Count == 0 ? BotsDeskData.Dash : Running.Count + "/" + Bots.Count;
    public string EngineBadgeColor => Running.Count > 0 ? BotsDeskData.Green : BotsDeskData.Dimmer;
    public string EngineBadgeBorder => Running.Count > 0 ? "#14302e" : SemanticColor.Stroke;
    public string EngineBadgeBg => Running.Count > 0 ? "#061615" : "transparent";

    public string HdrPnl => Sum24 is { } v ? Money(v, true) : BotsDeskData.Dash;
    public string HdrPnlColor => Sum24 is { } v ? Sgn(v) : BotsDeskData.Text3;
    public string HdrPnlSub => Running.Count + " engines live";
    public string HdrAlloc => ConfiguredBudget > 0 ? Money(DeployedBudget) : BotsDeskData.Dash;
    public string HdrAllocSub => ConfiguredBudget > 0 ? "of " + Money(ConfiguredBudget) + " configured" : "";
    public string HdrCapLabel => DailyLossCap <= 0 ? BotsDeskData.Dash : CapUsedPct + "% of −$" + DailyLossCap.ToString("#,##0");
    public double HdrCapRatio => CapUsedPct / 100.0;
    public string HdrCapColor => CapUsedPct > 70 ? BotsDeskData.Red : CapUsedPct > 40 ? BotsDeskData.Amber : BotsDeskData.Accent;

    public string PaperLabel => PaperOnly ? "PAPER-ONLY" : "LIVE ARMED";
    public string PaperColor => PaperOnly ? BotsDeskData.Accent : BotsDeskData.Amber;
    public string PaperBorder => PaperOnly ? "#1a3030" : "#3a2a12";
    public string PaperBg => PaperOnly ? "#061615" : "#150f04";

    // ═════════════════════════ footer ══════════════════════════════════════
    public string FooterEngine => !IsAttached ? "detached" : Running.Count > 0 ? "running" : "idle";
    public string FooterEngineColor => !IsAttached ? BotsDeskData.Dimmer : Running.Count > 0 ? BotsDeskData.Green : BotsDeskData.Dimmer;
    public string FooterMode => PaperOnly ? "paper-only ON (wallet guard)" : "live armed · wallet caps active";
    /// <summary>CEX gateways holding usable API credentials, out of the four the shell wires up.</summary>
    public string FooterGateways => GatewayHealthService.Instance.GatewaysLabel;
    /// <summary>Age of the newest market-data tick the shell holds.</summary>
    public string FooterWs => IsAttached ? FeedAgeLabel() : BotsDeskData.Dash;
    /// <summary>Broadcast orders per minute, or the measurable fill rate when nothing counts orders.</summary>
    public string FooterOpm => IsAttached ? OrderRateLabel() : BotsDeskData.Dash;
    private string _footerSync = "";
    public string FooterSync => _footerSync;

    private void RaiseLiveHeader()
    {
        foreach (var n in new[]
        {
            nameof(EngineBadgeLabel), nameof(EngineBadgeColor), nameof(EngineBadgeBorder), nameof(EngineBadgeBg),
            nameof(HdrPnl), nameof(HdrPnlColor), nameof(HdrPnlSub), nameof(HdrAlloc), nameof(HdrAllocSub),
            nameof(HdrCapLabel), nameof(HdrCapRatio), nameof(HdrCapColor),
            nameof(PaperOnly), nameof(PaperLabel), nameof(PaperColor), nameof(PaperBorder), nameof(PaperBg),
            nameof(FooterEngine), nameof(FooterEngineColor), nameof(FooterMode),
            nameof(FooterGateways), nameof(FooterWs), nameof(FooterOpm),
            nameof(BriefSignal), nameof(BriefSignalColor), nameof(BriefSignalBg), nameof(BriefSignalBorder),
            nameof(BriefMeta), nameof(BriefSummary), nameof(BriefBtnLabel), nameof(BriefBtnColor),
            nameof(BriefAutoLabel), nameof(BriefTrackBg), nameof(BriefKnob), nameof(BriefKnobLeft),
            nameof(AiProvider)
        })
            this.RaisePropertyChanged(n);
    }

    // ═════════════════════════ briefing (DailyBriefingViewModel) ════════════
    private DailyBriefingViewModel? Brief => _host?.BriefingVM;
    private bool BriefHasResult => Brief is { } b && !string.IsNullOrWhiteSpace(b.GeneratedAt);

    public string BriefSignal => Brief is { HasSignal: true } b ? b.SignalLabel : BotsDeskData.Dash;
    public string BriefSignalColor => Brief is { HasSignal: true } b ? b.SignalBrush : BotsDeskData.Dimmer;
    public string BriefSignalBg => Brief?.Signal switch
    {
        "RISK_ON" => "#061615",
        "RISK_OFF" => "#14060a",
        "NEUTRAL" => "#150f04",
        _ => "transparent"
    };
    public string BriefSignalBorder => Brief?.Signal switch
    {
        "RISK_ON" => "#14302e",
        "RISK_OFF" => "#3a1620",
        "NEUTRAL" => "#3a2a12",
        _ => SemanticColor.Stroke
    };
    public string BriefMeta => Brief is null ? ""
        : Brief.Running ? "generating…"
        : BriefHasResult ? "generated " + Brief.GeneratedAt + " · " + Brief.Source
        : "not generated yet";
    public string BriefSummary => BriefHasResult ? Brief!.Summary : "";
    public string BriefBtnLabel => Brief is { Running: true } ? "ANALYSING…" : "↻ GENERATE";
    public string BriefBtnColor => Brief is { Running: true } ? BotsDeskData.Accent : "#5a7a94";
    public string BriefAutoLabel => Brief is null ? "auto off"
        : Brief.AutoEnabled ? "auto " + Brief.AutoHour.ToString("00") + ":00" : "auto off";
    public string BriefTrackBg => Brief is { AutoEnabled: true } ? "#14302e" : SemanticColor.Stroke;
    public string BriefKnob => Brief is { AutoEnabled: true } ? BotsDeskData.Accent : "#3d5a72";
    public double BriefKnobLeft => Brief is { AutoEnabled: true } ? 14 : 2;

    private void BriefGenerate()
    {
        if (Brief is not { } brief) { Toast("Briefing service is not available", "warn"); return; }
        if (brief.Running) return;
        Run(brief.RefreshCommand);
        RaiseLiveHeader();
        Toast("Briefing requested from the book, news and sentiment panels", "ai");
    }

    private void BriefToggleAuto()
    {
        if (Brief is not { } brief) { Toast("Briefing service is not available", "warn"); return; }
        brief.AutoEnabled = !brief.AutoEnabled;
        RaiseLiveHeader();
        Toast(brief.AutoEnabled ? "Morning briefing at " + brief.AutoHour.ToString("00") + ":00" : "Morning briefing off", "info");
    }

    private void RebuildBullets()
    {
        Bullets.Clear();
        if (Brief is null) return;
        foreach (var b in Brief.Bullets) Bullets.Add(b);
    }

    // NOTE: bullet dot colours are fixed; exposed as a parallel list for binding.
    public string[] BulletDots => new[] { BotsDeskData.Accent, BotsDeskData.Accent, BotsDeskData.Accent };

    // ═════════════════════════ ticker (real closed trades) ══════════════════
    private void RebuildTicker()
    {
        Ticker.Clear();
        if (_host is null) return;
        foreach (var t in _records.OrderByDescending(r => r.ClosedAtUtc).Take(6))
        {
            var pnl = (double)t.PnlUsd;
            Ticker.Add(new TickerItem
            {
                Tag = (t.BotName ?? t.SourceLabel).ToUpperInvariant(),
                Text = t.Symbol + " " + t.Direction.ToString().ToLowerInvariant() + " · " + t.ClosedAtUtc.ToLocalTime().ToString("HH:mm"),
                Val = Money(pnl, true),
                Color = Sgn(pnl)
            });
        }
    }

    // ═════════════════════════ toolbar: filters ════════════════════════════
    private void RebuildVenueOptions()
    {
        var keep = _venueOption?.Value ?? "all";
        VenueOptions.Clear();
        VenueOptions.Add(new SelectOption { Value = "all", Label = "venue: all" });
        foreach (var v in Bots.Select(b => b.Venue).Where(v => !string.IsNullOrEmpty(v)).Distinct())
            VenueOptions.Add(new SelectOption { Value = v, Label = v });
        _venueOption = VenueOptions.FirstOrDefault(o => o.Value == keep) ?? VenueOptions[0];
        this.RaisePropertyChanged(nameof(VenueOption));
    }

    private void RebuildTypeChips()
    {
        TypeChips.Clear();
        void Add(string id, string label)
        {
            var active = _typeFilter == id;
            var count = id == "all" ? Bots.Count : Bots.Count(b => b.Type == id);
            TypeChips.Add(new TypeChip
            {
                Label = label,
                Count = count.ToString(),
                Bg = active ? "#0e2a2a" : "transparent",
                Fg = active ? BotsDeskData.Accent : "#3d5a72",
                CountColor = active ? BotsDeskData.Accent : "#1e3048",
                Command = new RelayCommand(() => { TypeFilter = id; RebuildTypeChips(); RebuildVisible(); })
            });
        }
        Add("all", "ALL");
        foreach (var t in BotsDeskData.Types) Add(t, t);
    }

    // ═════════════════════════ filtering / sorting ═════════════════════════
    private IEnumerable<BotRowViewModel> Filtered()
    {
        var q = (_search ?? "").Trim().ToLowerInvariant();
        var status = _statusOption?.Value ?? "all";
        var venue = _venueOption?.Value ?? "all";
        var list = Bots.Where(b =>
        {
            if (_typeFilter != "all" && b.Type != _typeFilter) return false;
            if (status != "all" && b.Status != status) return false;
            if (venue != "all" && b.Venue != venue) return false;
            if (q.Length > 0 && !($"{b.Name} {b.Venue} {b.Market} {b.Type}").ToLowerInvariant().Contains(q)) return false;
            return true;
        });
        // rows without a value sort last in both directions
        double Key(BotRowViewModel b) => _sortKey switch
        {
            "pnl24" => b.Pnl24Usd ?? double.NegativeInfinity,
            "pnlTotal" => b.PnlTotalUsd ?? double.NegativeInfinity,
            _ => b.BudgetUsd ?? double.NegativeInfinity
        };
        return list.OrderBy(b => Key(b) * _sortDir);
    }

    private void RebuildVisible()
    {
        var next = Filtered().ToList();
        VisibleBots.Clear();
        foreach (var b in next) { BuildRowActionsAndMenu(b); VisibleBots.Add(b); }
        this.RaisePropertyChanged(nameof(ShowEmpty));
        this.RaisePropertyChanged(nameof(EmptyTitle));
        this.RaisePropertyChanged(nameof(EmptySub));
        RefreshSelectionHeader();
    }

    private void Sort(string key)
    {
        if (_sortKey == key) _sortDir = -_sortDir; else { _sortKey = key; _sortDir = -1; }
        RebuildVisible();
        this.RaisePropertyChanged(nameof(SortArrowPnl24));
        this.RaisePropertyChanged(nameof(SortArrowPnlTotal));
        this.RaisePropertyChanged(nameof(SortArrowAlloc));
        this.RaisePropertyChanged(nameof(SortColorPnl24));
        this.RaisePropertyChanged(nameof(SortColorPnlTotal));
        this.RaisePropertyChanged(nameof(SortColorAlloc));
    }

    private string Arrow(string key) => _sortKey == key ? (_sortDir < 0 ? " ↓" : " ↑") : "";
    public string SortArrowPnl24 => "PnL 24H" + Arrow("pnl24");
    public string SortArrowPnlTotal => "PnL TOTAL" + Arrow("pnlTotal");
    public string SortArrowAlloc => "BUDGET · USED" + Arrow("alloc");
    public string SortColorPnl24 => _sortKey == "pnl24" ? BotsDeskData.Accent : BotsDeskData.Faint;
    public string SortColorPnlTotal => _sortKey == "pnlTotal" ? BotsDeskData.Accent : BotsDeskData.Faint;
    public string SortColorAlloc => _sortKey == "alloc" ? BotsDeskData.Accent : BotsDeskData.Faint;

    // ═════════════════════════ empty state / engine cards ══════════════════
    public bool ShowEmpty => VisibleBots.Count == 0;
    public string EmptyTitle => !IsAttached
        ? "Bots desk is not connected to the trading shell"
        : Bots.Count == 0 ? "No engines available" : "Nothing matches these filters";
    public string EmptySub => !IsAttached
        ? "The desk shows the app's own engines — it has nothing to display until the shell attaches."
        : "Pick an engine below to configure and start it, or clear the filters above.";

    private void RebuildTemplates()
    {
        Templates.Clear();
        foreach (var t in BotsDeskData.Templates)
        {
            var (c, _) = BotsDeskData.TypeMeta(t.type);
            var picked = _wizTemplate == t.id;
            var tt = t;
            Templates.Add(new TemplateCard
            {
                Type = t.type, Name = t.name, Desc = t.desc,
                // no backtest archive / APR statistics exist per engine
                Risk = "", Backtest = "", Apr = "",
                Accent = c, BadgeBg = Alpha(c, "14"), BadgeBorder = Alpha(c, "33"),
                PickBorder = picked ? c : "#0d1b27", PickBg = picked ? "#08131d" : "#060d14",
                QuickCreate = new RelayCommand(() => OpenWizardFor(tt.id)),
                Pick = new RelayCommand(() => { _wizTemplate = tt.id; _wizName = tt.name; _wizStep = 2; RebuildTemplates(); RebuildWizard(); Recompute(); })
            });
        }
    }

    // ═════════════════════════ toast ═══════════════════════════════════════
    private bool _hasToast;
    public bool HasToast { get => _hasToast; private set => this.RaiseAndSetIfChanged(ref _hasToast, value); }
    public string ToastMsg { get; private set; } = "";
    public string ToastColor { get; private set; } = BotsDeskData.Accent;
    public string ToastIcon { get; private set; } = "";
    public string ToastBorder { get; private set; } = "#0d1b27";
    public string ToastMeta { get; private set; } = "";

    public void Toast(string msg, string kind = "ok")
    {
        (string color, string icon) = kind switch
        {
            "ok" => (BotsDeskData.Green, "✓"),
            "warn" => (BotsDeskData.Amber, "!"),
            "bad" => (BotsDeskData.Red, "✕"),
            "ai" => (BotsDeskData.Accent, "✦"),
            _ => (BotsDeskData.Accent, "›"),
        };
        ToastMsg = msg; ToastColor = color; ToastIcon = icon; ToastBorder = Alpha(color, "55"); ToastMeta = Now();
        this.RaisePropertyChanged(nameof(ToastMsg));
        this.RaisePropertyChanged(nameof(ToastColor));
        this.RaisePropertyChanged(nameof(ToastIcon));
        this.RaisePropertyChanged(nameof(ToastBorder));
        this.RaisePropertyChanged(nameof(ToastMeta));
        HasToast = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast() => HasToast = false;

    // ═════════════════════════ paper-only toggle ═══════════════════════════
    private void TogglePaperOnly()
    {
        if (Wallet is null) { Toast("Wallet guard is not available", "warn"); return; }

        if (PaperOnly)
        {
            AskConfirm(new ConfirmConfig
            {
                Kind = "arm", Accent = "#3a2a12", IconColor = BotsDeskData.Amber, Icon = "⚠",
                Title = "Arm live trading",
                Body = "Turning the wallet's global paper guard off lets every engine send real orders. " +
                       "The wallet risk caps and the kill-switch stay active.",
                Checks = { new ConfirmCheck { Label = "I understand orders will hit the exchange with real funds." } },
                HasType = true, TypeWord = "ARM", Cta = "ARM LIVE", BtnBg = "#150f04", BtnFg = BotsDeskData.Amber, BtnBorder = BotsDeskData.Amber
            });
        }
        else
        {
            PaperOnly = true;
            RefreshAllRows();
            Recompute();
            Toast("Paper-only ON — the wallet guard blocks every broadcast", "info");
        }
    }

    // ═════════════════════════ recompute orchestration ═════════════════════
    /// <summary>Re-emit all top-level computed values.</summary>
    public void Recompute()
    {
        RebuildTicker();
        RebuildBullets();
        RebuildTypeChips();
        RebuildBulkActions();
        RebuildTemplates();
        _footerSync = IsAttached ? "last sync " + Now() : "not attached";
        this.RaisePropertyChanged(string.Empty);
    }

    private void RefreshAllRows()
    {
        foreach (var b in Bots) b.RefreshAll();
    }
}
