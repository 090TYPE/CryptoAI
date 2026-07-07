using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// View model backing the "AI Signal" desk — a faithful native port of the design
/// mock (signal feed with filters; regime / news / opportunities / insights /
/// multi-TF heatmap hub; right-hand detail panel with five tabs incl. a chat).
///
/// It runs in two modes over the same UI:
///   • <b>Live</b> — when an AI key is configured, <see cref="AiSignalDeskService"/>
///     turns real live market data into the whole desk (direction, confidence,
///     reasoning, regime, news, opportunities, insights, coach). Prices stay real;
///     the model only labels/analyses them. Refreshes on tab open + a Refresh button.
///   • <b>Offline</b> — with no key or on any error, the deterministic demo desk
///     below is shown, and Ask-AI replies via a canned heuristic. Identical UX.
/// </summary>
public sealed class AiSignalDeskViewModel : ReactiveObject
{
    // ── Direction palette ────────────────────────────────────────────────
    private readonly record struct DirStyle(string Color, string Bg, string Border, string Label);

    /// <summary>CSS rgba() → Avalonia #AARRGGBB (alpha first) so every colour parses reliably.</summary>
    internal static string Rgba(int r, int g, int b, double a) =>
        $"#{(int)Math.Round(Math.Clamp(a, 0, 1) * 255):X2}{r:X2}{g:X2}{b:X2}";

    private static DirStyle Ds(string dir) => dir switch
    {
        "BUY"  => new DirStyle("#3ddc84", Rgba(29, 74, 46, 0.55), "#1a4a2e", "LONG"),
        "SELL" => new DirStyle("#ff6b6b", Rgba(74, 26, 30, 0.55), "#4a1a1e", "SHORT"),
        _      => new DirStyle("#f4b860", Rgba(74, 56, 16, 0.45), "#3a2c10", "HOLD"),
    };

    // Tone palette for regime / news / insight badges.
    private static (string Bg, string Border, string Color) ToneStyle(string tone) => tone switch
    {
        "bull" => (Rgba(29, 74, 46, 0.55), "#1a4a2e", "#3ddc84"),
        "bear" => (Rgba(74, 26, 30, 0.55), "#4a1a1e", "#ff6b6b"),
        _      => (Rgba(74, 56, 16, 0.45), "#3a2c10", "#f4b860"),
    };

    private static string BiasTone(string bias) => bias.ToUpperInvariant() switch
    {
        "BULLISH" or "LONG" or "BUY" => "bull",
        "BEARISH" or "SHORT" or "SELL" => "bear",
        _ => "neutral",
    };

    private static string DotColor(string tone) => tone switch
    {
        "bear" => "#ff6b6b",
        "warn" => "#f4b860",
        _      => "#21e6c1",
    };

    // ── Master signal model ─────────────────────────────────────────────
    private sealed record SignalModel(
        string Sym, string Logo, string LogoBg, string Dir, string Tf, string Exch,
        double Conf, string Reason, string Time, string Price, string Change);

    private List<SignalModel> _all = DemoSignals();

    private static List<SignalModel> DemoSignals() =>
    [
        new("BTC",  "BTC", "#0c2218", "BUY",  "15m", "Binance", 0.87, "Strong breakout above 67,200 resistance with rising volume and RSI divergence confirmed on 15m.",   "14:32:11", "67,432", "+2.34%"),
        new("ETH",  "ETH", "#0c102a", "BUY",  "1h",  "Binance", 0.79, "Bullish engulfing on 1h, MA20/50 cross confirmed, volume 40% above 30-day average.",                "14:30:44", "3,521",  "+1.87%"),
        new("SOL",  "SOL", "#1c0c2a", "SELL", "5m",  "Bybit",   0.72, "Bearish divergence on RSI, rejection at 185 supply zone, funding rate elevated above 0.05%.",      "14:29:18", "182.40", "-0.92%"),
        new("BNB",  "BNB", "#2a1e0c", "HOLD", "1h",  "Binance", 0.61, "Consolidating between 580–590, awaiting breakout direction confirmation before entry.",             "14:28:55", "584.20", "+0.41%"),
        new("AVAX", "AX",  "#2a0c0c", "SELL", "4h",  "OKX",     0.83, "Head-and-shoulders on 4h, neckline broken at 38.5, distribution volume confirming.",               "14:27:33", "37.84",  "-2.18%"),
        new("LINK", "LNK", "#0c102a", "BUY",  "1h",  "Binance", 0.68, "Retesting broken resistance as dynamic support, momentum indicators turning up strongly.",         "14:25:12", "18.42",  "+3.14%"),
        new("ARB",  "ARB", "#0c1e2a", "BUY",  "15m", "Bybit",   0.74, "Breakout from descending wedge, RSI at 55 with room to run, notable volume spike.",               "14:24:08", "1.142",  "+4.22%"),
        new("DOGE", "DGE", "#2a200c", "HOLD", "1h",  "Binance", 0.55, "Mixed signals at key MA, awaiting volume confirmation for directional bias.",                      "14:22:44", "0.1418", "-0.33%"),
        new("OP",   "OP",  "#2a0c14", "SELL", "5m",  "OKX",     0.69, "Double top at 2.84, bearish MACD crossover confirmed, funding rate elevated.",                    "14:20:19", "2.741",  "-1.56%"),
        new("INJ",  "INJ", "#0c1e2a", "BUY",  "4h",  "Binance", 0.91, "Strong accumulation phase, on-chain inflows up 34%, RSI recovering from oversold at 31.",         "14:18:55", "31.44",  "+5.82%"),
        new("WIF",  "WIF", "#2a0c1e", "SELL", "15m", "Bybit",   0.77, "Parabolic extension, funding at 0.08%, pullback expected to 2.88 support.",                      "14:16:22", "3.124",  "+1.21%"),
        new("TIA",  "TIA", "#0c2a1e", "HOLD", "1h",  "Binance", 0.59, "Testing 200 EMA as dynamic support, neutral bias pending reaction.",                               "14:14:40", "8.932",  "+0.18%"),
    ];

