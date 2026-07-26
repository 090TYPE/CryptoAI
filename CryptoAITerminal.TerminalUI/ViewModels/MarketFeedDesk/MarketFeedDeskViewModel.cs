using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.MarketFeedDesk;

/// <summary>
/// The "Market feed" desk hosted in the News section: NEWS / LIVE TAPE / LIQUIDATIONS.
/// It owns no data of its own — <see cref="Attach"/> binds it to the shell's live view
/// models (RSS news feed, Binance/GeckoTerminal trade tape, liquidation heatmap) and the
/// desk re-projects whatever they currently hold. Before <see cref="Attach"/>, and for any
/// source that has nothing yet, the desk shows an empty list and says so.
/// </summary>
public sealed class MarketFeedDeskViewModel : ReactiveObject, IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Plot width the heatmap VM lays its band widths out in (1200 canvas − 24 pad).</summary>
    private const double LiqPlotWidth = 1176.0;
    private const int MaxLiqBands = 16;
    private const int MaxLiqAxisLabels = 9;

    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _coalesce;   // batches live-feed bursts into one rebuild

    // ── live sources (null until Attach) ─────────────────────────────────────
    private MainWindowViewModel? _host;
    private NewsFeedViewModel? _newsVm;
    private MarketTapeViewModel? _tapeVm;
    private LiquidationHeatmapViewModel? _liqVm;
    private SentimentViewModel? _sentimentVm;

    private readonly HashSet<long> _read = new();

    public MarketFeedDeskViewModel()
    {
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HasToast = false; };

        _coalesce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _coalesce.Tick += (_, _) => { _coalesce.Stop(); Recompute(); };

        OpenPulseCommand = new RelayCommand(OpenPulse);
        ToggleStreamCommand = new RelayCommand(ToggleStream);
        RefreshCommand = new RelayCommand(RefreshSource);
        AiCommand = new RelayCommand(RunAi);
        RegenCommand = new RelayCommand(RunAi);
        CreateAlertCommand = new RelayCommand(() => Toast("Alerts are configured in Settings → Alerts", "info"));
        ToggleImportantCommand = new RelayCommand(ToggleImportant);
        MarkReadCommand = new RelayCommand(MarkAllRead);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ApplyCommand = new RelayCommand(ApplyTape);
        DismissCommand = new RelayCommand(() => { _liqAlertDismissed = true; Recompute(); });
        AnalyzeCommand = new RelayCommand(RunAi);
        TradeCommand = new RelayCommand(() => Toast("Sending to the trading desk is not wired up yet", "warn"));
        CloseModalCommand = new RelayCommand(CloseModal);
        AlertCreateCommand = new RelayCommand(() => Toast("Alerts are configured in Settings → Alerts", "info"));

        _side2NewsAction = new RelayCommand(() => { Fire(_newsVm?.RefreshDigestCommand); Toast("Digest refresh requested", "ai"); });
        _side2TapeAction = new RelayCommand(() => { Fire(_tapeVm?.RefreshCommand); Toast("Tape re-subscribed", "ok"); });
        _side2LiqAction = new RelayCommand(() => { Fire(_liqVm?.RefreshCommand); Toast("Heatmap reload requested", "info"); });

        Recompute();
    }

    // ═════════════════════════ attach ════════════════════════════════════════

    /// <summary>Binds the desk to the shell's live view models and subscribes for updates.</summary>
    public void Attach(MainWindowViewModel host)
    {
        if (host is null || ReferenceEquals(_host, host)) return;
        Detach();

        _host = host;
        _newsVm = host.NewsFeedVM;
        _tapeVm = host.MarketTapeVM;
        _liqVm = host.LiquidationHeatmapVM;
        _sentimentVm = host.SentimentVM;

        host.PropertyChanged += OnHostChanged;

        if (_newsVm is not null)
        {
            _newsVm.Rows.CollectionChanged += OnSourceCollectionChanged;
            _newsVm.PropertyChanged += OnSourceChanged;
        }
        if (_tapeVm is not null)
        {
            _tapeVm.Rows.CollectionChanged += OnSourceCollectionChanged;
            _tapeVm.PropertyChanged += OnSourceChanged;
            _tapeThreshold = decimal.Truncate(_tapeVm.LargePrintThreshold).ToString("0", Inv);
        }
        if (_liqVm is not null) _liqVm.PropertyChanged += OnSourceChanged;
        if (_sentimentVm is not null) _sentimentVm.PropertyChanged += OnSourceChanged;

        SyncLiveGates();
        Recompute();
    }

    private void Detach()
    {
        if (_host is not null) _host.PropertyChanged -= OnHostChanged;
        if (_newsVm is not null)
        {
            _newsVm.Rows.CollectionChanged -= OnSourceCollectionChanged;
            _newsVm.PropertyChanged -= OnSourceChanged;
        }
        if (_tapeVm is not null)
        {
            _tapeVm.Rows.CollectionChanged -= OnSourceCollectionChanged;
            _tapeVm.PropertyChanged -= OnSourceChanged;
        }
        if (_liqVm is not null) _liqVm.PropertyChanged -= OnSourceChanged;
        if (_sentimentVm is not null) _sentimentVm.PropertyChanged -= OnSourceChanged;
        _host = null; _newsVm = null; _tapeVm = null; _liqVm = null; _sentimentVm = null;
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => ScheduleRebuild();
    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRebuild();

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsNewsSectionVisible)) return;
        // The shell stops the tape / heatmap timers later in the same navigation call,
        // so re-apply our own gating once that has finished.
        Dispatcher.UIThread.Post(() => { SyncLiveGates(); ScheduleRebuild(); }, DispatcherPriority.Background);
    }

    private void ScheduleRebuild()
    {
        // Off screen the desk still received every tape print (one CollectionChanged each) and
        // reprojected itself five times a second. Navigating back raises IsNewsSectionVisible,
        // and OnHostChanged schedules the rebuild that was skipped here.
        if (_host is not null && !_host.IsNewsSectionVisible) return;
        if (_coalesce.IsEnabled) return;
        _coalesce.Start();
    }

    /// <summary>
    /// Runs the tape poll / heatmap timers only while their view is on screen. Outside the
    /// News section the shell owns those lifecycles (it starts/stops them on every
    /// navigation), so we deliberately leave them alone there.
    /// </summary>
    private void SyncLiveGates()
    {
        if (_host?.IsNewsSectionVisible != true) return;

        if (IsTape && _streaming) _tapeVm?.Start(); else _tapeVm?.Stop();
        if (IsLiq) _liqVm?.Activate(); else _liqVm?.Deactivate();
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss");
    private static void Fire(ICommand? c, object? p = null) { try { c?.Execute(p); } catch { /* surfaced by the source VM */ } }

    // ── state ────────────────────────────────────────────────────────────────
    private string _view = "news";
    private bool _streaming = true;
    private string _newsSort = "time";
    private bool _liqAlertDismissed;
    private string _tapeThreshold = "50000";

    private bool IsNews => _view == "news";
    private bool IsTape => _view == "tape";
    private bool IsLiq => _view == "liq";
    public bool ViewNews => IsNews;
    public bool ViewTape => IsTape;
    public bool ViewLiq => IsLiq;

    private bool Live => _host is not null;

    // ── two-way inputs (mirrors of the live VM filter state) ─────────────────
    public string NewsSearch
    {
        get => _newsVm?.SearchText ?? "";
        set { if (_newsVm is not null) _newsVm.SearchText = value ?? ""; this.RaisePropertyChanged(); Recompute(); }
    }
    public string NewsSymbol
    {
        get => _newsVm?.FilterSymbol ?? "";
        set { if (_newsVm is not null) _newsVm.FilterSymbol = (value ?? "").ToUpperInvariant(); this.RaisePropertyChanged(); Recompute(); }
    }
    public string TapeSymbol
    {
        get => _tapeVm?.Symbol ?? "";
        set { if (_tapeVm is not null) _tapeVm.Symbol = (value ?? "").ToUpperInvariant(); this.RaisePropertyChanged(); Recompute(); }
    }
    public string TapeThreshold
    {
        get => _tapeThreshold;
        set
        {
            this.RaiseAndSetIfChanged(ref _tapeThreshold, BotsDeskData.Digits(value));
            if (_tapeVm is not null && decimal.TryParse(_tapeThreshold, NumberStyles.Any, Inv, out var v))
                _tapeVm.LargePrintThreshold = v;
            Recompute();
        }
    }
    public string TapePool
    {
        get => _tapeVm?.PoolAddress ?? "";
        set { if (_tapeVm is not null) _tapeVm.PoolAddress = value ?? ""; this.RaisePropertyChanged(); }
    }
    public string TapeNetwork
    {
        get => _tapeVm?.Network ?? "eth";
        set { if (_tapeVm is not null) _tapeVm.Network = value ?? "eth"; this.RaisePropertyChanged(); }
    }

    private string _liqCustom = "";
    public string LiqCustom
    {
        get => _liqCustom;
        set
        {
            this.RaiseAndSetIfChanged(ref _liqCustom, (value ?? "").ToUpperInvariant());
            if (_liqCustom.Length >= 6 && _liqVm is not null && _liqCustom != _liqVm.Symbol)
            {
                _liqAlertDismissed = false;
                Fire(_liqVm.SetSymbolCommand, _liqCustom);
            }
            Recompute();
        }
    }

    public string[] Networks { get; } = { "eth", "bsc", "solana", "base", "arbitrum", "polygon_pos" };

    private void RaiseInputs()
    {
        foreach (var n in new[] { nameof(NewsSearch), nameof(NewsSymbol), nameof(TapeSymbol), nameof(TapeThreshold), nameof(TapePool), nameof(TapeNetwork), nameof(LiqCustom) })
            this.RaisePropertyChanged(n);
    }

    // ── collections ──────────────────────────────────────────────────────────
    public ObservableCollection<ViewTab> ViewTabs { get; } = new();
    public ObservableCollection<TickerItem> Ticker { get; } = new();
    public ObservableCollection<Bullet> DigestBullets { get; } = new();
    public ObservableCollection<MfChip> Sentiments { get; } = new();
    public ObservableCollection<SortChip> Sorts { get; } = new();
    public ObservableCollection<NewsRow> NewsRows { get; } = new();
    public ObservableCollection<MfChip> TapeVenues { get; } = new();
    public ObservableCollection<SymBtn> QuickSyms { get; } = new();
    public ObservableCollection<TapeRow> TapeRows { get; } = new();
    public ObservableCollection<SymBtn> LiqSymbols { get; } = new();
    public ObservableCollection<SideToggle> LiqSides { get; } = new();
    public ObservableCollection<KpiCard> LiqKpis { get; } = new();
    public ObservableCollection<LiqBand> LiqBands { get; } = new();
    public ObservableCollection<LiqAxisLabel> LiqAxis { get; } = new();
    public ObservableCollection<Bullet> InsightBullets { get; } = new();
    public ObservableCollection<SideRow> Side1Rows { get; } = new();
    public ObservableCollection<KvRow> Side2Rows { get; } = new();
    public ObservableCollection<KvRow> PanelRows { get; } = new();

    // ── commands ─────────────────────────────────────────────────────────────
    public ICommand OpenPulseCommand { get; }
    public ICommand ToggleStreamCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AiCommand { get; }
    public ICommand RegenCommand { get; }
    public ICommand CreateAlertCommand { get; }
    public ICommand ToggleImportantCommand { get; }
    public ICommand MarkReadCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand TradeCommand { get; }
    public ICommand CloseModalCommand { get; }
    public ICommand AlertCreateCommand { get; }

    // The rail's action button has one behaviour per view, so the three commands are built once.
    // Re-allocating them inside RebuildSide2 made Side2ActionCommand a new instance on every
    // coalesced tick, which re-hooked the button's CanExecuteChanged five times a second.
    private readonly ICommand _side2NewsAction;
    private readonly ICommand _side2TapeAction;
    private readonly ICommand _side2LiqAction;

    // ── cached derived ───────────────────────────────────────────────────────
    private int _pressurePct, _largeCount, _tapeCount;
    private string _flowSignal = "", _flowColor = MarketFeedData.Text3, _flowSummary = "", _flowBullet = "", _flowDot = MarketFeedData.Faint;

    // ── header scalars ───────────────────────────────────────────────────────
    public string PulseLabel { get; private set; } = "NO DATA";
    public string PulseColor { get; private set; } = MarketFeedData.Faint;
    public string PulseBorder { get; private set; } = "#0d1b27";
    public string PulseBg { get; private set; } = SemanticColor.Surface;

    public string M1Label { get; private set; } = "";
    public string M1Value { get; private set; } = "";
    public string M1Sub { get; private set; } = "";
    public string M1Color { get; private set; } = MarketFeedData.Text;
    public string M2Label { get; private set; } = "";
    public string M2Value { get; private set; } = "";
    public string M2Sub { get; private set; } = "";
    public string M2Color { get; private set; } = MarketFeedData.Text;

    public string StreamLabel => !Live ? "NOT CONNECTED" : _streaming ? "STREAMING" : "PAUSED";
    public string StreamColor => !Live ? MarketFeedData.Faint : _streaming ? MarketFeedData.Green : MarketFeedData.Amber;
    public string StreamBorder => !Live ? "#0d1b27" : _streaming ? "#14302e" : "#3a2a12";
    public string StreamBg => !Live ? SemanticColor.Surface : _streaming ? "#061615" : "#150f04";
    public string AiBtn => AiRunning ? "✦ ANALYSING…" : "✦ AI ANALYSE";

    private bool AiRunning => IsNews ? _newsVm?.AiDigestRunning == true
        : IsLiq ? _liqVm?.InsightRunning == true
        : false;

    // ── digest ───────────────────────────────────────────────────────────────
    public string DigestTitle { get; private set; } = "";
    public string DigestSignal { get; private set; } = "";
    public string DigestBadgeColor { get; private set; } = MarketFeedData.Text3;
    public string DigestBadgeBg { get; private set; } = "#050f14";
    public string DigestBadgeBorder { get; private set; } = SemanticColor.Stroke;
    public string DigestMeta { get; private set; } = "";
    public string DigestText { get; private set; } = "";
    public string DigestBtnLabel => AiRunning ? "ANALYSING…" : "↻ REGENERATE";
    public string DigestBtnColor => AiRunning ? MarketFeedData.Accent : "#5a7a94";

    // ── news view ────────────────────────────────────────────────────────────
    private bool Important => _newsVm?.ShowImportantOnly == true;
    public string ImpMark => Important ? "✓" : "";
    public string ImpBoxBorder => Important ? MarketFeedData.Accent : SemanticColor.Stroke;
    public string ImpBoxBg => Important ? MarketFeedData.Accent : "transparent";
    public string ImpColor => Important ? MarketFeedData.Accent : MarketFeedData.Text3;
    public string ImpBorder => Important ? "#14302e" : "#0d1b27";
    public string ImpBg => Important ? "#061615" : SemanticColor.Surface;
    public bool HasUnread => (_newsVm?.UnreadCount ?? 0) > 0;
    public string UnreadLabel => (_newsVm?.UnreadCount ?? 0) + " NEW";
    public string NewsMeta { get; private set; } = "";
    public bool NewsEmpty => NewsRows.Count == 0;
    public string NewsEmptyNote { get; private set; } = "";
    public string SourcesNote => "RSS: CoinTelegraph · CoinDesk · Decrypt · The Block · Bitcoin Magazine — CRYPTOPANIC_API_KEY adds CryptoPanic";

    // ── tape view ────────────────────────────────────────────────────────────
    public bool IsCex => _tapeVm?.IsCex ?? true;
    public bool IsDex => _tapeVm?.IsDex ?? false;
    public string TapeHint => IsDex
        ? "On-chain swaps in a single pool, including the originating wallet. Follow an address across pools to tell a market maker from a fresh wallet."
        : "Other participants’ fills on the venue. CEX prints are anonymous — you see size and aggression, never identity. Prints above the threshold are highlighted.";
    public string TapePressure { get; private set; } = "";
    public string TapePressureColor { get; private set; } = MarketFeedData.Text3;
    public double TapePressureRatio => _pressurePct / 100.0;
    public string TapeStatus { get; private set; } = "";

    // ── liq view ─────────────────────────────────────────────────────────────
    public bool LiqAlertOn => !_liqAlertDismissed && _liqVm?.IsProximityAlertActive == true
                              && !string.IsNullOrWhiteSpace(_liqVm.ProximityAlertMessage);
    public string LiqAlertText { get; private set; } = "";
    public string LiqSource { get; private set; } = "source: not connected";
    public string LiqPriceLabel { get; private set; } = "";

    /// <summary>Real, exchange-reported liquidations (empty until the socket delivers one).</summary>
    private IReadOnlyList<LiquidationEvent> LiqFeed => _liqVm?.LiquidationFeed ?? Array.Empty<LiquidationEvent>();
    private LiquidationStreamStats LiqStats => _liqVm?.LiquidationStats ?? LiquidationStreamStats.Empty;
    private string LiqStreamStatus => _liqVm?.LiquidationStreamStatus ?? "not connected";
    private string LiqStreamColor => _liqVm is null ? MarketFeedData.Faint : _liqVm.LiquidationStreamColor;

    // ── rail ─────────────────────────────────────────────────────────────────
    public string RailTitle { get; private set; } = "";
    public string InsightTitle { get; private set; } = "";
    public string InsightMeta { get; private set; } = "";
    public string InsightSignal { get; private set; } = "";
    public string InsightSignalColor { get; private set; } = MarketFeedData.Text3;
    public string InsightSummary { get; private set; } = "";
    public string InsightBtnLabel => AiRunning ? "ANALYSING…" : "RE-ANALYSE";
    public string Side1Title { get; private set; } = "";
    public string Side1Meta { get; private set; } = "";
    public string Side2Title { get; private set; } = "";
    public string Side2ActionLabel { get; private set; } = "";
    public ICommand? Side2ActionCommand { get; private set; }

    // ── footer ───────────────────────────────────────────────────────────────
    public string FooterState { get; private set; } = "";
    public string FooterColor { get; private set; } = MarketFeedData.Faint;
    public string FooterF1 { get; private set; } = "";
    public string FooterF2 { get; private set; } = "";
    public string FooterF3 { get; private set; } = "";
    public string FooterSync { get; private set; } = "no updates yet";

    // ── panel / toast ────────────────────────────────────────────────────────
    private string? _modal;
    public bool ModalPanel => _modal == "panel";
    public string PanelTitle { get; private set; } = "";
    public string PanelSub { get; private set; } = "";
    public string PanelNote { get; private set; } = "";
    public string PanelActionLabel { get; private set; } = "OK";
    public ICommand? PanelActionCommand { get; private set; }

    private bool _hasToast;
    public bool HasToast { get => _hasToast; private set => this.RaiseAndSetIfChanged(ref _hasToast, value); }
    public string ToastMsg { get; private set; } = "";
    public string ToastColor { get; private set; } = MarketFeedData.Accent;
    public string ToastIcon { get; private set; } = "";
    public string ToastBorder { get; private set; } = "#0d1b27";
    public string ToastMeta { get; private set; } = "";

    // ═════════════════════════ actions ═══════════════════════════════════════

    private void SetView(string v)
    {
        if (_view == v) return;
        _view = v;
        SyncLiveGates();
        Recompute();
    }

    private void ToggleStream()
    {
        if (!Live) { Toast("Desk is not connected to the shell yet", "warn"); return; }
        _streaming = !_streaming;
        SyncLiveGates();
        Recompute();
        Toast(_streaming ? "Tape stream resumed" : "Tape stream paused", _streaming ? "ok" : "warn");
    }

    private void RefreshSource()
    {
        if (!Live) { Toast("Desk is not connected to the shell yet", "warn"); return; }
        if (IsNews) { Fire(_newsVm?.RefreshDigestCommand); Toast("Digest refresh requested — RSS polls on its own schedule", "info"); }
        else if (IsTape) { Fire(_tapeVm?.RefreshCommand); Toast("Tape re-subscribed", "info"); }
        else { Fire(_liqVm?.RefreshCommand); Toast("Heatmap reload requested", "info"); }
    }

    private void RunAi()
    {
        if (!Live) { Toast("Desk is not connected to the shell yet", "warn"); return; }
        if (IsNews) { Fire(_newsVm?.RefreshDigestCommand); Toast("Digesting the latest headlines", "ai"); }
        else if (IsLiq) { Fire(_liqVm?.AnalyzeWithAiCommand); Toast("Reading the liquidation map", "ai"); }
        else { Recompute(); Toast("Tape read recomputed from the buffered prints", "ai"); }
    }

    private void ToggleImportant()
    {
        if (_newsVm is null) return;
        _newsVm.ShowImportantOnly = !_newsVm.ShowImportantOnly;
        Recompute();
    }

    private void MarkAllRead()
    {
        if (_newsVm is null) return;
        foreach (var r in _newsVm.Rows) _read.Add(r.Item.Id);
        Fire(_newsVm.ClearUnreadCommand);
        Recompute();
        Toast("Headlines marked read", "info");
    }

    private void ClearFilters()
    {
        if (_newsVm is null) return;
        Fire(_newsVm.ClearFilterCommand);
        _newsSort = "time";
        RaiseInputs();
        Recompute();
        Toast("Filters cleared", "info");
    }

    private void ApplyTape()
    {
        if (_tapeVm is null) { Toast("Desk is not connected to the shell yet", "warn"); return; }
        Fire(_tapeVm.RefreshCommand);
        Toast("Re-subscribed to " + (IsCex ? _tapeVm.Symbol : _tapeVm.Network + " pool"), "ok");
    }

    // ═════════════════════════ recompute ═════════════════════════════════════

    public void Recompute()
    {
        var news = _newsVm is null ? new List<NewsItemRowVM>() : _newsVm.Rows.ToList();
        if (_newsSort == "votes") news = news.OrderByDescending(n => n.Item.Votes).ToList();

        var tape = _tapeVm is null ? new List<TapeRowVM>() : _tapeVm.Rows.ToList();
        ComputeTapeStats(tape);

        RebuildViewTabs(news, tape);
        RebuildHeaderKpis(news, tape);
        RebuildPulse();
        RebuildTicker(news, tape);
        RebuildDigest(news);
        // Only the view on screen owns rows worth rebuilding; SetView recomputes on every
        // switch, so the other two lists keep their last content instead of being rebuilt
        // (Clear + re-Add) on every coalesced burst.
        if (IsNews) RebuildNews(news);
        else if (IsTape) RebuildTape(tape);
        else RebuildLiq();
        RebuildRail(news, tape);
        RebuildFooter(news, tape);

        FooterSync = Live ? "last update " + Now() : "no updates yet";
        RaiseProjected();
    }

    /// <summary>Last value published for each scalar in <see cref="RaiseProjected"/>, in call order.</summary>
    private readonly List<object?> _published = new();

    /// <summary>
    /// Publishes the scalars a rebuild can move. This used to be <c>RaisePropertyChanged(string.Empty)</c>,
    /// which asked every binding on the desk — all nineteen collections included — to re-read itself on
    /// each coalesced tick, five times a second. Each value is compared with what was last published, so
    /// a tick that only moves the clock now raises one property instead of the whole view model.
    ///
    /// Deliberately absent: the collections (they notify through ObservableCollection), the constants
    /// (<see cref="Networks"/>, <see cref="SourcesNote"/>), the fixed commands, and the panel and
    /// toast scalars (raised where they are written, off the rebuild path).
    /// A property added to the rebuilds above belongs here too, or its binding goes stale.
    /// </summary>
    private void RaiseProjected()
    {
        var i = 0;
        void Raise(string name, object? value)
        {
            var known = i < _published.Count;
            if (known && Equals(_published[i], value)) { i++; return; }
            if (known) _published[i] = value; else _published.Add(value);
            i++;
            this.RaisePropertyChanged(name);
        }

        // view switch
        Raise(nameof(ViewNews), ViewNews);
        Raise(nameof(ViewTape), ViewTape);
        Raise(nameof(ViewLiq), ViewLiq);

        // header: pulse, the two KPIs, the stream badge
        Raise(nameof(PulseLabel), PulseLabel);
        Raise(nameof(PulseColor), PulseColor);
        Raise(nameof(PulseBorder), PulseBorder);
        Raise(nameof(PulseBg), PulseBg);
        Raise(nameof(M1Label), M1Label);
        Raise(nameof(M1Value), M1Value);
        Raise(nameof(M1Sub), M1Sub);
        Raise(nameof(M1Color), M1Color);
        Raise(nameof(M2Label), M2Label);
        Raise(nameof(M2Value), M2Value);
        Raise(nameof(M2Sub), M2Sub);
        Raise(nameof(M2Color), M2Color);
        Raise(nameof(StreamLabel), StreamLabel);
        Raise(nameof(StreamColor), StreamColor);
        Raise(nameof(StreamBorder), StreamBorder);
        Raise(nameof(StreamBg), StreamBg);
        Raise(nameof(AiBtn), AiBtn);

        // digest
        Raise(nameof(DigestTitle), DigestTitle);
        Raise(nameof(DigestSignal), DigestSignal);
        Raise(nameof(DigestBadgeColor), DigestBadgeColor);
        Raise(nameof(DigestBadgeBg), DigestBadgeBg);
        Raise(nameof(DigestBadgeBorder), DigestBadgeBorder);
        Raise(nameof(DigestMeta), DigestMeta);
        Raise(nameof(DigestText), DigestText);
        Raise(nameof(DigestBtnLabel), DigestBtnLabel);
        Raise(nameof(DigestBtnColor), DigestBtnColor);

        // news view
        Raise(nameof(ImpMark), ImpMark);
        Raise(nameof(ImpBoxBorder), ImpBoxBorder);
        Raise(nameof(ImpBoxBg), ImpBoxBg);
        Raise(nameof(ImpColor), ImpColor);
        Raise(nameof(ImpBorder), ImpBorder);
        Raise(nameof(ImpBg), ImpBg);
        Raise(nameof(HasUnread), HasUnread);
        Raise(nameof(UnreadLabel), UnreadLabel);
        Raise(nameof(NewsMeta), NewsMeta);
        Raise(nameof(NewsEmpty), NewsEmpty);
        Raise(nameof(NewsEmptyNote), NewsEmptyNote);

        // tape view
        Raise(nameof(IsCex), IsCex);
        Raise(nameof(IsDex), IsDex);
        Raise(nameof(TapeHint), TapeHint);
        Raise(nameof(TapePressure), TapePressure);
        Raise(nameof(TapePressureColor), TapePressureColor);
        Raise(nameof(TapePressureRatio), TapePressureRatio);
        Raise(nameof(TapeStatus), TapeStatus);

        // liquidations view
        Raise(nameof(LiqAlertOn), LiqAlertOn);
        Raise(nameof(LiqAlertText), LiqAlertText);
        Raise(nameof(LiqSource), LiqSource);
        Raise(nameof(LiqPriceLabel), LiqPriceLabel);

        // rail
        Raise(nameof(RailTitle), RailTitle);
        Raise(nameof(InsightTitle), InsightTitle);
        Raise(nameof(InsightMeta), InsightMeta);
        Raise(nameof(InsightSignal), InsightSignal);
        Raise(nameof(InsightSignalColor), InsightSignalColor);
        Raise(nameof(InsightSummary), InsightSummary);
        Raise(nameof(InsightBtnLabel), InsightBtnLabel);
        Raise(nameof(Side1Title), Side1Title);
        Raise(nameof(Side1Meta), Side1Meta);
        Raise(nameof(Side2Title), Side2Title);
        Raise(nameof(Side2ActionLabel), Side2ActionLabel);
        Raise(nameof(Side2ActionCommand), Side2ActionCommand);

        // footer
        Raise(nameof(FooterState), FooterState);
        Raise(nameof(FooterColor), FooterColor);
        Raise(nameof(FooterF1), FooterF1);
        Raise(nameof(FooterF2), FooterF2);
        Raise(nameof(FooterF3), FooterF3);
        Raise(nameof(FooterSync), FooterSync);

        // input mirrors: the desk does not own these, the source view models do, so a change
        // made anywhere else has to reach the boxes — that is what the blanket raise did for them.
        Raise(nameof(NewsSearch), NewsSearch);
        Raise(nameof(NewsSymbol), NewsSymbol);
        Raise(nameof(TapeSymbol), TapeSymbol);
        Raise(nameof(TapeThreshold), TapeThreshold);
        Raise(nameof(TapePool), TapePool);
        Raise(nameof(TapeNetwork), TapeNetwork);
        Raise(nameof(LiqCustom), LiqCustom);
    }

    private void ComputeTapeStats(List<TapeRowVM> tape)
    {
        // One pass over the buffered prints: large count, both notionals and both clip counts
        // used to be six separate LINQ walks over the same list on every rebuild.
        _tapeCount = tape.Count;
        _largeCount = 0;
        double buy = 0, sell = 0;
        int buyPrints = 0, sellPrints = 0;
        foreach (var r in tape)
        {
            if (r.IsLarge) _largeCount++;
            var notional = (double)r.Trade.QuoteQty;
            if (r.Side == "SELL") { sell += notional; sellPrints++; }
            else { buy += notional; buyPrints++; }
        }

        var total = buy + sell;
        _pressurePct = total > 0 ? (int)Math.Round(buy / total * 100) : 0;

        if (tape.Count == 0)
        {
            _flowSignal = Live ? "No prints buffered" : "Not connected";
            _flowColor = MarketFeedData.Faint;
            _flowSummary = Live
                ? "The tape has not delivered a print yet. Open the LIVE TAPE view to start the feed, or check the symbol / pool address."
                : "The desk is not attached to the shell, so there is no trade feed to read.";
            _flowBullet = "0 prints";
            _flowDot = MarketFeedData.Faint;
            return;
        }

        var avgBuy = buyPrints > 0 ? buy / buyPrints : 0d;
        var avgSell = sellPrints > 0 ? sell / sellPrints : 0d;
        var clip = avgSell > 0 ? avgBuy / avgSell : 0;
        var c = clip.ToString("0.0", Inv);

        if (clip > 1.3)
        {
            _flowSignal = "Buyers work the larger clips";
            _flowColor = MarketFeedData.Green;
            _flowSummary = $"Average buy clip is {c}× the average sell clip across {tape.Count} buffered prints — size is arriving on the bid.";
            _flowBullet = "Avg buy clip " + c + "× the avg sell clip";
            _flowDot = MarketFeedData.Green;
        }
        else if (clip > 0 && clip < 0.77)
        {
            _flowSignal = "Sellers work the larger clips";
            _flowColor = MarketFeedData.Red;
            _flowSummary = $"Average buy clip is only {c}× the average sell clip across {tape.Count} buffered prints — size is arriving on the offer.";
            _flowBullet = "Avg buy clip " + c + "× the avg sell clip";
            _flowDot = MarketFeedData.Red;
        }
        else
        {
            _flowSignal = "Balanced two-way tape";
            _flowColor = MarketFeedData.Text3;
            _flowSummary = $"Buy and sell clips are similar in size across {tape.Count} buffered prints; the tape carries no size imbalance right now.";
            _flowBullet = clip > 0 ? "Avg buy clip " + c + "× the avg sell clip" : "One-sided window — no clip ratio";
            _flowDot = MarketFeedData.Amber;
        }
    }

    private void RebuildViewTabs(List<NewsItemRowVM> news, List<TapeRowVM> tape)
    {
        ViewTabs.Clear();
        void T(string id, string label, string count)
        {
            var on = _view == id; var key = id;
            ViewTabs.Add(new ViewTab
            {
                Label = label, Count = count,
                Bg = on ? "#0e2a2a" : "transparent",
                Fg = on ? MarketFeedData.Accent : MarketFeedData.Dimmer,
                CountColor = on ? MarketFeedData.Accent : "#1e3048",
                Command = new RelayCommand(() => SetView(key)),
            });
        }
        T("news", "NEWS", Live ? (_newsVm?.UnreadCount ?? 0).ToString() : "—");
        T("tape", "LIVE TAPE", !Live ? "—" : _streaming ? tape.Count.ToString() : "off");
        T("liq", "LIQUIDATIONS", Live ? (_liqVm?.ClusterOverlay.Count ?? 0).ToString() : "—");
    }

    private void RebuildHeaderKpis(List<NewsItemRowVM> news, List<TapeRowVM> tape)
    {
        if (IsNews)
        {
            M1Label = "HEADLINES SHOWN"; M1Value = news.Count.ToString();
            M1Sub = Live ? (_newsVm?.UnreadCount ?? 0) + " unread" : "not connected";
            M1Color = news.Count > 0 ? MarketFeedData.Text : MarketFeedData.Faint;
            var imp = news.Count(n => n.IsImportant);
            M2Label = "IMPORTANT"; M2Value = imp.ToString();
            M2Sub = news.Count > 0 ? "flagged by the feed" : "no data";
            M2Color = imp > 0 ? MarketFeedData.Amber : MarketFeedData.Faint;
        }
        else if (IsTape)
        {
            M1Label = "BUY PRESSURE";
            M1Value = tape.Count > 0 ? _pressurePct + "%" : "—";
            M1Sub = tape.Count > 0 ? "of " + tape.Count + " prints (by notional)" : "no prints";
            M1Color = tape.Count == 0 ? MarketFeedData.Faint : _pressurePct > 55 ? MarketFeedData.Green : _pressurePct < 45 ? MarketFeedData.Red : MarketFeedData.Text;
            M2Label = "LARGE PRINTS"; M2Value = tape.Count > 0 ? _largeCount.ToString() : "—";
            M2Sub = "≥ " + ThresholdLabel();
            M2Color = _largeCount > 0 ? MarketFeedData.Amber : MarketFeedData.Faint;
        }
        else
        {
            var price = _liqVm?.CurrentPrice ?? 0m;
            M1Label = "CURRENT PRICE"; M1Value = MarketFeedData.Price(price);
            M1Sub = _liqVm is null ? "not connected" : _liqVm.Symbol;
            M1Color = price > 0 ? MarketFeedData.Amber : MarketFeedData.Faint;
            var clusters = _liqVm?.ClusterOverlay.Count ?? 0;
            M2Label = "CLUSTERS"; M2Value = clusters > 0 ? clusters.ToString() : "—";
            M2Sub = clusters > 0 ? "≥ $50M within ±15%" : "no clusters";
            M2Color = clusters > 0 ? MarketFeedData.Red : MarketFeedData.Faint;
        }
    }

    private string ThresholdLabel()
        => _tapeVm is null ? "$—" : "$" + _tapeVm.LargePrintThreshold.ToString("N0", Inv);

    private void RebuildPulse()
    {
        if (_newsVm is null || string.IsNullOrWhiteSpace(_newsVm.PulseLabel))
        {
            PulseLabel = "NO DATA"; PulseColor = MarketFeedData.Faint;
            PulseBorder = "#0d1b27"; PulseBg = SemanticColor.Surface;
            return;
        }
        PulseLabel = _newsVm.PulseLabel.ToUpperInvariant();
        PulseColor = _newsVm.PulseBrush;
        PulseBorder = MarketFeedData.Alpha(PulseColor, "55");
        PulseBg = MarketFeedData.Alpha(PulseColor, "18");
    }

    private void RebuildTicker(List<NewsItemRowVM> news, List<TapeRowVM> tape)
    {
        Ticker.Clear();
        if (!Live)
        {
            Ticker.Add(new TickerItem { Tag = "DESK", Text = "not attached to the shell", Val = "no data", Color = MarketFeedData.Faint });
            return;
        }

        if (IsNews)
        {
            foreach (var n in news.Take(3))
                Ticker.Add(new TickerItem
                {
                    Tag = n.IsImportant ? "IMPORTANT" : n.Source.ToUpperInvariant(),
                    Text = MarketFeedData.Trunc(n.Title, 64),
                    Val = n.AgeLabel,
                    Color = n.SentimentBrush,
                });
            if (news.Count == 0)
                Ticker.Add(new TickerItem { Tag = "NEWS", Text = _newsVm?.StatusLabel ?? "no headlines", Val = "0", Color = MarketFeedData.Faint });
        }
        else if (IsTape)
        {
            foreach (var t in tape.Where(r => r.IsLarge).Take(2))
                Ticker.Add(new TickerItem
                {
                    Tag = "LARGE",
                    Text = t.Side + " " + t.QtyLabel + " @ " + t.PriceLabel,
                    Val = MarketFeedData.Money0(t.Trade.QuoteQty),
                    Color = t.SideBrush,
                });
            Ticker.Add(tape.Count > 0
                ? new TickerItem { Tag = "BUFFER", Text = "prints buffered", Val = tape.Count.ToString(), Color = MarketFeedData.Text3 }
                : new TickerItem { Tag = "TAPE", Text = _tapeVm?.StatusLabel ?? "idle", Val = "0", Color = MarketFeedData.Faint });
        }
        else
        {
            Ticker.Add(new TickerItem
            {
                Tag = "STREAM",
                Text = _liqVm is null ? "not connected" : LiquidationStreamService.SourceLabel,
                Val = LiqStreamStatus,
                Color = LiqStreamColor,
            });
            // Real prints first — the estimated map lines are clearly labelled behind them.
            foreach (var e in LiqFeed.Take(4))
                Ticker.Add(new TickerItem
                {
                    Tag = "LIQ " + e.SideLabel,
                    Text = e.Symbol + " " + MarketFeedData.Price(e.Price) + " · " + e.TimeLabel,
                    Val = MarketFeedData.Compact(e.NotionalUsd),
                    Color = e.Side == LiquidationSide.Long ? MarketFeedData.Accent : MarketFeedData.Red,
                });
            Ticker.Add(new TickerItem { Tag = "MAP", Text = _liqVm?.DataSourceLabel ?? "not connected", Val = _liqVm?.Symbol ?? "—", Color = MarketFeedData.Faint });
            var topShort = _liqVm?.TopShortLabel ?? "";
            var topLong = _liqVm?.TopLongLabel ?? "";
            if (topShort.Length > 0)
                Ticker.Add(new TickerItem { Tag = "TOP SHORT (EST.)", Text = topShort, Val = "above", Color = MarketFeedData.Red });
            if (topLong.Length > 0)
                Ticker.Add(new TickerItem { Tag = "TOP LONG (EST.)", Text = topLong, Val = "below", Color = MarketFeedData.Accent });
        }
    }

    private void RebuildDigest(List<NewsItemRowVM> news)
    {
        DigestTitle = IsNews ? "AI MARKET DIGEST" : IsTape ? "TAPE READ" : "LIQUIDITY MAGNET";
        DigestBullets.Clear();

        if (!Live)
        {
            DigestSignal = "NO DATA"; DigestBadgeColor = MarketFeedData.Faint;
            DigestBadgeBg = SemanticColor.Surface; DigestBadgeBorder = "#0d1b27";
            DigestMeta = "not connected";
            DigestText = "The desk has no live sources attached, so there is nothing to summarise.";
            return;
        }

        if (IsNews)
        {
            var bias = _newsVm?.AiDigestBias ?? "";
            DigestSignal = string.IsNullOrWhiteSpace(bias) ? "NO DATA" : bias;
            DigestBadgeColor = _newsVm?.AiDigestBrush ?? MarketFeedData.Text3;
            DigestBadgeBg = MarketFeedData.Alpha(DigestBadgeColor, "18");
            DigestBadgeBorder = MarketFeedData.Alpha(DigestBadgeColor, "55");
            DigestMeta = _newsVm?.AiDigestRunning == true ? "reading…"
                : string.IsNullOrWhiteSpace(_newsVm?.AiDigestSource) ? "no digest yet" : _newsVm!.AiDigestSource;
            DigestText = string.IsNullOrWhiteSpace(_newsVm?.AiDigest) ? "No digest yet — waiting for headlines." : _newsVm!.AiDigest;

            if (!string.IsNullOrWhiteSpace(_newsVm?.PulseDetail))
                DigestBullets.Add(new Bullet { Text = _newsVm!.PulseDetail, Dot = _newsVm.PulseBrush });
            DigestBullets.Add(new Bullet { Text = news.Count(n => n.IsImportant) + " important of " + news.Count + " shown", Dot = MarketFeedData.Amber });
            if (!string.IsNullOrWhiteSpace(_newsVm?.StatusLabel))
                DigestBullets.Add(new Bullet { Text = _newsVm!.StatusLabel, Dot = MarketFeedData.Faint });
        }
        else if (IsTape)
        {
            DigestSignal = _tapeCount > 0 ? (_pressurePct > 55 ? "BUY SIDE" : _pressurePct < 45 ? "SELL SIDE" : "BALANCED") : "NO DATA";
            DigestBadgeColor = _tapeCount == 0 ? MarketFeedData.Faint : _pressurePct > 55 ? MarketFeedData.Green : _pressurePct < 45 ? MarketFeedData.Red : MarketFeedData.Text3;
            DigestBadgeBg = MarketFeedData.Alpha(DigestBadgeColor, "18");
            DigestBadgeBorder = MarketFeedData.Alpha(DigestBadgeColor, "55");
            DigestMeta = _tapeCount > 0 ? "computed from " + _tapeCount + " prints" : "no prints";
            DigestText = _tapeCount == 0
                ? _flowSummary
                : $"{_largeCount} of {_tapeCount} buffered prints cleared the {ThresholdLabel()} threshold. Buy side is {_pressurePct}% of traded notional in this window.";
            DigestBullets.Add(new Bullet { Text = "Buy pressure " + _pressurePct + "% by notional", Dot = _tapeCount == 0 ? MarketFeedData.Faint : _pressurePct > 50 ? MarketFeedData.Green : MarketFeedData.Red });
            DigestBullets.Add(new Bullet { Text = _largeCount + " large prints ≥ " + ThresholdLabel(), Dot = _largeCount > 0 ? MarketFeedData.Amber : MarketFeedData.Faint });
            DigestBullets.Add(new Bullet { Text = _flowBullet, Dot = _flowDot });
        }
        else
        {
            var (shortAbove, longBelow) = LiqSkew();
            var total = shortAbove + longBelow;
            var hasData = total > 0;
            DigestSignal = !hasData ? "NO DATA"
                : shortAbove > longBelow * 1.2m ? "MAGNET ABOVE"
                : longBelow > shortAbove * 1.2m ? "MAGNET BELOW" : "BALANCED";
            DigestBadgeColor = !hasData ? MarketFeedData.Faint
                : shortAbove > longBelow ? MarketFeedData.Red : MarketFeedData.Accent;
            DigestBadgeBg = MarketFeedData.Alpha(DigestBadgeColor, "18");
            DigestBadgeBorder = MarketFeedData.Alpha(DigestBadgeColor, "55");
            DigestMeta = _liqVm?.DataSourceLabel ?? "not connected";
            DigestText = !hasData
                ? "No liquidation clusters loaded for this symbol yet. Open the LIQUIDATIONS view to load the map."
                : $"Clusters above price total {MarketFeedData.Compact(shortAbove)} in short liquidations against {MarketFeedData.Compact(longBelow)} of longs below. Sizes come from {_liqVm!.DataSourceLabel}.";
            if (hasData)
            {
                DigestBullets.Add(new Bullet { Text = "Shorts above price " + MarketFeedData.Compact(shortAbove), Dot = MarketFeedData.Red });
                DigestBullets.Add(new Bullet { Text = "Longs below price " + MarketFeedData.Compact(longBelow), Dot = MarketFeedData.Accent });
            }
            DigestBullets.Add(new Bullet { Text = "Map source: " + (_liqVm?.DataSourceLabel ?? "not connected") + " — estimated", Dot = MarketFeedData.Amber });

            var streamStats = LiqStats;
            DigestBullets.Add(new Bullet
            {
                Text = streamStats.Events > 0
                    ? "Real stream: " + MarketFeedData.Compact(streamStats.TotalUsd) + " liquidated over " + (_liqVm?.LiquidationWindowLabel ?? "")
                    : "Real stream: " + LiqStreamStatus + " — no liquidations received yet",
                Dot = streamStats.Events > 0 ? MarketFeedData.Green : MarketFeedData.Faint,
            });
        }
    }

    /// <summary>Notional of short clusters above price vs long clusters below, from the live overlay.</summary>
    private (decimal ShortAbove, decimal LongBelow) LiqSkew()
    {
        if (_liqVm is null) return (0m, 0m);
        var price = _liqVm.CurrentPrice;
        if (price <= 0) return (0m, 0m);
        var shortAbove = _liqVm.ClusterOverlay.Where(c => c.Price > price && c.Side == "short").Sum(c => c.NotionalUsd);
        var longBelow = _liqVm.ClusterOverlay.Where(c => c.Price < price && c.Side == "long").Sum(c => c.NotionalUsd);
        return (shortAbove, longBelow);
    }

    // ── NEWS ─────────────────────────────────────────────────────────────────

    private void RebuildNews(List<NewsItemRowVM> news)
    {
        Sentiments.Clear();
        var options = _newsVm?.SentimentOptions ?? new[] { "All", "Bullish", "Bearish", "Neutral" };
        foreach (var opt in options)
        {
            var key = opt;
            var on = (_newsVm?.FilterSentiment ?? "All") == opt;
            Sentiments.Add(new MfChip
            {
                Label = opt.ToUpperInvariant(),
                Bg = on ? "#0e2a2a" : "transparent",
                Fg = on ? MarketFeedData.Accent : MarketFeedData.Dimmer,
                Command = new RelayCommand(() => { if (_newsVm is not null) { _newsVm.FilterSentiment = key; Recompute(); } }),
            });
        }

        Sorts.Clear();
        foreach (var (id, label) in new[] { ("time", "NEWEST"), ("votes", "MOST VOTED") })
        {
            var key = id; var on = _newsSort == id;
            Sorts.Add(new SortChip
            {
                Label = label,
                Border = on ? SemanticColor.Stroke : "#0d1b27",
                Bg = on ? "#08131d" : "transparent",
                Fg = on ? MarketFeedData.Accent : MarketFeedData.Faint,
                Command = new RelayCommand(() => { _newsSort = key; Recompute(); }),
            });
        }

        NewsMeta = !Live ? "not connected" : news.Count + " shown";
        NewsEmptyNote = !Live ? "Desk not connected." : _newsVm?.IsLoading == true ? "Loading feeds…" : (_newsVm?.StatusLabel ?? "No headlines.");

        NewsRows.Clear();
        foreach (var n in news)
        {
            var item = n.Item;
            var (label, color, bg, border) = MarketFeedData.Sent(SentKey(item.Sentiment));
            var isUnread = !_read.Contains(item.Id);
            var votes = item.Votes;
            NewsRows.Add(new NewsRow
            {
                Title = n.Title,
                TitleColor = isUnread ? MarketFeedData.Text : "#a9b8c8",
                Sentiment = label, SentColor = color, SentBg = bg, SentBorder = border,
                Tags = n.CurrencyTags,
                Important = n.IsImportant,
                Source = n.Source,
                Age = n.AgeLabel,
                Votes = votes != 0 ? "▲ " + votes : "—",
                VotesColor = votes > 120 ? MarketFeedData.Green : votes != 0 ? MarketFeedData.Text3 : MarketFeedData.Faint,
                Impact = n.IsImportant ? "important" : "",
                ImpactColor = n.IsImportant ? MarketFeedData.Amber : MarketFeedData.Faint,
                Bg = isUnread ? "rgba(33,230,193,.02)" : "transparent",
                LeftBorder = isUnread ? MarketFeedData.Accent : "transparent",
                Command = new RelayCommand(() => OpenNews(n, label, color)),
            });
        }
    }

    private static string SentKey(NewsSentiment s) => s switch
    {
        NewsSentiment.Bullish => "bullish",
        NewsSentiment.Bearish => "bearish",
        _ => "neutral",
    };

    private void OpenNews(NewsItemRowVM row, string sentLabel, string sentColor)
    {
        var item = row.Item;
        _read.Add(item.Id);
        var first = item.Currencies.FirstOrDefault() ?? "";

        var rows = new List<KvRow>
        {
            new() { Label = "Source", Value = item.Source, Color = MarketFeedData.Text },
            new() { Label = "Published", Value = item.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Color = MarketFeedData.Text3 },
            new() { Label = "Age", Value = row.AgeLabel, Color = MarketFeedData.Text3 },
            new() { Label = "Sentiment", Value = sentLabel, Color = sentColor },
            new() { Label = "Coins mentioned", Value = row.HasTags ? row.CurrencyTags : "none detected", Color = row.HasTags ? MarketFeedData.Blue : MarketFeedData.Faint },
            new() { Label = "Community votes", Value = item.Votes != 0 ? "▲ " + item.Votes : "not provided by this feed", Color = item.Votes != 0 ? MarketFeedData.Text : MarketFeedData.Faint },
            new() { Label = "Flagged important", Value = item.IsImportant ? "yes" : "no", Color = item.IsImportant ? MarketFeedData.Amber : MarketFeedData.Faint },
            new() { Label = "Link", Value = string.IsNullOrWhiteSpace(item.Url) ? "not provided" : MarketFeedData.Trunc(item.Url, 46), Color = MarketFeedData.Text3 },
        };

        var note = "Sentiment and the important flag are heuristics applied by the feed service to the headline text — they are labels, not forecasts. Votes only exist for CryptoPanic items.";

        if (first.Length > 0)
            OpenPanel(item.Title, item.Source + " · " + row.AgeLabel, rows, note,
                "FILTER FEED BY " + first,
                () => { CloseModal(); if (_newsVm is not null) _newsVm.FilterSymbol = first; RaiseInputs(); Recompute(); Toast("Filtered headlines to " + first, "ok"); });
        else
            OpenPanel(item.Title, item.Source + " · " + row.AgeLabel, rows, note, "CLOSE", CloseModal);

        Recompute();
    }

    // ── TAPE ─────────────────────────────────────────────────────────────────

    private void RebuildTape(List<TapeRowVM> tape)
    {
        TapeVenues.Clear();
        foreach (var (id, label) in new[] { ("CEX", "CEX"), ("DEX", "DEX") })
        {
            var key = id;
            var on = (_tapeVm?.Venue ?? "CEX") == id;
            TapeVenues.Add(new MfChip
            {
                Label = label,
                Bg = on ? "#0e2a2a" : "transparent",
                Fg = on ? MarketFeedData.Accent : MarketFeedData.Dimmer,
                Command = new RelayCommand(() =>
                {
                    if (_tapeVm is null) return;
                    Fire(key == "CEX" ? _tapeVm.SelectCexCommand : _tapeVm.SelectDexCommand);
                    RaiseInputs();
                    Recompute();
                }),
            });
        }

        QuickSyms.Clear();
        foreach (var x in new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT" })
        {
            var xx = x; var on = (_tapeVm?.Symbol ?? "") == x;
            QuickSyms.Add(new SymBtn
            {
                Label = x.Replace("USDT", ""),
                Border = on ? "#14302e" : SemanticColor.Stroke,
                Bg = on ? "#061615" : "#050f14",
                Fg = on ? MarketFeedData.Accent : MarketFeedData.Text3,
                Command = new RelayCommand(() =>
                {
                    if (_tapeVm is null) return;
                    _tapeVm.Symbol = xx;
                    Fire(_tapeVm.RefreshCommand);
                    RaiseInputs();
                    Recompute();
                }),
            });
        }

        TapePressure = !Live ? "Not connected"
            : tape.Count == 0 ? "No prints buffered"
            : _pressurePct > 55 ? "Buyers in control · " + _pressurePct + "% of notional"
            : _pressurePct < 45 ? "Sellers in control · " + (100 - _pressurePct) + "% of notional"
            : "Balanced tape · " + _pressurePct + "% buys";
        TapePressureColor = tape.Count == 0 ? MarketFeedData.Faint
            : _pressurePct > 55 ? MarketFeedData.Green
            : _pressurePct < 45 ? MarketFeedData.Red : MarketFeedData.Text3;
        TapeStatus = !Live ? "desk not connected" : _tapeVm?.StatusLabel ?? "idle";

        TapeRows.Clear();
        foreach (var r in tape)
        {
            var t = r.Trade;
            var isDex = t.Venue == "DEX";
            var row = r;
            TapeRows.Add(new TapeRow
            {
                Venue = r.VenueLabel,
                VenueColor = r.VenueBrush,
                Time = r.TimeLabel,
                Side = r.Side,
                SideColor = r.SideBrush,
                Weight = r.Weight,
                Price = r.PriceLabel,
                Qty = r.QtyLabel,
                Usd = MarketFeedData.Money0(t.QuoteQty),
                UsdNum = (double)t.QuoteQty,
                Wallet = isDex ? r.TraderLabel : "anonymous fill · taker",
                Flag = r.IsLarge ? "LARGE" : "",
                FlagColor = r.IsLarge ? MarketFeedData.Amber : "transparent",
                Bg = r.IsLarge ? (r.Side == "SELL" ? "rgba(255,107,107,.07)" : "rgba(61,220,132,.07)") : "transparent",
                Command = new RelayCommand(() => OpenTape(row)),
            });
        }
    }

    private void OpenTape(TapeRowVM row)
    {
        var t = row.Trade;
        var isDex = t.Venue == "DEX";
        var rows = new List<KvRow>
        {
            new() { Label = "Venue", Value = t.Venue + (isDex ? " · " + (_tapeVm?.Network ?? "") : ""), Color = row.VenueBrush },
            new() { Label = "Time", Value = t.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Color = MarketFeedData.Text3 },
            new() { Label = "Price", Value = row.PriceLabel, Color = MarketFeedData.Text },
            new() { Label = "Quantity", Value = row.QtyLabel, Color = MarketFeedData.Text },
            new() { Label = "Notional", Value = MarketFeedData.Money0(t.QuoteQty), Color = MarketFeedData.Text },
            new() { Label = "Side", Value = t.Side == "SELL" ? "SELL (taker hit bid)" : "BUY (taker lifted offer)", Color = row.SideBrush },
            new() { Label = "Counterparty", Value = isDex ? row.TraderLabel : "not disclosed by the venue", Color = isDex ? MarketFeedData.Text : MarketFeedData.Faint },
            new() { Label = "Tx hash", Value = string.IsNullOrWhiteSpace(t.TxHash) ? "n/a" : MarketFeedData.Trunc(t.TxHash, 22), Color = MarketFeedData.Text3 },
            new() { Label = "Large-print threshold", Value = ThresholdLabel(), Color = MarketFeedData.Amber },
        };

        OpenPanel(
            t.Side + " " + row.QtyLabel + " " + (isDex ? "pool token" : _tapeVm?.Symbol ?? ""),
            (isDex ? "on-chain swap" : "CEX anonymous fill") + " · " + (row.IsLarge ? "large print" : "ordinary size"),
            rows,
            isDex
                ? "DEX swaps carry the originating wallet, so the same address can be followed across pools. Data comes from GeckoTerminal."
                : "CEX tape is anonymous by design — size and aggression are public, identity is not. Data comes from the Binance public trades endpoint.",
            "CLOSE", CloseModal);
    }

    // ── LIQUIDATIONS ─────────────────────────────────────────────────────────

    private void RebuildLiq()
    {
        var symbol = _liqVm?.Symbol ?? "";
        var price = _liqVm?.CurrentPrice ?? 0m;

        LiqAlertText = _liqVm?.ProximityAlertMessage ?? "";
        LiqPriceLabel = price > 0 ? "mark " + MarketFeedData.Price(price) : "no price";
        // Two different things share this view: an estimated level map and a real event stream.
        LiqSource = _liqVm is null
            ? "source: not connected"
            : "map: " + _liqVm.DataSourceLabel + "  ·  stream: " + LiqStreamStatus + " (real)";

        LiqSymbols.Clear();
        foreach (var x in new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "DOGEUSDT" })
        {
            var xx = x; var on = symbol == x;
            LiqSymbols.Add(new SymBtn
            {
                Label = x.Replace("USDT", ""),
                Border = on ? "#14302e" : "#0d1b27",
                Bg = on ? "#061615" : SemanticColor.Surface,
                Fg = on ? MarketFeedData.Accent : MarketFeedData.Text3,
                Command = new RelayCommand(() =>
                {
                    if (_liqVm is null) return;
                    _liqAlertDismissed = false;
                    Fire(_liqVm.SetSymbolCommand, xx);
                    Recompute();
                }),
            });
        }

        LiqSides.Clear();
        void Side(bool isLong, string label, string col, bool on)
            => LiqSides.Add(new SideToggle
            {
                Label = label, Mark = on ? "✓" : "",
                BoxBorder = on ? col : SemanticColor.Stroke, BoxBg = on ? col : "transparent",
                Fg = on ? col : MarketFeedData.Faint,
                Border = on ? SemanticColor.Stroke : "#0d1b27", Bg = on ? "#08131d" : SemanticColor.Surface,
                Command = new RelayCommand(() =>
                {
                    if (_liqVm is null) return;
                    if (isLong) _liqVm.ShowLongs = !_liqVm.ShowLongs; else _liqVm.ShowShorts = !_liqVm.ShowShorts;
                    Recompute();
                }),
            });
        Side(true, "LONGS", MarketFeedData.Accent, _liqVm?.ShowLongs ?? false);
        Side(false, "SHORTS", MarketFeedData.Red, _liqVm?.ShowShorts ?? false);

        LiqKpis.Clear();
        var srcSub = _liqVm is null ? "not connected" : symbol + " · " + _liqVm.DataSourceLabel;
        LiqKpis.Add(new KpiCard
        {
            Label = "CURRENT PRICE", Value = MarketFeedData.Price(price), Sub = srcSub,
            Color = price > 0 ? MarketFeedData.Amber : MarketFeedData.Faint,
            Command = new RelayCommand(() => Toast(_liqVm?.StatusLabel ?? "not connected", "info")),
        });
        var topShort = _liqVm?.TopShortLabel ?? "";
        LiqKpis.Add(new KpiCard
        {
            Label = "TOP SHORT CLUSTER (EST.)", Value = string.IsNullOrWhiteSpace(topShort) ? "—" : topShort,
            Sub = string.IsNullOrWhiteSpace(topShort) ? "no data" : "estimated · shorts liquidate as price rises",
            Color = string.IsNullOrWhiteSpace(topShort) ? MarketFeedData.Faint : MarketFeedData.Red,
        });
        var topLong = _liqVm?.TopLongLabel ?? "";
        LiqKpis.Add(new KpiCard
        {
            Label = "TOP LONG CLUSTER (EST.)", Value = string.IsNullOrWhiteSpace(topLong) ? "—" : topLong,
            Sub = string.IsNullOrWhiteSpace(topLong) ? "no data" : "estimated · longs liquidate as price falls",
            Color = string.IsNullOrWhiteSpace(topLong) ? MarketFeedData.Faint : MarketFeedData.Accent,
        });

        // ── real stream KPIs (second row of the 3-column grid) ────────────────
        var stats = LiqStats;
        var hasStream = stats.Events > 0;
        LiqKpis.Add(new KpiCard
        {
            Label = "LIQUIDATED · LIVE",
            Value = hasStream ? MarketFeedData.Compact(stats.TotalUsd) : "—",
            Sub = hasStream
                ? (_liqVm?.LiquidationWindowLabel ?? "") + " · real"
                : (_liqVm is null ? "not connected" : "stream " + LiqStreamStatus),
            Color = hasStream ? MarketFeedData.Text : MarketFeedData.Faint,
            Command = new RelayCommand(OpenStreamPanel),
        });

        var longPct = (int)Math.Round(stats.LongShare * 100);
        LiqKpis.Add(new KpiCard
        {
            Label = "LONG / SHORT · LIVE",
            Value = hasStream ? longPct + "% / " + (100 - longPct) + "%" : "—",
            Sub = hasStream
                ? MarketFeedData.Compact(stats.LongUsd) + " longs · " + MarketFeedData.Compact(stats.ShortUsd) + " shorts"
                : "no liquidations received yet",
            Color = !hasStream ? MarketFeedData.Faint : longPct >= 55 ? MarketFeedData.Accent : longPct <= 45 ? MarketFeedData.Red : MarketFeedData.Text,
            Command = new RelayCommand(OpenStreamPanel),
        });

        var largest = stats.Largest;
        ICommand? largestCommand = null;
        if (largest is not null)
        {
            var top = largest;
            largestCommand = new RelayCommand(() => OpenLiquidation(top));
        }
        LiqKpis.Add(new KpiCard
        {
            Label = "LARGEST LIQUIDATION",
            Value = largest is null ? "—" : MarketFeedData.Compact(largest.NotionalUsd),
            Sub = largest is null
                ? "no liquidations received yet"
                : largest.Symbol + " " + largest.SideLabel + " @ " + MarketFeedData.Price(largest.Price) + " · " + largest.TimeLabel,
            Color = largest is null ? MarketFeedData.Faint : largest.Side == LiquidationSide.Long ? MarketFeedData.Accent : MarketFeedData.Red,
            Command = largestCommand,
        });

        // Heat bands: real bands from the heatmap VM, decimated to what this layout can show.
        LiqBands.Clear();
        var bands = _liqVm?.HeatBands ?? Array.Empty<HeatBand>();
        if (bands.Count > 0)
        {
            var step = Math.Max(1, (int)Math.Ceiling(bands.Count / (double)MaxLiqBands));
            for (int i = 0; i < bands.Count; i += step)
            {
                var b = bands[i];
                var ratio = Math.Clamp(b.Width / LiqPlotWidth, 0.0, 1.0);
                var fill = MarketFeedData.Hex(b.Fill, SemanticColor.Stroke);
                var isShort = b.Fill is Avalonia.Media.ISolidColorBrush s && s.Color.G < 0x80;
                // The VM lays bands out in pixel space over ±20% around the mark; invert for a label.
                var approx = price > 0 ? price * (decimal)(1.2 - 0.4 * Math.Clamp((b.Y + b.Height / 2.0) / 460.0, 0.0, 1.0)) : 0m;
                var title = (price > 0 ? "≈ " + MarketFeedData.Price(approx) + " · " : "")
                            + (isShort ? "short" : "long") + " band · " + (ratio * 100).ToString("F0", Inv) + "% of the largest cluster";
                var approxC = approx; var isShortC = isShort; var ratioC = ratio;
                LiqBands.Add(new LiqBand
                {
                    WidthRatio = ratio, Fill = fill, Title = title,
                    Command = new RelayCommand(() => OpenLiqBand(isShortC, ratioC, approxC)),
                });
            }
        }

        // Price axis: real labels, ordered top→bottom to match the bands, decimated to fit.
        LiqAxis.Clear();
        var labels = (_liqVm?.PriceLabels ?? Array.Empty<PriceAxisLabel>()).OrderBy(l => l.Y).ToList();
        if (labels.Count > 0)
        {
            var step = Math.Max(1, (int)Math.Ceiling(labels.Count / (double)MaxLiqAxisLabels));
            for (int i = 0; i < labels.Count; i += step)
                LiqAxis.Add(new LiqAxisLabel
                {
                    Text = labels[i].Text,
                    Color = labels[i].IsCurrentPrice ? MarketFeedData.Amber : "#26405a",
                });
        }
    }

    private void OpenLiqBand(bool isShort, double ratio, decimal approxPrice)
    {
        var rows = new List<KvRow>
        {
            new() { Label = "Side", Value = isShort ? "short liquidations" : "long liquidations", Color = isShort ? MarketFeedData.Red : MarketFeedData.Accent },
            new() { Label = "Approx. price level", Value = approxPrice > 0 ? "≈ " + MarketFeedData.Price(approxPrice) : "unknown", Color = MarketFeedData.Text },
            new() { Label = "Relative size", Value = (ratio * 100).ToString("F0", Inv) + "% of the largest cluster", Color = MarketFeedData.Text },
            new() { Label = "Symbol", Value = _liqVm?.Symbol ?? "—", Color = MarketFeedData.Text3 },
            new() { Label = "Data source", Value = _liqVm?.DataSourceLabel ?? "not connected", Color = MarketFeedData.Amber },
            new() { Label = "Feed status", Value = _liqVm?.StatusLabel ?? "—", Color = MarketFeedData.Text3 },
        };
        OpenPanel((isShort ? "Short" : "Long") + " liquidation band",
            (_liqVm?.Symbol ?? "") + " · " + (_liqVm?.DataSourceLabel ?? "not connected"),
            rows,
            _liqVm?.DataSourceLabel == "CoinGlass"
                ? "Cluster sizes come from CoinGlass exchange data."
                : "Without COINGLASS_API_KEY these levels are an estimate from a leverage model built on the live price, not exchange-reported liquidations. The price shown here is inverted from the band's position on the plot, so treat it as approximate.",
            "CLOSE", CloseModal);
    }

    /// <summary>Detail panel for one real, exchange-reported liquidation.</summary>
    private void OpenLiquidation(LiquidationEvent e)
    {
        var rows = new List<KvRow>
        {
            new() { Label = "Symbol", Value = e.Symbol, Color = MarketFeedData.Text },
            new() { Label = "Position liquidated", Value = e.SideLabel, Color = e.Side == LiquidationSide.Long ? MarketFeedData.Accent : MarketFeedData.Red },
            new() { Label = "Fill price", Value = MarketFeedData.Price(e.Price), Color = MarketFeedData.Text },
            new() { Label = "Quantity", Value = e.Quantity.ToString("0.######", Inv), Color = MarketFeedData.Text },
            new() { Label = "Notional", Value = MarketFeedData.Money0(e.NotionalUsd), Color = MarketFeedData.Text },
            new() { Label = "Time", Value = e.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Color = MarketFeedData.Text3 },
            new() { Label = "Source", Value = _liqVm?.LiquidationStreamSource ?? "—", Color = MarketFeedData.Green },
        };

        OpenPanel(e.SideLabel + " liquidation · " + MarketFeedData.Compact(e.NotionalUsd),
            e.Symbol + " · " + e.TimeLabel,
            rows,
            "Reported by the exchange, not estimated. Binance publishes the side of the liquidation order, which is the opposite of the position that blew up — a SELL order closes a long, so it is shown here as a LONG liquidation.",
            "CLOSE", CloseModal);
    }

    /// <summary>Status panel for the liquidation socket itself.</summary>
    private void OpenStreamPanel()
    {
        var stats = LiqStats;
        var rows = new List<KvRow>
        {
            new() { Label = "Stream", Value = _liqVm?.LiquidationStreamSource ?? "not connected", Color = _liqVm is null ? MarketFeedData.Faint : MarketFeedData.Green },
            new() { Label = "Connection", Value = LiqStreamStatus, Color = LiqStreamColor },
            new() { Label = "Window", Value = _liqVm?.LiquidationWindowLabel ?? "—", Color = MarketFeedData.Text3 },
            new() { Label = "Liquidated (window)", Value = stats.Events > 0 ? MarketFeedData.Compact(stats.TotalUsd) : "no events yet", Color = stats.Events > 0 ? MarketFeedData.Text : MarketFeedData.Faint },
            new() { Label = "Longs / shorts", Value = stats.Events > 0 ? MarketFeedData.Compact(stats.LongUsd) + " / " + MarketFeedData.Compact(stats.ShortUsd) : "—", Color = MarketFeedData.Text3 },
            new() { Label = _liqVm?.Symbol ?? "symbol", Value = (_liqVm?.SymbolLiquidationUsd ?? 0m) > 0m ? MarketFeedData.Compact(_liqVm!.SymbolLiquidationUsd) : "nothing on this symbol yet", Color = (_liqVm?.SymbolLiquidationUsd ?? 0m) > 0m ? MarketFeedData.Text : MarketFeedData.Faint },
            new() { Label = "Level map", Value = _liqVm?.DataSourceLabel ?? "—", Color = MarketFeedData.Amber },
        };
        if (!string.IsNullOrWhiteSpace(_liqVm?.LiquidationStreamError))
            rows.Add(new KvRow { Label = "Last error", Value = MarketFeedData.Trunc(_liqVm!.LiquidationStreamError, 46), Color = MarketFeedData.Red });

        OpenPanel("Liquidation stream", "public Binance · Bybit · OKX sockets · no API key",
            rows,
            "Liquidations are exchange-reported and read from three venues at once, so a venue that is blocked or silent on this network simply contributes nothing while the others keep feeding. A socket shown as “connected · no data” completed its handshake but has never delivered an event. Aggregates only cover what this session has received (the buffer keeps the last 500 events, up to 24 h) — nothing is back-filled. The level map above is a separate, estimated leverage model.",
            "CLOSE", CloseModal);
    }

    // ── RAIL ─────────────────────────────────────────────────────────────────

    private void RebuildRail(List<NewsItemRowVM> news, List<TapeRowVM> tape)
    {
        RailTitle = IsNews ? "AI DESK · NEWS" : IsTape ? "AI DESK · TAPE" : "AI DESK · LIQUIDATIONS";
        InsightBullets.Clear();

        if (IsNews)
        {
            InsightTitle = "NARRATIVE WATCH";
            InsightMeta = _newsVm is null ? "not connected" : _newsVm.IsLoading ? "loading…" : _newsVm.PulseDetail;
            InsightSignal = _newsVm is null || news.Count == 0 ? "No headlines yet" : _newsVm.PulseLabel + " · score " + _newsVm.PulseScore;
            InsightSignalColor = _newsVm?.PulseBrush ?? MarketFeedData.Faint;
            InsightSummary = news.Count == 0
                ? (_newsVm?.StatusLabel ?? "The desk is not connected to the news feed.")
                : $"{news.Count} headlines match the current filters, {news.Count(n => n.IsImportant)} of them flagged important. The pulse below is the bullish/bearish split of the last hour of headlines.";
            foreach (var n in news.Where(n => n.IsImportant).Take(3))
                InsightBullets.Add(new Bullet { Text = MarketFeedData.Trunc(n.Title, 78), Dot = n.SentimentBrush });
            if (InsightBullets.Count == 0)
                InsightBullets.Add(new Bullet { Text = news.Count == 0 ? "No data" : "No important headlines in this selection", Dot = MarketFeedData.Faint });
        }
        else if (IsTape)
        {
            InsightTitle = "ABSORPTION READ";
            InsightMeta = tape.Count > 0 ? tape.Count + " prints" : "no prints";
            InsightSignal = _flowSignal;
            InsightSignalColor = _flowColor;
            InsightSummary = _flowSummary;
            InsightBullets.Add(new Bullet { Text = _flowBullet, Dot = _flowDot });
            InsightBullets.Add(new Bullet { Text = _largeCount + " prints over " + ThresholdLabel(), Dot = _largeCount > 0 ? MarketFeedData.Amber : MarketFeedData.Faint });
            InsightBullets.Add(new Bullet { Text = _tapeVm?.PressureLabel ?? "no pressure data", Dot = _tapeVm?.PressureBrush ?? MarketFeedData.Faint });
        }
        else
        {
            InsightTitle = "MAGNET READ";
            InsightMeta = _liqVm is null ? "not connected" : _liqVm.InsightRunning ? "running…" : string.IsNullOrWhiteSpace(_liqVm.InsightSource) ? "not run yet" : _liqVm.InsightSource;
            InsightSignal = string.IsNullOrWhiteSpace(_liqVm?.InsightSignal) ? "Not analysed" : _liqVm!.InsightSignal.Replace('_', ' ');
            InsightSignalColor = _liqVm?.InsightSignalBrush ?? MarketFeedData.Faint;
            InsightSummary = string.IsNullOrWhiteSpace(_liqVm?.InsightSummary)
                ? "Press RE-ANALYSE to interpret the loaded liquidation map. Nothing is inferred until then."
                : _liqVm!.InsightSummary;
            foreach (var b in SplitBullets(_liqVm?.InsightBullets))
                InsightBullets.Add(new Bullet { Text = b, Dot = MarketFeedData.Accent });
            if (InsightBullets.Count == 0)
                InsightBullets.Add(new Bullet { Text = "No AI insight yet", Dot = MarketFeedData.Faint });
        }

        RebuildSide1(news, tape);
        RebuildSide2(news, tape);
    }

    private static IEnumerable<string> SplitBullets(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Enumerable.Empty<string>()
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(s => s.TrimStart('•', ' ').Trim())
                 .Where(s => s.Length > 0);

    private void RebuildSide1(List<NewsItemRowVM> news, List<TapeRowVM> tape)
    {
        Side1Rows.Clear();
        if (IsNews)
        {
            Side1Title = "COINS IN THE NEWS";
            Side1Meta = news.Count + " headlines";
            var counts = news
                .SelectMany(n => n.Item.Currencies)
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Coin = g.Key.ToUpperInvariant(), N = g.Count() })
                .OrderByDescending(x => x.N)
                .Take(6)
                .ToList();
            var max = Math.Max(1, counts.Count > 0 ? counts.Max(x => x.N) : 1);
            foreach (var c in counts)
            {
                var coin = c.Coin;
                Side1Rows.Add(new SideRow
                {
                    Label = coin, BarRatio = (double)c.N / max, BarColor = MarketFeedData.Accent,
                    Value = c.N + " items", ValueColor = MarketFeedData.Text3,
                    Command = new RelayCommand(() => { if (_newsVm is not null) { _newsVm.FilterSymbol = coin; RaiseInputs(); Recompute(); Toast("Filtered headlines to " + coin, "info"); } }),
                });
            }
        }
        else if (IsTape)
        {
            Side1Title = "PRINT SIZE BUCKETS";
            Side1Meta = tape.Count + " prints";
            var defs = new (string label, double lo, double hi, string col)[]
            {
                ("< $5k", 0, 5e3, MarketFeedData.Text3),
                ("$5–20k", 5e3, 2e4, MarketFeedData.Text3),
                ("$20–50k", 2e4, 5e4, MarketFeedData.Amber),
                ("$50–100k", 5e4, 1e5, MarketFeedData.Amber),
                ("> $100k", 1e5, double.PositiveInfinity, MarketFeedData.Red),
            };
            var usd = tape.Select(r => (double)r.Trade.QuoteQty).ToList();
            var counts = defs.Select(d => usd.Count(v => v >= d.lo && v < d.hi)).ToArray();
            var max = Math.Max(1, counts.Length > 0 ? counts.Max() : 1);
            for (int i = 0; i < defs.Length; i++)
            {
                var d = defs[i]; var n = counts[i]; var lc = d.label;
                Side1Rows.Add(new SideRow
                {
                    Label = d.label, BarRatio = (double)n / max,
                    BarColor = d.col == MarketFeedData.Red ? MarketFeedData.Red : d.col == MarketFeedData.Amber ? MarketFeedData.Amber : "#2a3f54",
                    Value = n + " prints", ValueColor = n > 0 ? d.col : MarketFeedData.Faint,
                    Command = new RelayCommand(() => Toast(n + " prints in the " + lc + " bucket", "info")),
                });
            }
        }
        else if (LiqFeed.Count > 0)
        {
            // Real prints take the rail over as soon as the socket delivers any.
            Side1Title = "LIVE LIQUIDATIONS · REAL";
            Side1Meta = "all symbols · " + (_liqVm?.LiquidationWindowLabel ?? "");
            var feed = LiqFeed.Take(8).ToList();
            var max = feed.Max(e => e.NotionalUsd);
            foreach (var e in feed)
            {
                var ev = e;
                var color = e.Side == LiquidationSide.Long ? MarketFeedData.Accent : MarketFeedData.Red;
                Side1Rows.Add(new SideRow
                {
                    Label = e.Symbol.Replace("USDT", "") + " " + e.SideLabel[..1],
                    BarRatio = max > 0m ? Math.Clamp((double)(e.NotionalUsd / max), 0.0, 1.0) : 0.0,
                    BarColor = color,
                    Value = MarketFeedData.Compact(e.NotionalUsd),
                    ValueColor = color,
                    Command = new RelayCommand(() => OpenLiquidation(ev)),
                });
            }
        }
        else
        {
            Side1Title = "CLUSTERS BY SIZE (EST.)";
            Side1Meta = _liqVm is null ? "not connected" : _liqVm.Symbol + " · " + LiqStreamStatus;
            var price = _liqVm?.CurrentPrice ?? 0m;
            var clusters = (_liqVm?.ClusterOverlay ?? Array.Empty<LiqClusterOverlay>())
                .OrderByDescending(c => c.NotionalUsd)
                .Take(6)
                .ToList();
            foreach (var c in clusters)
            {
                var dist = price > 0 ? (double)((c.Price / price - 1m) * 100m) : 0;
                var label = price > 0 ? (dist >= 0 ? "+" : "") + dist.ToString("F1", Inv) + "%" : MarketFeedData.Price(c.Price);
                var cc = c;
                Side1Rows.Add(new SideRow
                {
                    Label = label, BarRatio = Math.Clamp(c.Intensity, 0.0, 1.0), BarColor = c.Color,
                    Value = c.NotionalLabel, ValueColor = c.Color,
                    Command = new RelayCommand(() => Toast(cc.Side + " cluster " + cc.NotionalLabel + " @ " + MarketFeedData.Price(cc.Price), "info")),
                });
            }
        }
    }

    private void RebuildSide2(List<NewsItemRowVM> news, List<TapeRowVM> tape)
    {
        Side2Rows.Clear();
        if (IsNews)
        {
            Side2Title = "FEED STATUS";
            Side2Rows.Add(new KvRow { Label = "Feed", Value = _newsVm is null ? "not connected" : _newsVm.IsLoading ? "loading" : "live", Color = _newsVm is null ? MarketFeedData.Faint : _newsVm.IsLoading ? MarketFeedData.Amber : MarketFeedData.Green });
            Side2Rows.Add(new KvRow { Label = "Status", Value = _newsVm?.StatusLabel ?? "—", Color = MarketFeedData.Text3 });
            Side2Rows.Add(new KvRow { Label = "Shown / unread", Value = news.Count + " / " + (_newsVm?.UnreadCount ?? 0), Color = MarketFeedData.Text });
            Side2Rows.Add(new KvRow { Label = "Pulse", Value = _newsVm is null ? "—" : _newsVm.PulseLabel + " (" + _newsVm.PulseScore + ")", Color = _newsVm?.PulseBrush ?? MarketFeedData.Faint });
            Side2Rows.Add(new KvRow { Label = "Digest engine", Value = string.IsNullOrWhiteSpace(_newsVm?.AiDigestSource) ? "not run yet" : _newsVm!.AiDigestSource, Color = MarketFeedData.Text3 });
            Side2ActionLabel = "REFRESH DIGEST";
            Side2ActionCommand = _side2NewsAction;
        }
        else if (IsTape)
        {
            Side2Title = "CONNECTION";
            Side2Rows.Add(new KvRow { Label = "Venue", Value = _tapeVm?.Venue ?? "—", Color = MarketFeedData.Text });
            Side2Rows.Add(new KvRow { Label = "Subscription", Value = _tapeVm is null ? "—" : IsDex ? _tapeVm.Network + " · " + MarketFeedData.Trunc(_tapeVm.PoolAddress, 14) : _tapeVm.Symbol, Color = MarketFeedData.Text });
            Side2Rows.Add(new KvRow { Label = "Status", Value = _tapeVm?.StatusLabel ?? "—", Color = MarketFeedData.Text3 });
            Side2Rows.Add(new KvRow { Label = "Prints buffered", Value = tape.Count.ToString(), Color = tape.Count > 0 ? MarketFeedData.Text : MarketFeedData.Faint });
            Side2Rows.Add(new KvRow { Label = "Large threshold", Value = ThresholdLabel(), Color = MarketFeedData.Amber });
            Side2ActionLabel = "RECONNECT STREAM";
            Side2ActionCommand = _side2TapeAction;
        }
        else
        {
            var stats = LiqStats;
            Side2Title = "DATA SOURCE";
            Side2Rows.Add(new KvRow { Label = "Level map", Value = _liqVm?.DataSourceLabel ?? "not connected", Color = _liqVm?.DataSourceLabel == "CoinGlass" ? MarketFeedData.Green : MarketFeedData.Amber });
            Side2Rows.Add(new KvRow { Label = "Symbol", Value = _liqVm?.Symbol ?? "—", Color = MarketFeedData.Text });
            Side2Rows.Add(new KvRow { Label = "Map status", Value = _liqVm?.StatusLabel ?? "—", Color = MarketFeedData.Text3 });
            Side2Rows.Add(new KvRow { Label = "Bands shown", Value = LiqBands.Count + " of " + (_liqVm?.HeatBands.Count ?? 0), Color = MarketFeedData.Text3 });
            Side2Rows.Add(new KvRow { Label = "Liquidation stream", Value = LiqStreamStatus, Color = LiqStreamColor });
            foreach (var v in _liqVm?.StreamVenues ?? Array.Empty<LiquidationVenueStatus>())
                Side2Rows.Add(new KvRow
                {
                    Label = "· " + v.VenueLabel,
                    Value = v.Label,
                    // A socket that is open but has never delivered is amber, not green: on some
                    // networks a venue's futures host connects and then forwards nothing.
                    Color = v.State == LiquidationStreamState.Connected && !v.Silent ? MarketFeedData.Green
                          : v.State == LiquidationStreamState.Stopped ? MarketFeedData.Faint
                          : MarketFeedData.Amber,
                });
            Side2Rows.Add(new KvRow { Label = "Stream window", Value = _liqVm?.LiquidationWindowLabel ?? "—", Color = MarketFeedData.Text3 });
            Side2Rows.Add(new KvRow
            {
                Label = "Liquidated (real)",
                Value = stats.Events > 0 ? MarketFeedData.Compact(stats.TotalUsd) : "nothing yet",
                Color = stats.Events > 0 ? MarketFeedData.Text : MarketFeedData.Faint,
            });
            if (!string.IsNullOrWhiteSpace(_liqVm?.LiquidationStreamError))
                Side2Rows.Add(new KvRow { Label = "Stream error", Value = MarketFeedData.Trunc(_liqVm!.LiquidationStreamError, 26), Color = MarketFeedData.Red });
            Side2ActionLabel = "RELOAD HEATMAP";
            Side2ActionCommand = _side2LiqAction;
        }
    }

    private void RebuildFooter(List<NewsItemRowVM> news, List<TapeRowVM> tape)
    {
        if (!Live)
        {
            FooterState = "not connected"; FooterColor = MarketFeedData.Faint;
            FooterF1 = "desk not attached to the shell"; FooterF2 = "no sources"; FooterF3 = "no data";
            return;
        }

        if (IsNews)
        {
            FooterState = _newsVm is null ? "no feed" : _newsVm.IsLoading ? "loading feeds" : "rss feeds live";
            FooterColor = _newsVm is null ? MarketFeedData.Faint : _newsVm.IsLoading ? MarketFeedData.Amber : MarketFeedData.Green;
            FooterF1 = _newsVm?.StatusLabel ?? "no data";
            FooterF2 = news.Count + " shown";
            FooterF3 = string.IsNullOrWhiteSpace(_newsVm?.AiDigestSource) ? "digest not run" : "digest: " + _newsVm!.AiDigestSource;
        }
        else if (IsTape)
        {
            FooterState = _streaming ? "tape streaming" : "tape paused";
            FooterColor = _streaming ? MarketFeedData.Green : MarketFeedData.Amber;
            FooterF1 = _tapeVm?.StatusLabel ?? "no data";
            FooterF2 = tape.Count + " prints buffered";
            FooterF3 = "threshold " + ThresholdLabel();
        }
        else
        {
            var streamStats = LiqStats;
            FooterState = "stream " + LiqStreamStatus;
            FooterColor = LiqStreamColor;
            FooterF1 = "map: " + (_liqVm?.DataSourceLabel ?? "no source");
            FooterF2 = LiqBands.Count + " bands";
            FooterF3 = streamStats.Events > 0
                ? MarketFeedData.Compact(streamStats.TotalUsd) + " liquidated · " + (_liqVm?.Symbol ?? "—")
                : (_liqVm?.Symbol ?? "—");
        }
    }

    // ── pulse / panel / toast ────────────────────────────────────────────────

    private void OpenPulse()
    {
        var rows = new List<KvRow>();

        if (_newsVm is not null)
        {
            rows.Add(new KvRow { Label = "News pulse", Value = _newsVm.PulseLabel + " (" + _newsVm.PulseScore + ")", Color = _newsVm.PulseBrush });
            rows.Add(new KvRow { Label = "Last hour", Value = _newsVm.PulseDetail, Color = MarketFeedData.Text3 });
        }
        else rows.Add(new KvRow { Label = "News pulse", Value = "not connected", Color = MarketFeedData.Faint });

        if (_sentimentVm is not null)
        {
            rows.Add(new KvRow
            {
                Label = "Fear & greed",
                Value = _sentimentVm.FearGreedValue > 0 ? _sentimentVm.FearGreedValue + " · " + _sentimentVm.FearGreedLabel : "no data",
                Color = _sentimentVm.FearGreedValue > 0 ? MarketFeedData.Amber : MarketFeedData.Faint,
            });
            rows.Add(new KvRow
            {
                Label = "Open interest",
                Value = _sentimentVm.OpenInterest > 0 ? _sentimentVm.OpenInterestLabel + " · " + _sentimentVm.OpenInterestChangeLabel : "no data",
                Color = _sentimentVm.OpenInterest > 0 ? MarketFeedData.Text : MarketFeedData.Faint,
            });
        }

        rows.Add(new KvRow
        {
            Label = "Tape pressure",
            Value = _tapeCount > 0 ? _pressurePct + "% buys of " + _tapeCount + " prints" : "no prints buffered",
            Color = _tapeCount == 0 ? MarketFeedData.Faint : _pressurePct > 50 ? MarketFeedData.Green : MarketFeedData.Red,
        });

        var (shortAbove, longBelow) = LiqSkew();
        rows.Add(new KvRow
        {
            Label = "Liquidation skew",
            Value = shortAbove + longBelow > 0
                ? MarketFeedData.Compact(shortAbove) + " above / " + MarketFeedData.Compact(longBelow) + " below"
                : "no clusters loaded",
            Color = shortAbove + longBelow > 0 ? MarketFeedData.Amber : MarketFeedData.Faint,
        });
        rows.Add(new KvRow { Label = "Liquidation map", Value = (_liqVm?.DataSourceLabel ?? "not connected") + " — estimated", Color = MarketFeedData.Text3 });

        var liqStats = LiqStats;
        rows.Add(new KvRow
        {
            Label = "Liquidations (real stream)",
            Value = liqStats.Events > 0
                ? MarketFeedData.Compact(liqStats.TotalUsd) + " over " + (_liqVm?.LiquidationWindowLabel ?? "")
                : "stream " + LiqStreamStatus,
            Color = liqStats.Events > 0 ? MarketFeedData.Green : MarketFeedData.Faint,
        });

        OpenPanel("Market pulse", "live blend across news, sentiment, tape and liquidations", rows,
            "Every line above is read straight from a live source. Blank lines mean that source has not delivered data yet — nothing here is filled in with samples.",
            "CLOSE", CloseModal);
    }

    private void OpenPanel(string title, string sub, IEnumerable<KvRow> rows, string note, string actionLabel, Action onAction)
    {
        PanelTitle = title; PanelSub = sub; PanelNote = note; PanelActionLabel = actionLabel;
        PanelActionCommand = new RelayCommand(onAction);
        PanelRows.Clear();
        foreach (var r in rows) PanelRows.Add(r);
        _modal = "panel";
        foreach (var n in new[] { nameof(PanelTitle), nameof(PanelSub), nameof(PanelNote), nameof(PanelActionLabel), nameof(PanelActionCommand), nameof(ModalPanel) })
            this.RaisePropertyChanged(n);
    }

    private void CloseModal() { _modal = null; this.RaisePropertyChanged(nameof(ModalPanel)); }

    private void Toast(string msg, string kind = "ok")
    {
        (string color, string icon) = kind switch
        {
            "ok" => (MarketFeedData.Green, "✓"),
            "warn" => (MarketFeedData.Amber, "!"),
            "bad" => (MarketFeedData.Red, "✕"),
            "ai" => (MarketFeedData.Accent, "✦"),
            _ => (MarketFeedData.Accent, "›"),
        };
        ToastMsg = msg; ToastColor = color; ToastIcon = icon; ToastBorder = MarketFeedData.Alpha(color, "55"); ToastMeta = Now();
        foreach (var n in new[] { nameof(ToastMsg), nameof(ToastColor), nameof(ToastIcon), nameof(ToastBorder), nameof(ToastMeta) })
            this.RaisePropertyChanged(n);
        HasToast = true; _toastTimer.Stop(); _toastTimer.Start();
    }

    public void Dispose()
    {
        _toastTimer.Stop();
        _coalesce.Stop();
        Detach();
    }
}
