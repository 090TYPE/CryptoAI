using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.AnalyticsDesk;

/// <summary>
/// The Analytics tab. After <see cref="Attach"/> every number is computed from the
/// live <see cref="TradeRecord"/>s behind the P&amp;L dashboard; before that the desk
/// renders an empty state rather than sample rows.
/// </summary>
public sealed class AnalyticsDeskViewModel : ReactiveObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly DispatcherTimer _toastTimer;

    // ── live sources ─────────────────────────────────────────────────────────
    private MainWindowViewModel? _host;
    private PnlDashboardViewModel? _pnl;
    private TradeJournalViewModel? _journal;

    public AnalyticsDeskViewModel()
    {
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HasToast = false; };

        RefreshCommand = new RelayCommand(RunRefresh);
        ExportCommand = new RelayCommand(RunExport);
        AiReviewCommand = new RelayCommand(RunAi);
        BenchmarkCommand = new RelayCommand(OpenBenchmark);
        DeepCommand = new RelayCommand(OpenDeep);
        CoachCommand = new RelayCommand(RunAi);
        OpenJournalCommand = new RelayCommand(OpenJournal);
        CloseModalCommand = new RelayCommand(() => { _modal = null; this.RaisePropertyChanged(nameof(ModalPanel)); });

        Recompute();
    }

    // ═════════════════════════ attach ═══════════════════════════════════════

    /// <summary>Hands the desk its live sources and subscribes for updates.</summary>
    public void Attach(MainWindowViewModel host)
    {
        if (host is null) return;
        Detach();

        _host = host;
        _pnl = host.PnlDashboardVM;
        _journal = host.TradeJournalVM;

        if (_pnl is not null)
        {
            _sources = _pnl.AvailableSources.ToArray();
            _source = _pnl.SelectedSource;
            _period = _pnl.SelectedPeriod;
            _pnl.PropertyChanged += OnPnlChanged;
            _pnl.TradeRows.CollectionChanged += OnRowsChanged;
            _pnl.Refresh();
        }

        if (_journal is not null) _journal.PropertyChanged += OnJournalChanged;

        Recompute();
    }

    private void Detach()
    {
        if (_pnl is not null)
        {
            _pnl.PropertyChanged -= OnPnlChanged;
            _pnl.TradeRows.CollectionChanged -= OnRowsChanged;
        }
        if (_journal is not null) _journal.PropertyChanged -= OnJournalChanged;
        _pnl = null; _journal = null; _host = null;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRecompute();

    private void OnPnlChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_pnl is null) return;
        if (e.PropertyName == nameof(PnlDashboardViewModel.SelectedPeriod)) _period = _pnl.SelectedPeriod;
        if (e.PropertyName == nameof(PnlDashboardViewModel.SelectedSource)) _source = _pnl.SelectedSource;
        QueueRecompute();
    }

    private void OnJournalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TradeJournalViewModel.HasCoachReview) && _journal?.HasCoachReview == true)
            _reviewedAt = DateTime.Now;
        QueueRecompute();
    }

    private bool _queued;
    private void QueueRecompute()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Recompute(); }, DispatcherPriority.Background);
    }

    // ── state ────────────────────────────────────────────────────────────────
    private string _period = "All", _source = "All", _sortKey = "date", _search = "", _sideFilter = "all";
    private int _sortDir = -1;
    private bool _showEquity = true, _showHold = true, _showDrawdown = true;
    private readonly Dictionary<string, string> _bdSort = new() { ["source"] = "pnl", ["asset"] = "pnl", ["day"] = "pnl" };
    private string? _selRow, _selBd;
    private string[] _sources = Array.Empty<string>();
    private DateTime? _lastRead, _reviewedAt;

    /// <summary>Trades currently published by the dashboard, newest first.</summary>
    private List<TradeRecord> _trades = new();
    private List<double> _equity = new();      // cumulative realized P&L, oldest first, opens at 0
    private double? _holdNet;                  // buy and hold benchmark over the same trades

    public string Search { get => _search; set { this.RaiseAndSetIfChanged(ref _search, value ?? ""); Recompute(); } }
    public string[] Sources => _sources;

    public string Source
    {
        get => _source;
        set
        {
            var v = string.IsNullOrEmpty(value) ? _source : value;
            this.RaiseAndSetIfChanged(ref _source, v);
            _selBd = null;
            if (_pnl is not null && _pnl.SelectedSource != v) _pnl.SelectedSource = v; // drives the real filter
            else Recompute();
        }
    }

    // ── commands ─────────────────────────────────────────────────────────────
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand AiReviewCommand { get; }
    public ICommand BenchmarkCommand { get; }
    public ICommand DeepCommand { get; }
    public ICommand CoachCommand { get; }
    public ICommand OpenJournalCommand { get; }
    public ICommand CloseModalCommand { get; }

    // ── collections ──────────────────────────────────────────────────────────
    public ObservableCollection<PeriodChip> Periods { get; } = new();
    public ObservableCollection<TickerItem> Ticker { get; } = new();
    public ObservableCollection<AnKpi> Kpis { get; } = new();
    public ObservableCollection<EqToggle> EqToggles { get; } = new();
    public ObservableCollection<EqMark> EqMarks { get; } = new();
    public ObservableCollection<BreakdownPanel> Breakdowns { get; } = new();
    public ObservableCollection<ColHeader> ThCols { get; } = new();
    public ObservableCollection<FilterChip> ThFilters { get; } = new();
    public ObservableCollection<AnTradeRow> ThRows { get; } = new();
    public ObservableCollection<Bullet> ReviewFindings { get; } = new();
    public ObservableCollection<Ratio> Ratios { get; } = new();
    public ObservableCollection<DistBar> DistBars { get; } = new();
    public ObservableCollection<KvRow> Habits { get; } = new();
    public ObservableCollection<KvRow> PanelRows { get; } = new();

    // ── header scalars ───────────────────────────────────────────────────────
    public string HdrPeriodLabel { get; private set; } = "";
    public string HdrPnl { get; private set; } = "";
    public string HdrPnlColor { get; private set; } = AnalyticsData.Dim;
    public string HdrPnlSub { get; private set; } = "";
    public string HdrTrades { get; private set; } = "";
    public string HdrTradesSub { get; private set; } = "";
    public string HdrHold { get; private set; } = "";
    public string HdrHoldColor { get; private set; } = AnalyticsData.Dim;
    public double HdrHoldRatio { get; private set; }
    public string AiBtnLabel => _journal?.CoachRunning == true ? "✦ ANALYSING…" : "✦ AI REVIEW";
    public string AiProvider => string.IsNullOrWhiteSpace(_journal?.CoachSource) ? AnalyticsData.Empty : _journal!.CoachSource.ToUpperInvariant();

    // ── equity chart ─────────────────────────────────────────────────────────
    public List<Point> EqPoints { get; private set; } = new();
    public List<Point> HoldPoints { get; private set; } = new();
    public List<Point> DdPoints { get; private set; } = new();
    public bool ShowEquity => _showEquity;
    public bool ShowHold => _showHold;
    public bool ShowDrawdown => _showDrawdown;
    public string EqMaxLabel { get; private set; } = "";
    public string EqMinLabel { get; private set; } = "";
    public string EqHoldDelta { get; private set; } = "";
    public string EqAxisTop { get; private set; } = "";
    public string EqAxisBottom { get; private set; } = "";

    // ── review / footer ──────────────────────────────────────────────────────
    public string ReviewMeta => _journal?.CoachRunning == true ? "analysing…"
        : _reviewedAt.HasValue ? "reviewed " + _reviewedAt.Value.ToString("HH:mm:ss", Inv) : "not run yet";
    public string ReviewSummary { get; private set; } = "";
    public string ThMeta { get; private set; } = "";
    public bool ThEmpty => ThRows.Count == 0;
    public string DistMeta { get; private set; } = "";
    public string DistLeft { get; private set; } = AnalyticsData.Empty;
    public string DistRight { get; private set; } = AnalyticsData.Empty;
    public string FooterRows { get; private set; } = "";
    public string FooterPeriod { get; private set; } = "";
    public string FooterSource { get; private set; } = "";
    public string FooterFees { get; private set; } = "";
    public string FooterSync => _lastRead.HasValue ? "last read " + _lastRead.Value.ToString("HH:mm:ss", Inv) : "not read yet";

    // ═════════════════════════ record helpers ═══════════════════════════════

    private static double Pnl(TradeRecord t) => (double)t.PnlUsd;
    private static double PctOf(TradeRecord t) => (double)t.PnlPercent;
    private static string DateOf(TradeRecord t) => t.ClosedAtUtc.ToLocalTime().ToString("dd MMM HH:mm", Inv);
    private static string SideOf(TradeRecord t) => t.Direction == TradeDirection.Long ? "LONG" : "SHORT";
    private static string SourceOf(TradeRecord t) => string.IsNullOrWhiteSpace(t.BotName) ? t.Source.ToString() : t.BotName!;

    /// <summary>Holding time, or null when the record has no usable open timestamp.</summary>
    private static TimeSpan? HoldOf(TradeRecord t)
    {
        if (t.OpenedAtUtc == default || t.ClosedAtUtc <= t.OpenedAtUtc) return null;
        return t.ClosedAtUtc - t.OpenedAtUtc;
    }

    private sealed record DayBucket(DateTime Date, string Label, int Trades, int Wins, double Pnl);

    /// <summary>Closed trades bucketed by UTC close date, newest day first.</summary>
    private List<DayBucket> Days() => _trades
        .GroupBy(t => t.ClosedAtUtc.Date)
        .OrderByDescending(g => g.Key)
        .Select(g => new DayBucket(g.Key, g.Key.ToString("dd MMM", Inv), g.Count(), g.Count(t => t.IsWin), g.Sum(Pnl)))
        .ToList();

    // ═════════════════════════ snapshot ═════════════════════════════════════

    private void ReadSnapshot()
    {
        _trades = new List<TradeRecord>();
        _equity = new List<double>();
        _holdNet = null;
        if (_pnl is null) return;

        foreach (var r in _pnl.TradeRows) _trades.Add(r.Model);

        if (_trades.Count > 0)
        {
            // The dashboard hands us the most recent first; walk it backwards.
            double cum = 0;
            _equity.Add(0);
            for (int k = _trades.Count - 1; k >= 0; k--) { cum += Pnl(_trades[k]); _equity.Add(cum); }
        }

        _holdNet = ComputeHoldBenchmark(_trades);
    }

    /// <summary>
    /// Buy and hold on the most-traded symbol: the same notional entered at that
    /// symbol's first entry price and exited at its last exit price. Null when the
    /// records carry no usable prices or quantities.
    /// </summary>
    private static double? ComputeHoldBenchmark(IReadOnlyList<TradeRecord> trades)
    {
        var top = trades
            .Where(t => t.EntryPrice > 0m && t.ExitPrice > 0m && t.Quantity > 0m)
            .GroupBy(t => t.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (top is null) return null;

        var sorted = top.OrderBy(t => t.OpenedAtUtc).ToList();
        decimal firstEntry = sorted[0].EntryPrice, lastExit = sorted[^1].ExitPrice;
        if (firstEntry <= 0m) return null;

        decimal notional = sorted.Sum(t => t.EntryPrice * t.Quantity);
        return (double)(notional / firstEntry * (lastExit - firstEntry));
    }

    /// <summary>Most-traded symbol that carries prices, used by the hold benchmark.</summary>
    private (string symbol, int trades)? HoldSymbol()
    {
        var top = _trades
            .Where(t => t.EntryPrice > 0m && t.ExitPrice > 0m && t.Quantity > 0m)
            .GroupBy(t => t.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        return top is null ? null : (top.Key, top.Count());
    }

    // ═════════════════════════ recompute ════════════════════════════════════

    private void Recompute()
    {
        _lastRead = _pnl is null ? null : DateTime.Now;
        ReadSnapshot();

        RebuildPeriods();
        RebuildHeader();
        RebuildTicker();
        RebuildKpis();
        RebuildEquity();
        RebuildBreakdowns();
        RebuildTradeHistory();
        RebuildReview();
        RebuildRatios();
        RebuildDist();
        RebuildHabits();
        RebuildFooter();

        this.RaisePropertyChanged(string.Empty);
    }

    private void RebuildPeriods()
    {
        Periods.Clear();
        if (_pnl is null) return;
        foreach (var (id, label) in AnalyticsData.Periods)
        {
            var key = id; var on = _period == id;
            Periods.Add(new PeriodChip
            {
                Label = label,
                Bg = on ? "#0e2a2a" : "transparent",
                Fg = on ? AnalyticsData.Accent : AnalyticsData.Dimmer,
                Command = new RelayCommand(() => SetPeriod(key)),
            });
        }
    }

    private void SetPeriod(string id)
    {
        _selRow = null;
        _period = id;
        if (_pnl is not null && _pnl.SelectedPeriod != id) _pnl.SelectedPeriod = id; // drives the real filter
        else Recompute();
    }

    private void RebuildHeader()
    {
        HdrPeriodLabel = AnalyticsData.Periods.FirstOrDefault(p => p.id == _period).label ?? _period;

        if (_pnl is null)
        {
            HdrPnl = AnalyticsData.Empty; HdrPnlColor = AnalyticsData.Dim; HdrPnlSub = "not connected";
            HdrTrades = AnalyticsData.Empty; HdrTradesSub = "";
            HdrHold = AnalyticsData.Empty; HdrHoldColor = AnalyticsData.Dim; HdrHoldRatio = 0;
            return;
        }

        // Ready-made KPI labels stay as the dashboard formatted them.
        HdrPnl = _pnl.TotalPnlLabel;
        HdrPnlColor = _pnl.TotalPnlBrush;
        HdrPnlSub = _pnl.WinRateLabel == "--" ? AnalyticsData.Empty : _pnl.WinRateLabel + " win rate";

        int wins = _trades.Count(t => t.IsWin);
        HdrTrades = _trades.Count.ToString(Inv);
        HdrTradesSub = _trades.Count == 0 ? "" : wins + "W / " + (_trades.Count - wins) + "L";

        double net = Net();
        if (_holdNet is double hold)
        {
            double diff = net - hold;
            HdrHold = AnalyticsData.Money(diff, true) + " vs hold";
            HdrHoldColor = AnalyticsData.Sgn(diff);
            HdrHoldRatio = Math.Abs(hold) < 1e-9 ? 0 : Math.Clamp(Math.Abs(net) / (Math.Abs(hold) * 2), 0, 1);
        }
        else
        {
            HdrHold = AnalyticsData.Empty + " vs hold";
            HdrHoldColor = AnalyticsData.Dim;
            HdrHoldRatio = 0;
        }
    }

    private double Net() => _equity.Count > 0 ? _equity[^1] : 0;

    private void RebuildTicker()
    {
        Ticker.Clear();
        if (_trades.Count == 0) return;

        var best = _trades.OrderByDescending(Pnl).First();
        var worst = _trades.OrderBy(Pnl).First();
        Ticker.Add(new TickerItem { Tag = "BEST", Text = best.Symbol + " · " + SourceOf(best), Val = AnalyticsData.Money(Pnl(best), true), Color = AnalyticsData.Sgn(Pnl(best)) });
        Ticker.Add(new TickerItem { Tag = "WORST", Text = worst.Symbol + " · " + SourceOf(worst), Val = AnalyticsData.Money(Pnl(worst), true), Color = AnalyticsData.Sgn(Pnl(worst)) });

        var (len, win) = CurrentStreak();
        if (len > 0)
            Ticker.Add(new TickerItem { Tag = "STREAK", Text = "current run", Val = StreakLabel(len, win), Color = win ? AnalyticsData.Green : AnalyticsData.Red });
    }

    private static string StreakLabel(int len, bool win)
        => len + (win ? (len == 1 ? " win" : " wins") : (len == 1 ? " loss" : " losses"));

    // ── KPIs ─────────────────────────────────────────────────────────────────

    private void RebuildKpis()
    {
        Kpis.Clear();
        if (_pnl is null) return;
        var pnl = _pnl;   // captured by the tile commands

        double gross = _trades.Where(t => t.IsWin).Sum(Pnl);
        double grossLoss = Math.Abs(_trades.Where(t => !t.IsWin).Sum(Pnl));
        double net = Net();
        int n = _trades.Count;

        Kpis.Add(new AnKpi
        {
            Label = "NET P&L", Value = pnl.TotalPnlLabel, Sub = pnl.TradeCountLabel, Color = pnl.TotalPnlBrush,
            Command = new RelayCommand(() => OpenPanel("Net P&L", "period " + _period.ToLowerInvariant(), n == 0 ? NoRows() : new[]
            {
                new KvRow { Label = "Gross profit", Value = AnalyticsData.Money(gross, true), Color = AnalyticsData.Green },
                new KvRow { Label = "Gross loss", Value = AnalyticsData.Money(-grossLoss, true), Color = AnalyticsData.Red },
                new KvRow { Label = "Net", Value = AnalyticsData.Money(net, true), Color = AnalyticsData.Sgn(net) },
                new KvRow { Label = "Expectancy / trade", Value = AnalyticsData.Money(net / n, true), Color = AnalyticsData.Sgn(net) },
                new KvRow { Label = "Trades", Value = pnl.TradeCountLabel, Color = AnalyticsData.Text },
            }, n == 0 ? "No closed trades in this period." : "Summed from the recorded closed trades — no fee model is applied on top.")),
        });

        Kpis.Add(new AnKpi
        {
            Label = "WIN RATE", Value = pnl.WinRateLabel, Sub = pnl.TradeCountLabel, Color = pnl.WinRateLabel == "--" ? AnalyticsData.Dim : AnalyticsData.Accent,
            Command = new RelayCommand(() => { _sideFilter = "win"; Recompute(); Toast("Trade history filtered to winners", "info"); }),
        });

        Kpis.Add(new AnKpi
        {
            Label = "AVG WIN", Value = pnl.AvgWinLabel, Sub = "per winning trade", Color = pnl.AvgWinLabel == "--" ? AnalyticsData.Dim : AnalyticsData.Green,
            Command = new RelayCommand(() => { _sortKey = "pnl"; _sortDir = -1; Recompute(); Toast("Sorted by P&L, best first", "info"); }),
        });

        Kpis.Add(new AnKpi
        {
            Label = "AVG LOSS", Value = pnl.AvgLossLabel, Sub = "per losing trade", Color = pnl.AvgLossLabel == "--" ? AnalyticsData.Dim : AnalyticsData.Red,
            Command = new RelayCommand(() => { _sortKey = "pnl"; _sortDir = 1; _sideFilter = "loss"; Recompute(); Toast("Showing losses, worst first", "warn"); }),
        });

        double ddUsd = MaxDrawdownUsd(out int peakIdx, out int troughIdx);
        Kpis.Add(new AnKpi
        {
            Label = "MAX DRAWDOWN", Value = pnl.MaxDrawdownLabel, Sub = "peak to trough", Color = pnl.MaxDrawdownLabel == "--" ? AnalyticsData.Dim : AnalyticsData.Amber,
            Command = new RelayCommand(() => OpenPanel("Max drawdown", "rolling peak model", ddUsd <= 0 ? NoRows() : new[]
            {
                new KvRow { Label = "Depth", Value = pnl.MaxDrawdownLabel, Color = AnalyticsData.Amber },
                new KvRow { Label = "Depth (USD)", Value = AnalyticsData.Money(-ddUsd, true), Color = AnalyticsData.Red },
                new KvRow { Label = "Peak at", Value = EquityDate(peakIdx), Color = AnalyticsData.Text3 },
                new KvRow { Label = "Trough at", Value = EquityDate(troughIdx), Color = AnalyticsData.Text3 },
                new KvRow { Label = "Trades in drawdown", Value = Math.Max(0, troughIdx - peakIdx).ToString(Inv), Color = AnalyticsData.Text },
            }, ddUsd <= 0 ? "No drawdown recorded in this period." : "Measured from the rolling peak of the realized equity curve.")),
        });

        double payoff = Payoff();
        Kpis.Add(new AnKpi
        {
            Label = "PROFIT FACTOR", Value = pnl.ProfitFactorLabel, Sub = "gross win / gross loss", Color = pnl.ProfitFactorLabel == "--" ? AnalyticsData.Dim : AnalyticsData.Violet,
            Command = new RelayCommand(() => OpenPanel("Profit factor", "gross profit ÷ gross loss", n == 0 ? NoRows() : new[]
            {
                new KvRow { Label = "Gross profit", Value = AnalyticsData.Money(gross, true), Color = AnalyticsData.Green },
                new KvRow { Label = "Gross loss", Value = AnalyticsData.Money(-grossLoss, true), Color = AnalyticsData.Red },
                new KvRow { Label = "Profit factor", Value = grossLoss > 0 ? (gross / grossLoss).ToString("0.00", Inv) : AnalyticsData.Empty, Color = AnalyticsData.Violet },
                new KvRow { Label = "Expectancy / trade", Value = AnalyticsData.Money(net / n, true), Color = AnalyticsData.Sgn(net) },
                new KvRow { Label = "Payoff ratio", Value = payoff > 0 ? payoff.ToString("0.00", Inv) : AnalyticsData.Empty, Color = AnalyticsData.Text },
            }, n == 0 ? "No closed trades in this period." : "Gross profit divided by gross loss over the closed trades in the current filter.")),
        });
    }

    private static KvRow[] NoRows() => new[] { new KvRow { Label = "Trades", Value = "0", Color = AnalyticsData.Dim } };

    // ── equity chart ─────────────────────────────────────────────────────────

    private void RebuildEquity()
    {
        EqPoints = new List<Point>();
        HoldPoints = new List<Point>();
        DdPoints = new List<Point>();
        EqMaxLabel = EqMinLabel = EqHoldDelta = EqAxisTop = EqAxisBottom = "";
        EqMarks.Clear();
        RebuildEqToggles();

        if (_pnl is null || _equity.Count < 2) return;

        double eqMin = _equity.Min(), eqMax = _equity.Max();
        double lo = eqMin, hi = eqMax;
        if (_holdNet is double h) { lo = Math.Min(lo, h); hi = Math.Max(hi, h); }

        EqPoints = Curve(_equity, lo, hi);
        DdPoints = Band(_equity, lo, hi);
        if (_holdNet is double hv)
        {
            HoldPoints = new List<Point> { Pt(0, Y(0, lo, hi)), Pt(1000, Y(hv, lo, hi)) };
            EqHoldDelta = "hold " + AnalyticsData.Money(hv, true);
        }

        EqMaxLabel = "peak " + AnalyticsData.Money(eqMax, true);
        EqMinLabel = "trough " + AnalyticsData.Money(eqMin, true);
        EqAxisTop = AnalyticsData.Money(eqMax, true);
        EqAxisBottom = AnalyticsData.Money(eqMin, true);

        RebuildEqMarks();
    }

    // Canvas is 1000 × 168 with the baseline at y=154 (see AnalyticsView.axaml).
    private static double Y(double v, double lo, double hi)
    {
        double r = hi - lo; if (r <= 0) r = 1;
        return Math.Round(154 - (v - lo) / r * 140, 1);
    }

    private static Point Pt(double x, double y) => new(Math.Round(x, 1), y);

    private static List<Point> Curve(IReadOnlyList<double> arr, double lo, double hi)
    {
        var pts = new List<Point>(arr.Count);
        int n = arr.Count;
        for (int i = 0; i < n; i++) pts.Add(Pt(n == 1 ? 0 : i * (1000.0 / (n - 1)), Y(arr[i], lo, hi)));
        return pts;
    }

    /// <summary>Closed polygon between the rolling peak and the equity line.</summary>
    private static List<Point> Band(IReadOnlyList<double> arr, double lo, double hi)
    {
        var pts = new List<Point>();
        int n = arr.Count;
        if (n < 2) return pts;

        var peak = new double[n];
        double p = arr[0];
        for (int i = 0; i < n; i++) { if (arr[i] > p) p = arr[i]; peak[i] = p; }
        if (!peak.Where((t, i) => t - arr[i] > 1e-9).Any()) return pts;   // never below peak

        for (int i = 0; i < n; i++) pts.Add(Pt(i * (1000.0 / (n - 1)), Y(arr[i], lo, hi)));
        for (int i = n - 1; i >= 0; i--) pts.Add(Pt(i * (1000.0 / (n - 1)), Y(peak[i], lo, hi)));
        return pts;
    }

    private void RebuildEqToggles()
    {
        EqToggles.Clear();
        void T(string key, string label, string sw, bool on)
            => EqToggles.Add(new EqToggle
            {
                Label = label, Swatch = on ? sw : "#152233", Fg = on ? AnalyticsData.Text : AnalyticsData.Faint,
                Border = on ? "#152233" : "#0d1b27", Bg = on ? "#08131d" : "transparent",
                Command = new RelayCommand(() =>
                {
                    if (key == "eq") _showEquity = !_showEquity;
                    else if (key == "hold") _showHold = !_showHold;
                    else _showDrawdown = !_showDrawdown;
                    Recompute();
                }),
            });
        T("eq", "ACTIVE", AnalyticsData.Accent, _showEquity);
        T("hold", "HOLD", AnalyticsData.Dimmer, _showHold);
        T("dd", "DRAWDOWN", AnalyticsData.Red, _showDrawdown);
    }

    private void RebuildEqMarks()
    {
        EqMarks.Clear();
        foreach (var d in Days().Take(7).Reverse())
        {
            var day = d;
            EqMarks.Add(new EqMark
            {
                Label = day.Label, Value = AnalyticsData.Money(day.Pnl, true), Color = AnalyticsData.Sgn(day.Pnl),
                Command = new RelayCommand(() => Toast(
                    day.Label + " · " + AnalyticsData.Money(day.Pnl, true) + " · " + day.Trades + " trades · " + day.Wins + "W", "info")),
            });
        }
    }

    // ── breakdowns ───────────────────────────────────────────────────────────

    private readonly record struct Bd(string Label, double Pnl, int Trades, int Wins);

    private void RebuildBreakdowns()
    {
        Breakdowns.Clear();
        if (_pnl is null) return;

        Breakdowns.Add(MkBd("source", "P&L BY SOURCE", "SOURCE", Group(SourceOf)));
        Breakdowns.Add(MkBd("asset", "P&L BY ASSET", "ASSET", Group(t => t.Asset)));
        Breakdowns.Add(MkBd("day", "P&L BY DAY", "DAY", Days().Select(d => new Bd(d.Label, d.Pnl, d.Trades, d.Wins))));
    }

    private IEnumerable<Bd> Group(Func<TradeRecord, string> key) => _trades
        .GroupBy(key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new Bd(g.Key, g.Sum(Pnl), g.Count(), g.Count(t => t.IsWin)));

    private BreakdownPanel MkBd(string id, string title, string col1, IEnumerable<Bd> source)
    {
        var sortKey = _bdSort[id];
        var rows = (sortKey == "pnl"
            ? source.OrderByDescending(r => Math.Abs(r.Pnl))
            : source.OrderByDescending(r => r.Trades)).ToList();

        double max = Math.Max(1e-9, rows.Select(r => Math.Abs(r.Pnl)).DefaultIfEmpty(0).Max());

        var panel = new BreakdownPanel
        {
            Title = title, Col1 = col1,
            SortLabel = sortKey == "pnl" ? "by P&L ↓" : "by trades ↓",
            SortCommand = new RelayCommand(() => { _bdSort[id] = sortKey == "pnl" ? "trades" : "pnl"; Recompute(); }),
        };

        foreach (var r in rows)
        {
            var rr = r;
            panel.Rows.Add(new BreakdownRow
            {
                Label = r.Label,
                Wt = r.Wins + "/" + r.Trades,
                Pnl = AnalyticsData.Money(r.Pnl, true),
                PnlColor = AnalyticsData.Sgn(r.Pnl),
                BarRatio = Math.Min(1, Math.Abs(r.Pnl) / max),
                BarColor = r.Pnl >= 0 ? AnalyticsData.Accent : AnalyticsData.Red,
                Bg = _selBd == id + r.Label ? "#08131d" : "transparent",
                Command = new RelayCommand(() =>
                {
                    _selBd = id + rr.Label;
                    double net = Net();
                    OpenPanel(rr.Label, title.ToLowerInvariant() + " · " + _period.ToLowerInvariant(), new[]
                    {
                        new KvRow { Label = "Net P&L", Value = AnalyticsData.Money(rr.Pnl, true), Color = AnalyticsData.Sgn(rr.Pnl) },
                        new KvRow { Label = "Trades", Value = rr.Trades.ToString(Inv), Color = AnalyticsData.Text },
                        new KvRow { Label = "Wins / losses", Value = rr.Wins + " / " + Math.Max(0, rr.Trades - rr.Wins), Color = AnalyticsData.Text3 },
                        new KvRow { Label = "Win rate", Value = rr.Trades > 0 ? ((double)rr.Wins / rr.Trades * 100).ToString("0", Inv) + "%" : AnalyticsData.Empty, Color = AnalyticsData.Accent },
                        new KvRow { Label = "Avg trade", Value = rr.Trades > 0 ? AnalyticsData.Money(rr.Pnl / rr.Trades, true) : AnalyticsData.Empty, Color = AnalyticsData.Sgn(rr.Pnl) },
                        new KvRow { Label = "Share of net P&L", Value = Math.Abs(net) > 1e-9 ? Math.Round(rr.Pnl / net * 100) + "%" : AnalyticsData.Empty, Color = AnalyticsData.Text3 },
                    }, "Aggregated from the closed trades currently in scope. Change the period or source in the header to rescope the whole page.");
                }),
            });
        }
        return panel;
    }

    // ── trade history ────────────────────────────────────────────────────────

    private void RebuildTradeHistory()
    {
        var q = _search.Trim().ToLowerInvariant();
        var rows = _trades.Where(t =>
        {
            if (_sideFilter == "win" && !t.IsWin) return false;
            if (_sideFilter == "loss" && t.IsWin) return false;
            if (q.Length > 0 && !($"{t.Symbol} {SourceOf(t)} {t.ExitReason} {SideOf(t)} {t.Exchange}").ToLowerInvariant().Contains(q)) return false;
            return true;
        }).ToList();

        rows = _sortKey switch
        {
            "pnl" => rows.OrderBy(t => Pnl(t) * _sortDir).ToList(),
            "pct" => rows.OrderBy(t => PctOf(t) * _sortDir).ToList(),
            "symbol" => rows.OrderBy(t => t.Symbol, StringComparer.Ordinal).ToList().Also(_sortDir),
            "source" => rows.OrderBy(SourceOf, StringComparer.Ordinal).ToList().Also(_sortDir),
            _ => (_sortDir < 0 ? rows.OrderByDescending(t => t.ClosedAtUtc) : rows.OrderBy(t => t.ClosedAtUtc)).ToList(),
        };

        ThMeta = _pnl is null ? "not connected" : rows.Count + " of " + _trades.Count + " closed";

        string Arrow(string k) => _sortKey == k ? (_sortDir < 0 ? " ↓" : " ↑") : "";
        string ColColor(string k) => _sortKey == k ? AnalyticsData.Accent : AnalyticsData.Faint;

        ThCols.Clear();
        ThCols.Add(new ColHeader { Label = "DATE" + Arrow("date"), Width = 112, Align = "Left", Color = ColColor("date"), Command = new RelayCommand(() => SetSort("date")) });
        ThCols.Add(new ColHeader { Label = "SYMBOL" + Arrow("symbol"), Width = 106, Align = "Left", Color = ColColor("symbol"), Command = new RelayCommand(() => SetSort("symbol")) });
        ThCols.Add(new ColHeader { Label = "SOURCE" + Arrow("source"), Width = 106, Align = "Left", Color = ColColor("source"), Command = new RelayCommand(() => SetSort("source")) });
        ThCols.Add(new ColHeader { Label = "SIDE", Width = 66, Align = "Left", Color = AnalyticsData.Faint, Command = new RelayCommand(() => { _sideFilter = _sideFilter == "all" ? "win" : "all"; Recompute(); }) });
        ThCols.Add(new ColHeader { Label = "P&L" + Arrow("pnl"), Width = 92, Align = "Right", Color = ColColor("pnl"), Command = new RelayCommand(() => SetSort("pnl")) });
        ThCols.Add(new ColHeader { Label = "P&L %" + Arrow("pct"), Width = 82, Align = "Right", Color = ColColor("pct"), Command = new RelayCommand(() => SetSort("pct")) });
        ThCols.Add(new ColHeader { Label = "DURATION", Width = 86, Align = "Right", Color = AnalyticsData.Faint });
        ThCols.Add(new ColHeader { Label = "EXIT REASON", Width = 200, Fill = true, Align = "Left", Color = AnalyticsData.Faint });

        ThFilters.Clear();
        foreach (var (id, label) in new[] { ("all", "ALL"), ("win", "WINS"), ("loss", "LOSSES") })
        {
            var key = id; var on = _sideFilter == id;
            ThFilters.Add(new FilterChip
            {
                Label = label, Bg = on ? "#0e2a2a" : "transparent", Fg = on ? AnalyticsData.Accent : AnalyticsData.Dimmer,
                Command = new RelayCommand(() => { _sideFilter = key; Recompute(); }),
            });
        }

        ThRows.Clear();
        int idx = 0;
        foreach (var t in rows)
        {
            var tt = t; var i = idx++; var total = rows.Count;
            var src = SourceOf(t); var side = SideOf(t); var date = DateOf(t);
            ThRows.Add(new AnTradeRow
            {
                Date = date, Symbol = t.Symbol, Source = src,
                SrcColor = t.Source == TradeSource.Manual ? AnalyticsData.Amber
                    : t.Source == TradeSource.Sniper ? AnalyticsData.Violet
                    : AnalyticsData.Blue,
                Side = side, SideColor = t.Direction == TradeDirection.Long ? AnalyticsData.Green : AnalyticsData.Red,
                Pnl = AnalyticsData.Money(Pnl(t), true), PnlPct = AnalyticsData.Pct(PctOf(t)), PnlColor = AnalyticsData.Sgn(Pnl(t)),
                Duration = t.DurationLabel, Exit = t.ExitReason,
                Bg = _selRow == t.Id ? "#08131d" : "transparent",
                Command = new RelayCommand(() =>
                {
                    _selRow = tt.Id;
                    OpenPanel(tt.Symbol + " · " + side, src + " · closed " + date, new[]
                    {
                        new KvRow { Label = "Realized P&L", Value = AnalyticsData.Money(Pnl(tt), true), Color = AnalyticsData.Sgn(Pnl(tt)) },
                        new KvRow { Label = "Return", Value = AnalyticsData.Pct(PctOf(tt)), Color = AnalyticsData.Sgn(Pnl(tt)) },
                        new KvRow { Label = "Entry → exit", Value = tt.EntryPrice > 0m && tt.ExitPrice > 0m
                            ? tt.EntryPrice.ToString("0.####", Inv) + " → " + tt.ExitPrice.ToString("0.####", Inv)
                            : AnalyticsData.Empty, Color = AnalyticsData.Text },
                        new KvRow { Label = "Quantity", Value = tt.Quantity > 0m ? tt.Quantity.ToString("0.####", Inv) : AnalyticsData.Empty, Color = AnalyticsData.Text3 },
                        new KvRow { Label = "Holding time", Value = HoldOf(tt) is TimeSpan hold ? FormatSpan(hold) : AnalyticsData.Empty, Color = AnalyticsData.Text },
                        new KvRow { Label = "Exchange", Value = string.IsNullOrWhiteSpace(tt.Exchange) ? AnalyticsData.Empty : tt.Exchange, Color = AnalyticsData.Text3 },
                        new KvRow { Label = "Exit reason", Value = string.IsNullOrWhiteSpace(tt.ExitReason) ? AnalyticsData.Empty : tt.ExitReason, Color = AnalyticsData.Text3 },
                        new KvRow { Label = "Rank in view", Value = "#" + (i + 1) + " of " + total, Color = AnalyticsData.Text3 },
                    }, "Recorded by the engine that closed the position; the desk does not re-price or model fees on top of it.");
                }),
            });
        }
    }

    private void SetSort(string k) { if (_sortKey == k) _sortDir = -_sortDir; else { _sortKey = k; _sortDir = -1; } Recompute(); }

    // ── AI review ────────────────────────────────────────────────────────────

    private void RebuildReview()
    {
        ReviewFindings.Clear();

        if (_journal is null) { ReviewSummary = "no data"; return; }
        if (_journal.CoachRunning) { ReviewSummary = "Reading the closed trades…"; return; }
        if (!_journal.HasCoachReview)
        {
            ReviewSummary = _trades.Count == 0
                ? "no data — record or import closed trades first"
                : "No review yet. Run AI REVIEW to have the journal coach read the closed trades.";
            return;
        }

        ReviewSummary = string.IsNullOrWhiteSpace(_journal.CoachSummary) ? AnalyticsData.Empty : _journal.CoachSummary;
        AddBullets(_journal.CoachStrengths, AnalyticsData.Green);
        AddBullets(_journal.CoachLeaks, AnalyticsData.Amber);
        AddBullets(_journal.CoachSuggestions, AnalyticsData.Accent);
    }

    private void AddBullets(string? block, string dot)
    {
        if (string.IsNullOrWhiteSpace(block)) return;
        foreach (var line in block.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length > 0) ReviewFindings.Add(new Bullet { Text = text, Dot = dot });
        }
    }

    // ── risk-adjusted ratios ─────────────────────────────────────────────────

    private void RebuildRatios()
    {
        Ratios.Clear();
        if (_pnl is null) return;

        string sharpe = AnalyticsData.Empty, sortino = AnalyticsData.Empty, calmar = AnalyticsData.Empty;
        string expectancy = AnalyticsData.Empty, payoffLabel = AnalyticsData.Empty, kelly = AnalyticsData.Empty;

        // Daily realized P&L — the smallest real return bucket the records support.
        var daily = Days().Select(d => d.Pnl).ToList();
        if (daily.Count >= 5)
        {
            double mean = daily.Average();
            double variance = daily.Sum(v => (v - mean) * (v - mean)) / (daily.Count - 1);
            double sd = Math.Sqrt(variance);
            if (sd > 0) sharpe = (mean / sd * Math.Sqrt(365)).ToString("0.00", Inv);

            var down = daily.Where(v => v < 0).ToList();
            if (down.Count > 0)
            {
                double dsd = Math.Sqrt(down.Sum(v => v * v) / down.Count);
                if (dsd > 0) sortino = (mean / dsd * Math.Sqrt(365)).ToString("0.00", Inv);
            }
        }

        double net = Net();
        double maxDd = MaxDrawdownUsd(out _, out _);
        if (maxDd > 0) calmar = (net / maxDd).ToString("0.00", Inv);
        if (_trades.Count > 0) expectancy = AnalyticsData.Money(net / _trades.Count, true);

        double payoff = Payoff();
        if (payoff > 0) payoffLabel = payoff.ToString("0.00", Inv);

        if (_trades.Count >= 10 && payoff > 0)
        {
            double w = (double)_trades.Count(t => t.IsWin) / _trades.Count;
            kelly = Math.Round((w - (1 - w) / payoff) * 100).ToString("0", Inv) + "%";
        }

        Ratios.Add(new Ratio { Label = "Sharpe", Value = sharpe, Hint = "daily P&L, annualized", Color = sharpe == AnalyticsData.Empty ? AnalyticsData.Dim : AnalyticsData.Text });
        Ratios.Add(new Ratio { Label = "Sortino", Value = sortino, Hint = "downside only", Color = sortino == AnalyticsData.Empty ? AnalyticsData.Dim : AnalyticsData.Text });
        Ratios.Add(new Ratio { Label = "Calmar", Value = calmar, Hint = "net P&L / max DD", Color = calmar == AnalyticsData.Empty ? AnalyticsData.Dim : AnalyticsData.Accent });
        Ratios.Add(new Ratio { Label = "Expectancy", Value = expectancy, Hint = "per trade", Color = expectancy == AnalyticsData.Empty ? AnalyticsData.Dim : AnalyticsData.Sgn(net) });
        Ratios.Add(new Ratio { Label = "Payoff", Value = payoffLabel, Hint = "avg win / avg loss", Color = payoffLabel == AnalyticsData.Empty ? AnalyticsData.Dim : AnalyticsData.Text });
        Ratios.Add(new Ratio { Label = "Kelly", Value = kelly, Hint = "suggested size", Color = kelly == AnalyticsData.Empty ? AnalyticsData.Dim : AnalyticsData.Amber });
    }

    private double Payoff()
    {
        var wins = _trades.Where(t => t.IsWin).ToList();
        var losses = _trades.Where(t => !t.IsWin).ToList();
        if (wins.Count == 0 || losses.Count == 0) return 0;
        double avgWin = wins.Average(Pnl);
        double avgLoss = Math.Abs(losses.Average(Pnl));
        return avgLoss <= 0 ? 0 : avgWin / avgLoss;
    }

    private double MaxDrawdownUsd(out int peakIdx, out int troughIdx)
    {
        peakIdx = troughIdx = 0;
        if (_equity.Count < 2) return 0;
        double peak = _equity[0], worst = 0;
        int pIdx = 0;
        for (int i = 0; i < _equity.Count; i++)
        {
            if (_equity[i] > peak) { peak = _equity[i]; pIdx = i; }
            double dd = peak - _equity[i];
            if (dd > worst) { worst = dd; peakIdx = pIdx; troughIdx = i; }
        }
        return worst;
    }

    /// <summary>Close time of the trade behind an equity-curve index (index 0 is the opening zero).</summary>
    private string EquityDate(int equityIndex)
    {
        int k = _trades.Count - equityIndex;              // equity walks the list backwards
        if (equityIndex <= 0 || k < 0 || k >= _trades.Count) return AnalyticsData.Empty;
        return DateOf(_trades[k]);
    }

    // ── P&L distribution ─────────────────────────────────────────────────────

    private void RebuildDist()
    {
        DistBars.Clear();
        DistLeft = DistRight = AnalyticsData.Empty;

        if (_trades.Count == 0) { DistMeta = "no data"; return; }
        DistMeta = _trades.Count + (_trades.Count == 1 ? " trade" : " trades");

        double lo = _trades.Min(PctOf), hi = _trades.Max(PctOf);
        if (hi - lo < 1e-9) { lo -= 0.5; hi += 0.5; }

        const int N = 9;
        double w = (hi - lo) / N;
        var counts = new int[N];
        foreach (var t in _trades) counts[Math.Clamp((int)((PctOf(t) - lo) / w), 0, N - 1)]++;

        double max = Math.Max(1, counts.Max());
        for (int i = 0; i < N; i++)
        {
            double a = lo + w * i, b = a + w;
            int c = counts[i];
            var title = c + (c == 1 ? " trade " : " trades ") + "between " + AnalyticsData.Pct(a) + " and " + AnalyticsData.Pct(b);
            DistBars.Add(new DistBar
            {
                HeightRatio = c / max,
                Color = (a + b) / 2 < 0 ? "rgba(255,107,107,.8)" : "rgba(33,230,193,.8)",
                Title = title,
                Command = new RelayCommand(() => Toast(title, "info")),
            });
        }

        DistLeft = AnalyticsData.Pct(lo);
        DistRight = AnalyticsData.Pct(hi);
    }

    // ── streaks & habits ─────────────────────────────────────────────────────

    private (int len, bool win) CurrentStreak()
    {
        if (_trades.Count == 0) return (0, false);
        bool win = _trades[0].IsWin;                       // index 0 is the most recent trade
        int len = 0;
        foreach (var t in _trades) { if (t.IsWin != win) break; len++; }
        return (len, win);
    }

    private void RebuildHabits()
    {
        Habits.Clear();
        if (_trades.Count == 0) return;

        var (len, win) = CurrentStreak();
        int bestWin = 0, worstLoss = 0, runW = 0, runL = 0;
        foreach (var t in Enumerable.Reverse(_trades))
        {
            if (t.IsWin) { runW++; runL = 0; if (runW > bestWin) bestWin = runW; }
            else { runL++; runW = 0; if (runL > worstLoss) worstLoss = runL; }
        }

        var top = _trades.GroupBy(t => t.Symbol, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First();
        int days = _trades.Select(t => t.ClosedAtUtc.Date).Distinct().Count();

        Habits.Add(new KvRow { Label = "Current streak", Value = StreakLabel(len, win), Color = win ? AnalyticsData.Green : AnalyticsData.Red });
        Habits.Add(new KvRow { Label = "Best streak", Value = StreakLabel(bestWin, true), Color = AnalyticsData.Green });
        Habits.Add(new KvRow { Label = "Worst streak", Value = StreakLabel(worstLoss, false), Color = AnalyticsData.Red });
        Habits.Add(new KvRow { Label = "Most traded", Value = top.Key + " · " + top.Count(), Color = AnalyticsData.Text });
        Habits.Add(new KvRow { Label = "Avg trades / day", Value = days == 0 ? AnalyticsData.Empty : ((double)_trades.Count / days).ToString("0.0", Inv), Color = AnalyticsData.Text3 });
    }

    private void RebuildFooter()
    {
        if (_pnl is null)
        {
            FooterRows = "not connected"; FooterPeriod = ""; FooterSource = ""; FooterFees = "";
            return;
        }
        FooterRows = _trades.Count + " closed trades";
        FooterPeriod = "period " + _period.ToLowerInvariant();
        FooterSource = "src " + _source.ToLowerInvariant();
        FooterFees = _pnl.StatusMessage;
    }

    // ── header actions ───────────────────────────────────────────────────────

    private void RunRefresh()
    {
        if (_pnl is null) { Toast("P&L store is not connected yet", "warn"); return; }
        _pnl.RefreshCommand.Execute().Subscribe();
        Recompute();
        Toast(_pnl.StatusMessage.Length > 0 ? _pnl.StatusMessage : "Trade store re-read", "info");
    }

    private void RunExport()
    {
        if (_pnl is null) { Toast("P&L store is not connected yet", "warn"); return; }
        _pnl.ExportCsvCommand.Execute().Subscribe();
        Toast(_pnl.StatusMessage.Length > 0 ? _pnl.StatusMessage : "CSV exported", "ok");
    }

    private void RunAi()
    {
        if (_journal is null) { Toast("Journal coach is not connected yet", "warn"); return; }
        if (_journal.CoachRunning) return;
        _journal.ReviewWithAiCommand.Execute().Subscribe();
        Toast("Journal coach reading the closed trades", "ai");
        QueueRecompute();
    }

    private void OpenJournal()
    {
        if (_host is null) { Toast("Shell is not connected yet", "warn"); return; }
        _host.SelectMainTabCommand.Execute("journal").Subscribe();
    }

    private void OpenBenchmark()
    {
        var sym = HoldSymbol();
        if (_pnl is null || _holdNet is null || sym is null)
        {
            OpenPanel("Benchmark", "strategy vs buy & hold", NoRows(),
                "The hold benchmark needs entry price, exit price and quantity on the recorded trades; the current selection does not have them.");
            return;
        }

        double hold = _holdNet.Value, net = Net();
        OpenPanel("Benchmark", "strategy vs buy & hold · " + _period.ToLowerInvariant(), new[]
        {
            new KvRow { Label = "Strategy net P&L", Value = AnalyticsData.Money(net, true), Color = AnalyticsData.Sgn(net) },
            new KvRow { Label = "Hold symbol", Value = sym.Value.symbol + " · " + sym.Value.trades + " trades", Color = AnalyticsData.Text3 },
            new KvRow { Label = "Buy & hold (same notional)", Value = AnalyticsData.Money(hold, true), Color = AnalyticsData.Text3 },
            new KvRow { Label = "Excess return", Value = AnalyticsData.Money(net - hold, true), Color = AnalyticsData.Sgn(net - hold) },
            new KvRow { Label = "Strategy max drawdown", Value = _pnl.MaxDrawdownLabel, Color = AnalyticsData.Amber },
            new KvRow { Label = "Trades compared", Value = _trades.Count.ToString(Inv), Color = AnalyticsData.Text3 },
        }, "Hold assumes the same notional entered at the first entry price of the most-traded symbol and exited at its last exit price.");
    }

    private void OpenDeep()
    {
        if (_pnl is null || _trades.Count == 0)
        {
            OpenPanel("Deep dive", "closed trades", NoRows(), "No closed trades in the current selection.");
            return;
        }

        var bySrc = Group(SourceOf).ToList();
        var byAsset = Group(t => t.Asset).ToList();
        var best = _trades.OrderByDescending(Pnl).First();
        var worst = _trades.OrderBy(Pnl).First();

        string AvgHold(bool wins)
        {
            var set = _trades.Where(t => t.IsWin == wins).Select(HoldOf).Where(s => s.HasValue)
                .Select(s => s!.Value.TotalMinutes).ToList();
            return set.Count == 0 ? AnalyticsData.Empty : FormatSpan(TimeSpan.FromMinutes(set.Average()));
        }

        string Pick(List<Bd> rows, bool topEnd) => rows.Count == 0 ? AnalyticsData.Empty
            : (topEnd ? rows.OrderByDescending(r => r.Pnl).First() : rows.OrderBy(r => r.Pnl).First()).Label;

        OpenPanel("Deep dive", _trades.Count + " closed trades · " + _period.ToLowerInvariant(), new[]
        {
            new KvRow { Label = "Best source", Value = Pick(bySrc, true), Color = AnalyticsData.Green },
            new KvRow { Label = "Worst source", Value = Pick(bySrc, false), Color = AnalyticsData.Red },
            new KvRow { Label = "Best asset", Value = Pick(byAsset, true), Color = AnalyticsData.Green },
            new KvRow { Label = "Worst asset", Value = Pick(byAsset, false), Color = AnalyticsData.Red },
            new KvRow { Label = "Largest win", Value = best.Symbol + " " + AnalyticsData.Money(Pnl(best), true), Color = AnalyticsData.Sgn(Pnl(best)) },
            new KvRow { Label = "Largest loss", Value = worst.Symbol + " " + AnalyticsData.Money(Pnl(worst), true), Color = AnalyticsData.Sgn(Pnl(worst)) },
            new KvRow { Label = "Avg hold · winners", Value = AvgHold(true), Color = AnalyticsData.Text },
            new KvRow { Label = "Avg hold · losers", Value = AvgHold(false), Color = AnalyticsData.Text },
        }, "Derived from the closed trades in the current filter. Anything the store does not record shows as " + AnalyticsData.Empty + ".");
    }

    private static string FormatSpan(TimeSpan d)
    {
        if (d.TotalDays >= 1) return d.TotalDays.ToString("0.0", Inv) + "d";
        if (d.TotalHours >= 1) return d.TotalHours.ToString("0.0", Inv) + "h";
        return Math.Max(0, d.TotalMinutes).ToString("0", Inv) + "m";
    }

    // ── panel / toast ────────────────────────────────────────────────────────
    private string? _modal;
    public bool ModalPanel => _modal == "panel";
    public string PanelTitle { get; private set; } = "";
    public string PanelSub { get; private set; } = "";
    public string PanelNote { get; private set; } = "";

    private void OpenPanel(string title, string sub, IEnumerable<KvRow> rows, string note)
    {
        PanelTitle = title; PanelSub = sub; PanelNote = note;
        PanelRows.Clear();
        foreach (var r in rows) PanelRows.Add(r);
        _modal = "panel";
        foreach (var n in new[] { nameof(PanelTitle), nameof(PanelSub), nameof(PanelNote), nameof(ModalPanel) }) this.RaisePropertyChanged(n);
    }

    private bool _hasToast;
    public bool HasToast { get => _hasToast; private set => this.RaiseAndSetIfChanged(ref _hasToast, value); }
    public string ToastMsg { get; private set; } = "";
    public string ToastColor { get; private set; } = AnalyticsData.Accent;
    public string ToastIcon { get; private set; } = "";
    public string ToastBorder { get; private set; } = "#0d1b27";
    public string ToastMeta { get; private set; } = "";

    private void Toast(string msg, string kind = "ok")
    {
        (string color, string icon) = kind switch
        {
            "ok" => (AnalyticsData.Green, "✓"), "warn" => (AnalyticsData.Amber, "!"), "bad" => (AnalyticsData.Red, "✕"),
            "ai" => (AnalyticsData.Accent, "✦"), _ => (AnalyticsData.Accent, "›"),
        };
        ToastMsg = msg; ToastColor = color; ToastIcon = icon; ToastBorder = AnalyticsData.Alpha(color, "55"); ToastMeta = DateTime.Now.ToString("HH:mm:ss", Inv);
        foreach (var n in new[] { nameof(ToastMsg), nameof(ToastColor), nameof(ToastIcon), nameof(ToastBorder), nameof(ToastMeta) }) this.RaisePropertyChanged(n);
        HasToast = true; _toastTimer.Stop(); _toastTimer.Start();
    }
}

internal static class ListExt
{
    /// <summary>Reverses the list when the sort direction is descending.</summary>
    public static List<T> Also<T>(this List<T> list, int dir) { if (dir < 0) list.Reverse(); return list; }
}