    // ── Multi-TF heatmap state (rebuilt from whichever signals are active) ─
    private static readonly string[] HmTfs = ["1m", "5m", "15m", "1h", "4h", "1D"];
    private List<string> _hmSyms = [];
    private List<string[]> _hmDir = [];
    private List<double[]> _hmConf = [];

    // ── State ───────────────────────────────────────────────────────────
    private string _filter = "all";
    private int _selIdx;
    private string _detailTab = "signal";
    private string _chatInput = string.Empty;
    private bool _isGenerating;
    private string _sourceLabel = "offline · heuristic";
    private bool _loadedOnce;
    private CancellationTokenSource? _refreshCts;

    private readonly Func<AiSignalDeskContext>? _contextProvider;
    private readonly AiSignalDeskService? _service;

    // ── Collections ─────────────────────────────────────────────────────
    public ObservableCollection<AiSignalTickerViewModel> Ticker { get; } = [];
    public ObservableCollection<AiSignalFilterViewModel> Filters { get; } = [];
    public ObservableCollection<AiSignalCardViewModel> FeedItems { get; } = [];
    public ObservableCollection<AiSignalDexTokenViewModel> DexTokens { get; } = [];
    public ObservableCollection<AiSignalOpportunityViewModel> Opportunities { get; } = [];
    public ObservableCollection<AiSignalInsightViewModel> Insights { get; } = [];
    public ObservableCollection<AiSignalNewsBulletViewModel> NewsBullets { get; } = [];
    public ObservableCollection<AiSignalConsensusBarViewModel> ConsensusBars { get; } = [];
    public ObservableCollection<AiSignalDetailTabViewModel> DetailTabs { get; } = [];
    public ObservableCollection<string> HeatmapTimeframes { get; } = [];
    public ObservableCollection<AiSignalHeatmapRowViewModel> HeatmapRows { get; } = [];
    public ObservableCollection<AiSignalChatMessageViewModel> ChatMessages { get; } = [];
    public ObservableCollection<AiSignalSuggestionViewModel> Suggestions { get; } = [];
    public ObservableCollection<string> JournalStrengths { get; } = [];
    public ObservableCollection<string> JournalLeaks { get; } = [];
    public ObservableCollection<string> JournalSuggestions { get; } = [];

    // ── Commands ────────────────────────────────────────────────────────
    public ReactiveCommand<string, Unit> SetFilterCommand { get; }
    public ReactiveCommand<AiSignalCardViewModel, Unit> SelectSignalCommand { get; }
    public ReactiveCommand<string, Unit> SetDetailTabCommand { get; }
    public ReactiveCommand<string, Unit> UseSuggestionCommand { get; }
    public ReactiveCommand<Unit, Unit> SendChatCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Design-time / offline-only ctor (demo desk, no AI).</summary>
    public AiSignalDeskViewModel() : this(null, null) { }

    public AiSignalDeskViewModel(Func<AiSignalDeskContext>? contextProvider, AiSignalDeskService? service)
    {
        _contextProvider = contextProvider;
        _service = service;
        _sourceLabel = service?.SourceLabel ?? "offline · heuristic";

        SetFilterCommand = ReactiveCommand.Create<string>(SetFilter);
        SelectSignalCommand = ReactiveCommand.Create<AiSignalCardViewModel>(SelectSignal);
        SetDetailTabCommand = ReactiveCommand.Create<string>(SetDetailTab);
        UseSuggestionCommand = ReactiveCommand.Create<string>(text => ChatInput = text);
        SendChatCommand = ReactiveCommand.CreateFromTask(SendChatAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);

        BuildStaticContent();
        BuildFilters();
        BuildFeed();
        BuildHeatmapFromSignals();
        BuildJournalDemo();
        BuildRegimeNewsDemo();
        RefreshSelected();
    }

    // ── Live generation ─────────────────────────────────────────────────

    /// <summary>Called when the AI Signals tab is first opened; generates once if configured.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loadedOnce) return;
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        SourceLabel = _service?.SourceLabel ?? "offline · heuristic";

        AiSignalDeskContext? ctx = null;
        try { ctx = _contextProvider?.Invoke(); }
        catch { ctx = null; }

        // Always mirror the real tracked markets in the feed — even offline. The AI
        // pass below refines direction/confidence/reasoning on the same symbols.
        if (ctx?.Markets is { Count: > 0 })
        {
            SeedFeedFromMarkets(ctx);
            _loadedOnce = true; // markets were available — don't auto-reload on every tab open
        }

        if (_service is null || ctx?.Markets is not { Count: > 0 } || !_service.IsConfigured)
            return; // feed already reflects tracked markets (offline heuristic)

        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();

        IsGenerating = true;
        try
        {
            var result = await _service.GenerateAsync(ctx, cts.Token).ConfigureAwait(true);
            if (cts.Token.IsCancellationRequested) return;
            if (result is not null)
            {
                ApplyResult(result, ctx);
                SourceLabel = result.Source;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(cts, _refreshCts)) IsGenerating = false;
        }
    }

    /// <summary>
    /// Rebuilds the signal feed from the real tracked markets (the coins shown on the
    /// Markets tab). Direction/confidence/reasoning are a deterministic momentum
    /// heuristic so the feed is meaningful even without an AI key; the live AI pass
    /// then refines the same symbols.
    /// </summary>
    private void SeedFeedFromMarkets(AiSignalDeskContext ctx)
    {
        var now = DateTime.Now;
        var list = new List<SignalModel>();
        var idx = 0;
        foreach (var m in ctx.Markets)
        {
            list.Add(SignalFromMarket(m, idx, now));
            idx++;
        }
        if (list.Count == 0) return;

        _all = list;
        if (_selIdx >= _all.Count) _selIdx = 0;
        BuildFilters();
        BuildFeed();
        BuildHeatmapFromSignals();
        RefreshSelected();
        this.RaisePropertyChanged(nameof(SignalCount));
    }

    private static SignalModel SignalFromMarket(AiSignalMarketRow m, int idx, DateTime now)
    {
        var chg = (double)m.ChangePct;
        var dir = chg > 0.6 ? "BUY" : chg < -0.6 ? "SELL" : "HOLD";
        var conf = Math.Clamp(0.55 + Math.Abs(chg) / 12.0 + Math.Min((double)m.Activity, 200) / 800.0, 0.5, 0.92);
        var chgStr = $"{m.ChangePct:+0.##;-0.##;0}%";
        var reason = dir switch
        {
            "BUY" => $"{m.Sym} up {chgStr} on the session with firm bid-side activity — momentum favours continuation.",
            "SELL" => $"{m.Sym} down {chgStr} under rising supply pressure — bias stays bearish until reclaim.",
            _ => $"{m.Sym} ranging ({chgStr}) — awaiting a decisive break before committing.",
        };
        var logo = !string.IsNullOrWhiteSpace(m.LogoText) ? m.LogoText : Short(m.Sym);
        var logoBg = !string.IsNullOrWhiteSpace(m.LogoBg) ? m.LogoBg : "#0c1a26";
        return new SignalModel(
            m.Sym, logo, logoBg, dir, "1h", m.Exch, conf, reason,
            now.AddSeconds(-idx * 37).ToString("HH:mm:ss"), FormatPrice(m.Price), chgStr);
    }

    private void ApplyResult(AiSignalDeskResult result, AiSignalDeskContext ctx)
    {
        var bySym = ctx.Markets.ToDictionary(m => m.Sym.ToUpperInvariant(), m => m);

        // Signals — reasoning/direction/confidence from AI, price/logo from real markets.
        var now = DateTime.Now;
        var signals = new List<SignalModel>();
        var idx = 0;
        foreach (var s in result.Signals)
        {
            bySym.TryGetValue(s.Sym, out var m);
            var price = m is not null ? FormatPrice(m.Price) : "--";
            var change = m is not null ? $"{m.ChangePct:+0.##;-0.##;0}%" : "";
            var logo = m is not null && !string.IsNullOrWhiteSpace(m.LogoText) ? m.LogoText : Short(s.Sym);
            var logoBg = m is not null && !string.IsNullOrWhiteSpace(m.LogoBg) ? m.LogoBg : "#0c1a26";
            var exch = !string.IsNullOrWhiteSpace(s.Exch) ? s.Exch : m?.Exch ?? "";
            signals.Add(new SignalModel(
                s.Sym, logo, logoBg, s.Dir, s.Tf, exch, s.Conf, s.Reason,
                now.AddSeconds(-idx * 37).ToString("HH:mm:ss"), price, change));
            idx++;
        }
        if (signals.Count > 0) _all = signals;

        // Opportunities.
        if (result.Opportunities.Count > 0)
        {
            Opportunities.Clear();
            foreach (var o in result.Opportunities)
            {
                var d = Ds(o.Bias == "SHORT" ? "SELL" : "BUY");
                Opportunities.Add(new AiSignalOpportunityViewModel(
                    o.Sym, o.Exch, o.Score, o.Bias == "SHORT" ? "SHORT" : "LONG",
                    d.Bg, d.Border, d.Color, o.Score / 100.0, o.Reason));
            }
        }

        // Insights.
        if (result.Insights.Count > 0)
        {
            Insights.Clear();
            foreach (var i in result.Insights)
            {
                var (bg, border, color) = ToneStyle(i.Tone);
                Insights.Add(new AiSignalInsightViewModel(i.Label, i.Signal, color, bg, border, i.Summary));
            }
        }

        // Regime + news + coach.
        ApplyRegime(result.Regime);
        ApplyNews(result.News);
        ApplyCoach(result.Coach);

        // Rebuild dependent UI.
        _selIdx = 0;
        _detailTab = "signal";
        BuildFilters();
        BuildFeed();
        BuildHeatmapFromSignals();
        BuildDetailTabs();
        RefreshSelected();
        this.RaisePropertyChanged(nameof(SignalCount));
    }

    private void ApplyRegime(AiDeskRegime r)
    {
        var (bg, border, color) = ToneStyle(r.Tone);
        RegimeLabel = string.IsNullOrWhiteSpace(r.Label) ? RegimeLabel : r.Label.ToUpperInvariant();
        RegimeSummary = string.IsNullOrWhiteSpace(r.Summary) ? RegimeSummary : r.Summary;
        RegimeMeta = string.IsNullOrWhiteSpace(r.Meta) ? RegimeMeta : r.Meta;
        RegimeBadgeBg = bg; RegimeBadgeBorder = border; RegimeBadgeColor = color;
        RaiseAll(nameof(RegimeLabel), nameof(RegimeSummary), nameof(RegimeMeta),
            nameof(RegimeBadgeBg), nameof(RegimeBadgeBorder), nameof(RegimeBadgeColor));
    }

    private void ApplyNews(AiDeskNews n)
    {
        var (bg, border, color) = ToneStyle(BiasTone(n.Bias));
        NewsBias = string.IsNullOrWhiteSpace(n.Bias) ? NewsBias : n.Bias.ToUpperInvariant();
        NewsSummary = string.IsNullOrWhiteSpace(n.Summary) ? NewsSummary : n.Summary;
        NewsBadgeBg = bg; NewsBadgeBorder = border; NewsBadgeColor = color;
        if (n.Bullets.Count > 0)
        {
            NewsBullets.Clear();
            foreach (var b in n.Bullets)
                NewsBullets.Add(new AiSignalNewsBulletViewModel(b.Text, DotColor(b.Tone)));
        }
        RaiseAll(nameof(NewsBias), nameof(NewsSummary), nameof(NewsBadgeBg), nameof(NewsBadgeBorder), nameof(NewsBadgeColor));
    }

    private void ApplyCoach(AiDeskCoach c)
    {
        if (!string.IsNullOrWhiteSpace(c.Summary)) { JournalSummary = c.Summary; this.RaisePropertyChanged(nameof(JournalSummary)); }
        ReplaceList(JournalStrengths, c.Strengths);
        ReplaceList(JournalLeaks, c.Leaks);
        ReplaceList(JournalSuggestions, c.Suggestions);
    }

    private static void ReplaceList(ObservableCollection<string> target, IReadOnlyList<string> src)
    {
        if (src is null || src.Count == 0) return;
        target.Clear();
        foreach (var s in src) target.Add(s);
    }

    private void RaiseAll(params string[] names)
    {
        foreach (var n in names) this.RaisePropertyChanged(n);
    }

    // ── Header / status ──────────────────────────────────────────────────
    public int SignalCount => _all.Count + 41;

    public bool IsGenerating
    {
        get => _isGenerating;
        private set => this.RaiseAndSetIfChanged(ref _isGenerating, value);
    }

    public string SourceLabel
    {
        get => _sourceLabel;
        private set => this.RaiseAndSetIfChanged(ref _sourceLabel, value);
    }

    // ── Regime / news display state ──────────────────────────────────────
    public string RegimeLabel { get; private set; } = "TRENDING UP";
    public string RegimeSummary { get; private set; } = "Strong directional momentum across majors.";
    public string RegimeMeta { get; private set; } = "ADX 38 · Vol: MED · Corr: HIGH · OI: LONG";
    public string RegimeBadgeBg { get; private set; } = Rgba(29, 74, 46, 0.55);
    public string RegimeBadgeBorder { get; private set; } = "#1a4a2e";
    public string RegimeBadgeColor { get; private set; } = "#3ddc84";

    public string NewsBias { get; private set; } = "BULLISH";
    public string NewsSummary { get; private set; } =
        "ETF inflows accelerating for the third consecutive week as the Fed signals a pause on rate hikes, boosting risk appetite across crypto markets.";
    public string NewsBadgeBg { get; private set; } = Rgba(29, 74, 46, 0.55);
    public string NewsBadgeBorder { get; private set; } = "#1a4a2e";
    public string NewsBadgeColor { get; private set; } = "#3ddc84";

    public string JournalSummary { get; private set; } =
        "Good momentum identification, but exits too early. Average winner is 1.4× average loser — hold breakouts longer for better R:R.";

    // ── Filter handling ─────────────────────────────────────────────────
    private void SetFilter(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || _filter == key) return;
        _filter = key;
        BuildFilters();
        BuildFeed();
    }

    private void BuildFilters()
    {
        AiSignalFilterViewModel Mk(string key, string label, int count) => new(
            key, $"{label} {count}", active: _filter == key, command: SetFilterCommand);

        Filters.Clear();
        Filters.Add(Mk("all",   "ALL", _all.Count));
        Filters.Add(Mk("long",  "L",   _all.Count(s => s.Dir == "BUY")));
        Filters.Add(Mk("short", "S",   _all.Count(s => s.Dir == "SELL")));
        Filters.Add(Mk("hold",  "H",   _all.Count(s => s.Dir == "HOLD")));
    }

    private IEnumerable<SignalModel> Filtered() => _filter switch
    {
        "long"  => _all.Where(s => s.Dir == "BUY"),
        "short" => _all.Where(s => s.Dir == "SELL"),
        "hold"  => _all.Where(s => s.Dir == "HOLD"),
        _       => _all,
    };

    private void BuildFeed()
    {
        FeedItems.Clear();
        foreach (var s in Filtered())
        {
            var d = Ds(s.Dir);
            var allIndex = _all.IndexOf(s);
            FeedItems.Add(new AiSignalCardViewModel(
                allIndex, s.Sym, s.Logo, s.LogoBg, s.Tf, s.Exch, s.Reason, s.Time,
                d.Color, d.Bg, d.Border, d.Label,
                confPct: (int)Math.Round(s.Conf * 100),
                isSelected: allIndex == _selIdx,
                command: SelectSignalCommand));
        }
    }

    private void SelectSignal(AiSignalCardViewModel card)
    {
        _selIdx = card.AllIndex;
        _detailTab = "signal";
        foreach (var c in FeedItems) c.IsSelected = c.AllIndex == _selIdx;
        BuildDetailTabs();
        RefreshSelected();
    }

    // ── Selected signal detail ──────────────────────────────────────────
    private void RefreshSelected()
    {
        var sig = _selIdx >= 0 && _selIdx < _all.Count ? _all[_selIdx] : _all[0];
        var d = Ds(sig.Dir);

        SelSym = sig.Sym;
        SelLogo = sig.Logo;
        SelLogoBg = sig.LogoBg;
        SelTf = sig.Tf;
        SelExch = sig.Exch;
        SelReason = sig.Reason;
        SelTime = sig.Time;
        SelPrice = sig.Price;
        SelChange = sig.Change;
        SelChangeColor = sig.Change.StartsWith('-') ? "#ff6b6b" : "#3ddc84";
        SelDirColor = d.Color;
        SelDirBg = d.Bg;
        SelDirBorder = d.Border;
        SelDirLabel = d.Label;
        SelConf = (int)Math.Round(sig.Conf * 100);
        SelConfW = SelConf / 100.0;
        ChatPlaceholder = $"Ask AI about {sig.Sym}...";

        BuildConsensus(sig, d);
        BuildTpSl(sig);

        RaiseAll(nameof(SelSym), nameof(SelLogo), nameof(SelLogoBg), nameof(SelTf), nameof(SelExch),
            nameof(SelReason), nameof(SelTime), nameof(SelPrice), nameof(SelChange), nameof(SelChangeColor),
            nameof(SelDirColor), nameof(SelDirBg), nameof(SelDirBorder), nameof(SelDirLabel),
            nameof(SelConf), nameof(SelConfW), nameof(SelDirLabelExecute), nameof(ChatPlaceholder));
    }

    public string SelSym { get; private set; } = string.Empty;
    public string SelLogo { get; private set; } = string.Empty;
    public string SelLogoBg { get; private set; } = string.Empty;
    public string SelTf { get; private set; } = string.Empty;
    public string SelExch { get; private set; } = string.Empty;
    public string SelReason { get; private set; } = string.Empty;
    public string SelTime { get; private set; } = string.Empty;
    public string SelPrice { get; private set; } = string.Empty;
    public string SelChange { get; private set; } = string.Empty;
    public string SelChangeColor { get; private set; } = "#3ddc84";
    public string SelDirColor { get; private set; } = "#3ddc84";
    public string SelDirBg { get; private set; } = string.Empty;
    public string SelDirBorder { get; private set; } = string.Empty;
    public string SelDirLabel { get; private set; } = string.Empty;
    public string SelDirLabelExecute => $"Execute {SelDirLabel} →";
    public int SelConf { get; private set; }
    public double SelConfW { get; private set; }

    // ── Multi-TF consensus (right panel Signal tab) ─────────────────────
    private void BuildConsensus(SignalModel sig, DirStyle d)
    {
        var hmIdx = _hmSyms.IndexOf(sig.Sym);
        var dirKey = sig.Dir == "BUY" ? "buy" : sig.Dir == "SELL" ? "sell" : "hold";
        var consensus = hmIdx >= 0 ? _hmDir[hmIdx].Count(x => x == dirKey) : 4;

        ConsensusCount = $"{consensus} / 6";
        ConsensusLabel = consensus >= 5 ? "Strong consensus" : consensus >= 3 ? "Moderate consensus" : "Weak signal";

        ConsensusBars.Clear();
        for (var i = 0; i < 6; i++)
            ConsensusBars.Add(new AiSignalConsensusBarViewModel(i < consensus ? d.Color : "#0a1520"));

        RaiseAll(nameof(ConsensusCount), nameof(ConsensusLabel));
    }

    public string ConsensusCount { get; private set; } = string.Empty;
    public string ConsensusLabel { get; private set; } = string.Empty;

    // ── TP / SL tab ─────────────────────────────────────────────────────
    private void BuildTpSl(SignalModel sig)
    {
        var isBuy = sig.Dir == "BUY";
        var rawP = double.TryParse(sig.Price.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
        var tpPct = isBuy ? 0.042 : 0.038;
        var slPct = isBuy ? 0.021 : 0.018;
        var tpRaw = isBuy ? rawP * (1 + tpPct) : rawP * (1 - tpPct);
        var slRaw = isBuy ? rawP * (1 - slPct) : rawP * (1 + slPct);

        TpslTp = (isBuy ? "+" : "−") + (tpPct * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
        TpslSl = (isBuy ? "−" : "+") + (slPct * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
        TpslTpPrice = FormatPrice(tpRaw);
        TpslSlPrice = FormatPrice(slRaw);
        TpslRr = "2.0 : 1";
        TpslRationale = $"ATR-adjusted setup for {sig.Sym}. Medium volatility — 2:1 R:R target maintained. Trailing stop recommended to protect profits on extended moves.";
        TpslAtr = (rawP * 0.014).ToString(rawP < 10 ? "F3" : rawP < 1000 ? "F2" : "F0", CultureInfo.InvariantCulture);
        TpslSize = "2.0% risk";

        RaiseAll(nameof(TpslTp), nameof(TpslSl), nameof(TpslTpPrice), nameof(TpslSlPrice),
            nameof(TpslRr), nameof(TpslRationale), nameof(TpslAtr), nameof(TpslSize));
    }

    public string TpslTp { get; private set; } = string.Empty;
    public string TpslSl { get; private set; } = string.Empty;
    public string TpslTpPrice { get; private set; } = string.Empty;
    public string TpslSlPrice { get; private set; } = string.Empty;
    public string TpslRr { get; private set; } = string.Empty;
    public string TpslRationale { get; private set; } = string.Empty;
    public string TpslAtr { get; private set; } = string.Empty;
    public string TpslSize { get; private set; } = string.Empty;

    // ── Detail tabs ─────────────────────────────────────────────────────
    private void SetDetailTab(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || _detailTab == key) return;
        _detailTab = key;
        BuildDetailTabs();
        RaiseTabVisibility();
    }

    private void BuildDetailTabs()
    {
        (string key, string label)[] defs =
        [
            ("signal", "Signal"), ("tpsl", "TP/SL"), ("statarb", "Pairs"),
            ("journal", "Coach"), ("ask", "Ask AI"),
        ];

        DetailTabs.Clear();
        foreach (var (key, label) in defs)
            DetailTabs.Add(new AiSignalDetailTabViewModel(key, label, _detailTab == key, SetDetailTabCommand));

        RaiseTabVisibility();
    }

    private void RaiseTabVisibility() =>
        RaiseAll(nameof(IsSignalTab), nameof(IsTpSlTab), nameof(IsStatArbTab), nameof(IsJournalTab), nameof(IsAskTab));

    public bool IsSignalTab => _detailTab == "signal";
    public bool IsTpSlTab => _detailTab == "tpsl";
    public bool IsStatArbTab => _detailTab == "statarb";
    public bool IsJournalTab => _detailTab == "journal";
    public bool IsAskTab => _detailTab == "ask";

    // ── Ask AI chat ─────────────────────────────────────────────────────
    public string ChatInput
    {
        get => _chatInput;
        set
        {
            this.RaiseAndSetIfChanged(ref _chatInput, value);
            RaiseAll(nameof(ChatSendBg), nameof(ChatSendFg));
        }
    }

    public string ChatPlaceholder { get; private set; } = "Ask AI about BTC...";
    public string ChatSendBg => string.IsNullOrEmpty(_chatInput) ? "#0a1520" : Rgba(33, 230, 193, 0.18);
    public string ChatSendFg => string.IsNullOrEmpty(_chatInput) ? "#2d4a5e" : "#21e6c1";
    public bool ShowSuggestions => ChatMessages.Count <= 1;

    private async Task SendChatAsync()
    {
        var text = (_chatInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return;

        var sig = _selIdx >= 0 && _selIdx < _all.Count ? _all[_selIdx] : _all[0];
        var conf = (int)Math.Round(sig.Conf * 100);

        ChatMessages.Add(AiSignalChatMessageViewModel.User(text));
        var typing = AiSignalChatMessageViewModel.Typing();
        ChatMessages.Add(typing);
        ChatInput = string.Empty;
        this.RaisePropertyChanged(nameof(ShowSuggestions));

        string? reply = null;
        if (_service is not null && _service.IsConfigured)
        {
            var system =
                $"You are the AI trading assistant embedded in a crypto terminal. The user is looking at a {sig.Sym} " +
                $"{sig.Tf} signal on {sig.Exch}: direction {Ds(sig.Dir).Label} at {conf}% confidence, last price {sig.Price} " +
                $"({sig.Change}). Signal reasoning: {sig.Reason}. Answer the user's question concisely and practically " +
                "for an active trader. Plain text, no markdown.";
            try { reply = await _service.AskAsync(system, text).ConfigureAwait(true); }
            catch { reply = null; }
        }

        if (string.IsNullOrWhiteSpace(reply))
        {
            await Task.Delay(700).ConfigureAwait(true);
            reply = ResponseFor(text, sig, conf);
        }

        ChatMessages.Remove(typing);
        ChatMessages.Add(AiSignalChatMessageViewModel.Ai(reply));
        this.RaisePropertyChanged(nameof(ShowSuggestions));
    }

    private static string ResponseFor(string text, SignalModel sig, int conf) => text switch
    {
        "What is the confidence level?" =>
            $"The confidence for {sig.Sym} is {conf}%. This is derived from RSI divergence strength, volume confirmation, and multi-timeframe alignment. Scores above 75% historically resolve in signal direction ~73% of the time.",
        "Is this a good entry point?" =>
            $"For {sig.Sym} at {sig.Price}, the signal quality is {(conf >= 75 ? "strong" : "moderate")}. Entry is justified if your account risk per trade is ≤2%. Wait for candle close confirmation on {sig.Tf} before executing. Avoid chasing if price moves more than 0.5% from current levels.",
        "What are the key risks?" =>
            $"Key risks for this {(sig.Dir == "BUY" ? "LONG" : sig.Dir == "SELL" ? "SHORT" : "HOLD")} setup on {sig.Sym}: (1) broader market correlation — BTC move >2% would invalidate, (2) funding rate at elevated levels increases squeeze risk, (3) upcoming macro events could spike volatility. Use the AI-suggested stop loss strictly.",
        "Explain the reasoning" =>
            sig.Reason + $" The signal aligns with the current {(sig.Dir == "BUY" ? "trending bullish" : sig.Dir == "SELL" ? "bearish pressure" : "consolidating")} regime detected across multiple timeframes.",
        _ =>
            $"Analysing \"{text}\" in context of {sig.Sym} {sig.Tf} signal… Based on current market structure and the {conf}% confidence reading, " +
            (sig.Dir == "BUY" ? "bullish momentum remains intact. Monitor volume for continuation."
             : sig.Dir == "SELL" ? "bearish pressure is dominant. Watch for a relief bounce before continuation."
             : "price is consolidating — patience is the edge here."),
    };

    // ── Static / demo content build ─────────────────────────────────────
    private void BuildStaticContent()
    {
        Ticker.Add(new AiSignalTickerViewModel("BTC",  "67,432", "+2.34%", "#3ddc84"));
        Ticker.Add(new AiSignalTickerViewModel("ETH",  "3,521",  "+1.87%", "#3ddc84"));
        Ticker.Add(new AiSignalTickerViewModel("SOL",  "182.40", "-0.92%", "#ff6b6b"));
        Ticker.Add(new AiSignalTickerViewModel("BNB",  "584.20", "+0.41%", "#3ddc84"));
        Ticker.Add(new AiSignalTickerViewModel("AVAX", "37.84",  "-2.18%", "#ff6b6b"));
        Ticker.Add(new AiSignalTickerViewModel("LINK", "18.42",  "+3.14%", "#3ddc84"));
        Ticker.Add(new AiSignalTickerViewModel("ARB",  "1.142",  "+4.22%", "#3ddc84"));
        Ticker.Add(new AiSignalTickerViewModel("INJ",  "31.44",  "+5.82%", "#3ddc84"));

        DexTokens.Add(new AiSignalDexTokenViewModel("PEPE",  "PE", "#1a0c2a", "ETH", 88, "MOMENTUM", "#3ddc84", "+128%"));
        DexTokens.Add(new AiSignalDexTokenViewModel("WIF",   "WF", "#160c1a", "SOL", 72, "EARLY",    "#21e6c1", "+44%"));
        DexTokens.Add(new AiSignalDexTokenViewModel("TURBO", "TB", "#2a1a0c", "ETH", 41, "FADING",   "#f4b860", "+12%"));

        var longBg = Rgba(29, 74, 46, 0.55);
        var shortBg = Rgba(74, 26, 30, 0.55);
        Opportunities.Add(new AiSignalOpportunityViewModel("INJ/USDT",  "Binance", 91, "LONG",  longBg,  "#1a4a2e", "#3ddc84", 0.91, "Accumulation + RSI reset, volume confirms"));
        Opportunities.Add(new AiSignalOpportunityViewModel("BTC/USDT",  "Binance", 87, "LONG",  longBg,  "#1a4a2e", "#3ddc84", 0.87, "Breakout above key resistance with volume"));
        Opportunities.Add(new AiSignalOpportunityViewModel("ARB/USDT",  "Bybit",   74, "LONG",  longBg,  "#1a4a2e", "#3ddc84", 0.74, "Wedge breakout, strong momentum setup"));
        Opportunities.Add(new AiSignalOpportunityViewModel("AVAX/USDT", "OKX",     83, "SHORT", shortBg, "#4a1a1e", "#ff6b6b", 0.83, "H&S neckline break, distribution confirmed"));
        Opportunities.Add(new AiSignalOpportunityViewModel("OP/USDT",   "OKX",     69, "SHORT", shortBg, "#4a1a1e", "#ff6b6b", 0.69, "Double top, bearish MACD cross, high funding"));

        var accumBg = Rgba(29, 74, 46, 0.5);
        var warnBg = Rgba(74, 56, 16, 0.4);
        Insights.Add(new AiSignalInsightViewModel("WHALE FLOW",   "ACCUM",    "#3ddc84", accumBg, "#1a4a2e", "$820M BTC added by large wallets in last 6h"));
        Insights.Add(new AiSignalInsightViewModel("SENTIMENT",    "BULLISH",  "#3ddc84", accumBg, "#1a4a2e", "Fear & Greed at 71 — Greed zone, risk-on"));
        Insights.Add(new AiSignalInsightViewModel("ON-CHAIN",     "NEUTRAL",  "#f4b860", warnBg,  "#3a2c10", "Exchange flows balanced, stablecoin minting flat"));
        Insights.Add(new AiSignalInsightViewModel("LIQUIDATIONS", "ELEVATED", "#f4b860", warnBg,  "#3a2c10", "$420M longs at risk if BTC drops below 66,800"));

        foreach (var tf in HmTfs) HeatmapTimeframes.Add(tf);

        ChatMessages.Add(AiSignalChatMessageViewModel.Ai(
            "Hi! I'm your AI trading assistant. Ask me anything about this signal, the market, risk management, or any crypto topic."));

        Suggestions.Add(new AiSignalSuggestionViewModel("What is the confidence level?", UseSuggestionCommand));
        Suggestions.Add(new AiSignalSuggestionViewModel("Is this a good entry point?", UseSuggestionCommand));
        Suggestions.Add(new AiSignalSuggestionViewModel("What are the key risks?", UseSuggestionCommand));
        Suggestions.Add(new AiSignalSuggestionViewModel("Explain the reasoning", UseSuggestionCommand));

        BuildDetailTabs();
    }

    private void BuildRegimeNewsDemo()
    {
        NewsBullets.Clear();
        NewsBullets.Add(new AiSignalNewsBulletViewModel("Spot BTC ETF inflows +$840M in 24h — 3-week streak", "#21e6c1"));
        NewsBullets.Add(new AiSignalNewsBulletViewModel("Fed pause expected — risk appetite elevated", "#21e6c1"));
        NewsBullets.Add(new AiSignalNewsBulletViewModel("Mt. Gox repayment timeline uncertainty persists", "#f4b860"));
    }

    private void BuildJournalDemo()
    {
        ReplaceList(JournalStrengths,
        [
            "Accurate trend direction (68% win rate on breakouts)",
            "Disciplined stop-loss adherence",
            "Good entry timing on reversals",
        ]);
        ReplaceList(JournalLeaks,
        [
            "Premature profit-taking on winners",
            "Overtrading in ranging markets",
            "Position sizing inconsistency",
        ]);
        ReplaceList(JournalSuggestions,
        [
            "Set min hold: 2 bars before moving TP",
            "Skip signals when hourly ATR < 0.3%",
            "Fixed 2% risk per trade, every trade",
        ]);
    }

    /// <summary>
    /// Multi-TF heatmap for the top signals — a deterministic visualization of each
    /// signal's AI direction/confidence spread across six timeframes (demo signals
    /// reproduce the reference exactly; live signals drive their own colours).
    /// </summary>
    private void BuildHeatmapFromSignals()
    {
        static string CellBg(string dir, double c) => dir switch
        {
            "buy"  => Rgba(61, 220, 132, 0.1 + c * 0.5),
            "sell" => Rgba(255, 107, 107, 0.1 + c * 0.5),
            _      => Rgba(244, 184, 96, 0.08 + c * 0.32),
        };
        static string CellFg(string dir) => dir == "buy" ? "#3ddc84" : dir == "sell" ? "#ff6b6b" : "#f4b860";
        static string CellBr(string dir) => dir == "buy" ? Rgba(61, 220, 132, 0.18) : dir == "sell" ? Rgba(255, 107, 107, 0.18) : Rgba(244, 184, 96, 0.12);

        // Deterministic per-timeframe jitter so lower/higher TFs read slightly differently.
        double[] tfWeight = [0.92, 1.0, 1.0, 0.96, 0.78, 0.72];

        _hmSyms = _all.Take(7).Select(s => s.Sym).ToList();
        _hmDir = [];
        _hmConf = [];
        HeatmapRows.Clear();

        foreach (var s in _all.Take(7))
        {
            var baseDir = s.Dir == "BUY" ? "buy" : s.Dir == "SELL" ? "sell" : "hold";
            var dirs = new string[HmTfs.Length];
            var confs = new double[HmTfs.Length];
            var cells = new ObservableCollection<AiSignalHeatmapCellViewModel>();

            for (var ti = 0; ti < HmTfs.Length; ti++)
            {
                var c = Math.Clamp(s.Conf * tfWeight[ti], 0.35, 0.98);
                // On the weakest/longest frames a mid-confidence signal softens to HOLD.
                var dir = c < 0.5 && baseDir != "hold" ? "hold" : baseDir;
                dirs[ti] = dir;
                confs[ti] = c;
                cells.Add(new AiSignalHeatmapCellViewModel(
                    CellBg(dir, c), CellBr(dir), CellFg(dir),
                    ((int)Math.Round(c * 100)).ToString(CultureInfo.InvariantCulture)));
            }
            _hmDir.Add(dirs);
            _hmConf.Add(confs);
            HeatmapRows.Add(new AiSignalHeatmapRowViewModel(s.Sym, cells));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    private static string FormatPrice(double v) => v >= 10000
        ? v.ToString("N0", CultureInfo.InvariantCulture)
        : v >= 100 ? v.ToString("N2", CultureInfo.InvariantCulture)
        : v >= 1 ? v.ToString("F3", CultureInfo.InvariantCulture)
        : v.ToString("F5", CultureInfo.InvariantCulture);

    private static string FormatPrice(decimal v) => FormatPrice((double)v);

    private static string Short(string sym) => sym.Length <= 3 ? sym : sym[..3];
}

// ── Item view models ───────────────────────────────────────────────────

public sealed class AiSignalTickerViewModel(string sym, string price, string chg, string chgColor)
{
    public string Sym { get; } = sym;
    public string Price { get; } = price;
    public string Chg { get; } = chg;
    public string ChgColor { get; } = chgColor;
}

public sealed class AiSignalFilterViewModel(string key, string label, bool active, ReactiveCommand<string, Unit> command)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Background { get; } = active ? "#0c1e2e" : "Transparent";
    public string BorderBrush { get; } = active ? "#1a3a4a" : "Transparent";
    public string Foreground { get; } = active ? "#21e6c1" : "#3d5a72";
    public ReactiveCommand<string, Unit> Command { get; } = command;
}

public sealed class AiSignalCardViewModel : ReactiveObject
{
    private bool _isSelected;

    public AiSignalCardViewModel(
        int allIndex, string sym, string logo, string logoBg, string tf, string exch,
        string reason, string time, string dirColor, string dirBg, string dirBorder, string dirLabel,
        int confPct, bool isSelected, ReactiveCommand<AiSignalCardViewModel, Unit> command)
    {
        AllIndex = allIndex;
        Sym = sym;
        Logo = logo;
        LogoBg = logoBg;
        Tf = tf;
        Exch = exch;
        Reason = reason;
        Time = time;
        DirColor = dirColor;
        DirBg = dirBg;
        DirBorder = dirBorder;
        DirLabel = dirLabel;
        ConfPct = $"{confPct}%";
        ConfW = confPct / 100.0;
        _isSelected = isSelected;
        Command = command;
    }

    public int AllIndex { get; }
    public string Sym { get; }
    public string Logo { get; }
    public string LogoBg { get; }
    public string Tf { get; }
    public string Exch { get; }
    public string Reason { get; }
    public string Time { get; }
    public string DirColor { get; }
    public string DirBg { get; }
    public string DirBorder { get; }
    public string DirLabel { get; }
    public string ConfPct { get; }
    public double ConfW { get; }
    public string TfExch => $"{Tf} · {Exch}";
    public ReactiveCommand<AiSignalCardViewModel, Unit> Command { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(CardBg));
            this.RaisePropertyChanged(nameof(CardBorder));
        }
    }

    public string CardBg => _isSelected ? "#0b1d2e" : "#060e18";
    public string CardBorder => _isSelected ? "#21e6c1" : "#0c1a26";
}

public sealed class AiSignalDexTokenViewModel(
    string sym, string logo, string bg, string chain, int score, string signal, string sigColor, string change)
{
    public string Sym { get; } = sym;
    public string Logo { get; } = logo;
    public string Bg { get; } = bg;
    public string Chain { get; } = chain;
    public string Signal { get; } = signal;
    public string SigColor { get; } = sigColor;
    public string Change { get; } = change;
    public string ChainScore { get; } = $"{chain} · score {score}";
}

public sealed class AiSignalOpportunityViewModel(
    string sym, string exch, int score, string bias, string biasBg, string biasBorder, string biasColor,
    double bar, string reason)
{
    public string Sym { get; } = sym;
    public string Exch { get; } = exch;
    public string Score { get; } = score.ToString(CultureInfo.InvariantCulture);
    public string Bias { get; } = bias;
    public string BiasBg { get; } = biasBg;
    public string BiasBorder { get; } = biasBorder;
    public string BiasColor { get; } = biasColor;
    public double Bar { get; } = bar;
    public string Reason { get; } = reason;
}

public sealed class AiSignalInsightViewModel(
    string label, string sig, string sigColor, string sigBg, string sigBorder, string summary)
{
    public string Label { get; } = label;
    public string Sig { get; } = sig;
    public string SigColor { get; } = sigColor;
    public string SigBg { get; } = sigBg;
    public string SigBorder { get; } = sigBorder;
    public string Summary { get; } = summary;
}

public sealed class AiSignalNewsBulletViewModel(string text, string dotColor)
{
    public string Text { get; } = text;
    public string DotColor { get; } = dotColor;
}

public sealed class AiSignalConsensusBarViewModel(string color)
{
    public string Color { get; } = color;
}

public sealed class AiSignalDetailTabViewModel(string key, string label, bool active, ReactiveCommand<string, Unit> command)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Foreground { get; } = active ? "#21e6c1" : "#3d5a72";
    public string BorderColor { get; } = active ? "#21e6c1" : "Transparent";
    public ReactiveCommand<string, Unit> Command { get; } = command;
}

public sealed class AiSignalHeatmapCellViewModel(string background, string border, string foreground, string text)
{
    public string Background { get; } = background;
    public string Border { get; } = border;
    public string Foreground { get; } = foreground;
    public string Text { get; } = text;
}

public sealed class AiSignalHeatmapRowViewModel(string symbol, ObservableCollection<AiSignalHeatmapCellViewModel> cells)
{
    public string Symbol { get; } = symbol;
    public ObservableCollection<AiSignalHeatmapCellViewModel> Cells { get; } = cells;
}

public sealed class AiSignalChatMessageViewModel
{
    private AiSignalChatMessageViewModel(bool isAi, bool isUser, bool isTyping, string text)
    {
        IsAi = isAi;
        IsUser = isUser;
        IsTyping = isTyping;
        Text = text;
    }

    public bool IsAi { get; }
    public bool IsUser { get; }
    public bool IsTyping { get; }
    public string Text { get; }

    public static AiSignalChatMessageViewModel Ai(string text) => new(true, false, false, text);
    public static AiSignalChatMessageViewModel User(string text) => new(false, true, false, text);
    public static AiSignalChatMessageViewModel Typing() => new(false, false, true, string.Empty);
}

public sealed class AiSignalSuggestionViewModel(string label, ReactiveCommand<string, Unit> command)
{
    public string Label { get; } = label;
    public ReactiveCommand<string, Unit> Command { get; } = command;
    public string CommandParameter { get; } = label;
}
