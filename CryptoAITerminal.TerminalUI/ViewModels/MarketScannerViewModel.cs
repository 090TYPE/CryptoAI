using Avalonia.Media;
using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ════════════════════════════════════════════════════════════════════════════
//  Single scanner row
// ════════════════════════════════════════════════════════════════════════════

public sealed class ScanRowVM : ReactiveObject
{
    private ScanResult _data;

    public ScanRowVM(ScanResult d) => _data = d;

    public string Symbol    => _data.Symbol;
    public string Exchange  => _data.Exchange;

    public string PriceLabel   => FmtPrice(_data.LastPrice);
    public string ChangeLabel  => $"{_data.ChangePct24h:+0.00;-0.00}%";
    public string VolumeLabel  => FmtVolume(_data.Volume24hUsd);
    public string RsiLabel     => _data.Rsi14 > 0 ? $"{_data.Rsi14:F0}" : "—";
    public string ActivityLabel => $"{_data.ActivityScore:F0}";
    public string HighLowLabel  =>
        _data.High24h > 0 ? $"{FmtPrice(_data.High24h)} / {FmtPrice(_data.Low24h)}" : "—";

    public bool   IsHot         => _data.IsHot;
    public bool   IsGainer      => _data.ChangePct24h > 0;
    public bool   IsLoser       => _data.ChangePct24h < 0;

    // Brush properties (no converters needed)
    public IBrush ChangeBrush   =>
        _data.ChangePct24h >= 2m  ? Teal  :
        _data.ChangePct24h >= 0   ? LightTeal :
        _data.ChangePct24h >= -2m ? LightRed :
                                     Red;

    public IBrush RsiBrush =>
        _data.Rsi14 <= 30  ? Teal  :
        _data.Rsi14 >= 70  ? Amber :
                              Gray;

    public IBrush RowBackground =>
        _data.IsHot ? HotBg : Brushes.Transparent;

    public IBrush HotBadgeBrush =>
        _data.IsHot ? HotBadge : Brushes.Transparent;

    public string HotBadgeText =>
        _data.Rsi14 > 0 && _data.Rsi14 <= 30 ? "💎" :
        _data.Rsi14 >= 70                      ? "⚡" :
        Math.Abs(_data.ChangePct24h) >= 5m     ? "🚀" :
        _data.ActivityScore >= 20              ? "🔥" :
                                                  "";

    public void RefreshFrom(ScanResult d)
    {
        _data = d;
        this.RaisePropertyChanged(string.Empty);
    }

    private static string FmtPrice(decimal p) =>
        p >= 1000 ? $"${p:N2}" :
        p >= 1    ? $"${p:N4}" :
        p > 0     ? $"${p:N6}" : "—";

    private static string FmtVolume(decimal v) =>
        v >= 1_000_000_000 ? $"${v / 1_000_000_000:F2}B" :
        v >= 1_000_000     ? $"${v / 1_000_000:F1}M"     :
        v >= 1_000         ? $"${v / 1_000:F0}K"          :
        v > 0              ? $"${v:F0}" : "—";

    private static readonly IBrush Teal      = new SolidColorBrush(Color.Parse("#21E6C1"));
    private static readonly IBrush LightTeal = new SolidColorBrush(Color.Parse("#5FA89A"));
    private static readonly IBrush LightRed  = new SolidColorBrush(Color.Parse("#B05555"));
    private static readonly IBrush Red       = new SolidColorBrush(Color.Parse("#FF5555"));
    private static readonly IBrush Amber     = new SolidColorBrush(Color.Parse("#F4B860"));
    private static readonly IBrush Gray      = new SolidColorBrush(Color.Parse("#5C6E82"));
    private static readonly IBrush HotBg     = new SolidColorBrush(Color.Parse("#0F1E14"));
    private static readonly IBrush HotBadge  = new SolidColorBrush(Color.Parse("#FF6B00"));
}

// ════════════════════════════════════════════════════════════════════════════
//  Alert row
// ════════════════════════════════════════════════════════════════════════════

public sealed class AlertRowVM
{
    private readonly ScannerAlert _alert;

    public AlertRowVM(ScannerAlert a) => _alert = a;

    public string Symbol     => _alert.Symbol;
    public string Message    => _alert.Message;
    public string TimeLabel  => _alert.TriggeredAt.ToLocalTime().ToString("HH:mm:ss");

    public IBrush SeverityBrush =>
        _alert.Severity == AlertSeverity.Hot     ? new SolidColorBrush(Color.Parse("#FF6B00")) :
        _alert.Severity == AlertSeverity.Warning ? new SolidColorBrush(Color.Parse("#F4B860")) :
                                                    new SolidColorBrush(Color.Parse("#5C6E82"));

    public string SeverityDot =>
        _alert.Severity == AlertSeverity.Hot     ? "●" :
        _alert.Severity == AlertSeverity.Warning ? "◆" : "○";
}

// ════════════════════════════════════════════════════════════════════════════
//  Price level row
// ════════════════════════════════════════════════════════════════════════════

public sealed class PriceLevelRowVM : ReactiveObject
{
    public PriceLevel Model { get; }
    public PriceLevelRowVM(PriceLevel m) => Model = m;

    public string Symbol       => Model.Symbol;
    public string PriceLabel   => $"${Model.Price:N2}";
    public string KindLabel    => Model.IsResistance ? "Res." : "Sup.";
    public string Note         => Model.Note;
    public bool   IsTriggered  => Model.Triggered;
    public IBrush KindBrush    => Model.IsResistance
        ? new SolidColorBrush(Color.Parse("#FF5555"))
        : new SolidColorBrush(Color.Parse("#21E6C1"));
}

// ════════════════════════════════════════════════════════════════════════════
//  Main ViewModel
// ════════════════════════════════════════════════════════════════════════════

/// <summary>One AI-ranked opportunity row in the scanner's "AI Picks" panel.</summary>
public sealed class AiPickVM
{
    public string Symbol { get; init; } = "";
    public int    Score  { get; init; }
    public string Bias   { get; init; } = "WATCH";
    public string Reason { get; init; } = "";
    public string ScoreLabel => $"{Score}";
    public string BiasBrush => Bias switch { "LONG" => "#3DDC84", "SHORT" => "#FF6B6B", _ => "#8FA3B8" };
}

public sealed class MarketScannerViewModel : ReactiveObject, IDisposable
{
    private readonly MarketScannerService _svc;
    private readonly Dictionary<string, ScanRowVM> _rowMap = new(StringComparer.OrdinalIgnoreCase);

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<ScanRowVM>       Rows    { get; } = [];
    public ObservableCollection<AlertRowVM>      Alerts  { get; } = [];
    public ObservableCollection<PriceLevelRowVM> Levels  { get; } = [];

    // ── AI ranking (#1) ─────────────────────────────────────────────────────────
    private readonly Services.MarketRankingAiService _aiRanker = new();
    private IReadOnlyList<ScanResult> _latestResults = [];
    public ObservableCollection<AiPickVM> AiPicks { get; } = [];

    private bool _aiRunning;
    private string _aiSource = "";
    public bool AiRunning { get => _aiRunning; private set => this.RaiseAndSetIfChanged(ref _aiRunning, value); }
    public string AiSource { get => _aiSource; private set => this.RaiseAndSetIfChanged(ref _aiSource, value); }
    public bool HasAiPicks => AiPicks.Count > 0;

    /// <summary>Share the Claude key/model (called by MainWindowViewModel).</summary>
    public void ConfigureAi(string apiKey, string model)
    {
        _aiRanker.ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(model)) _aiRanker.Model = model;
    }

    // ── Filter state ──────────────────────────────────────────────────────────

    private ScanPreset _activePreset = ScanPreset.All;
    private string     _symbolSearch = "";
    private decimal    _minRsi;
    private decimal    _maxRsi       = 100m;
    private decimal    _minChange;
    private decimal    _maxChange;
    private decimal    _minVolume;

    public ScanPreset ActivePreset
    {
        get => _activePreset;
        set { this.RaiseAndSetIfChanged(ref _activePreset, value); ApplyPreset(value); }
    }

    public string SymbolSearch
    {
        get => _symbolSearch;
        set { this.RaiseAndSetIfChanged(ref _symbolSearch, value); RebuildFilter(); }
    }

    public decimal MinRsi    { get => _minRsi;    set { this.RaiseAndSetIfChanged(ref _minRsi,    value); RebuildFilter(); } }
    public decimal MaxRsi    { get => _maxRsi;    set { this.RaiseAndSetIfChanged(ref _maxRsi,    value); RebuildFilter(); } }
    public decimal MinChange { get => _minChange; set { this.RaiseAndSetIfChanged(ref _minChange, value); RebuildFilter(); } }
    public decimal MaxChange { get => _maxChange; set { this.RaiseAndSetIfChanged(ref _maxChange, value); RebuildFilter(); } }
    public decimal MinVolume { get => _minVolume; set { this.RaiseAndSetIfChanged(ref _minVolume, value); RebuildFilter(); } }

    private ScannerFilter _filter = new();

    // ── Stats ─────────────────────────────────────────────────────────────────

    private int _totalSymbols;
    private int _hotCount;
    private int _gainersCount;
    private int _losersCount;

    public int TotalSymbols { get => _totalSymbols; private set { this.RaiseAndSetIfChanged(ref _totalSymbols, value); } }
    public int HotCount     { get => _hotCount;     private set { this.RaiseAndSetIfChanged(ref _hotCount,     value); } }
    public int GainersCount { get => _gainersCount; private set { this.RaiseAndSetIfChanged(ref _gainersCount, value); } }
    public int LosersCount  { get => _losersCount;  private set { this.RaiseAndSetIfChanged(ref _losersCount,  value); } }

    // ── Sort ──────────────────────────────────────────────────────────────────

    private string _sortColumn = "change";
    private bool   _sortDesc   = true;

    public string SortColumn { get => _sortColumn; set { this.RaiseAndSetIfChanged(ref _sortColumn, value); } }
    public bool   SortDesc   { get => _sortDesc;   set { this.RaiseAndSetIfChanged(ref _sortDesc,   value); } }

    // ── Price level inputs ────────────────────────────────────────────────────

    private string  _levelSymbol = "";
    private decimal _levelPrice;
    private bool    _levelIsResistance = true;
    private string  _levelNote  = "";

    public string  LevelSymbol       { get => _levelSymbol; set { this.RaiseAndSetIfChanged(ref _levelSymbol, value); } }
    public decimal LevelPrice        { get => _levelPrice;  set { this.RaiseAndSetIfChanged(ref _levelPrice,  value); } }
    public bool    LevelIsResistance { get => _levelIsResistance; set { this.RaiseAndSetIfChanged(ref _levelIsResistance, value); } }
    public string  LevelNote         { get => _levelNote;  set { this.RaiseAndSetIfChanged(ref _levelNote,  value); } }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<string,         Unit>   SetPresetCommand       { get; }
    public ReactiveCommand<ScanRowVM,      Unit>   GoToTradingCommand     { get; }
    public ReactiveCommand<Unit,           Unit>   AddLevelCommand        { get; }
    public ReactiveCommand<PriceLevelRowVM, Unit>  RemoveLevelCommand     { get; }
    public ReactiveCommand<string,         Unit>   SortByCommand          { get; }
    public ReactiveCommand<Unit,           Unit>   RankWithAiCommand      { get; }

    // ── Navigation callback ───────────────────────────────────────────────────

    /// <summary>
    /// Set by MainWindowViewModel. Called when user clicks a row to navigate to the trading screen.
    /// Parameter: symbol string (e.g. "BTCUSDT").
    /// </summary>
    public Action<string>? OnGoToTrading { get; set; }

    // ── Toast ─────────────────────────────────────────────────────────────────

    public event Action<string>? ToastRequested;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MarketScannerViewModel(MarketScannerService svc)
    {
        _svc = svc;
        _svc.ResultsUpdated += OnResultsUpdated;
        _svc.AlertFired     += OnAlertFired;

        SetPresetCommand   = ReactiveCommand.Create<string>(p =>
        {
            if (Enum.TryParse<ScanPreset>(p, true, out var preset))
                ActivePreset = preset;
        }, outputScheduler: App.UiScheduler);
        GoToTradingCommand = ReactiveCommand.Create<ScanRowVM>(row => OnGoToTrading?.Invoke(row.Symbol), outputScheduler: App.UiScheduler);
        AddLevelCommand    = ReactiveCommand.Create(AddLevel,    outputScheduler: App.UiScheduler);
        RemoveLevelCommand = ReactiveCommand.Create<PriceLevelRowVM>(RemoveLevel, outputScheduler: App.UiScheduler);
        SortByCommand      = ReactiveCommand.Create<string>(col =>
        {
            if (_sortColumn == col) SortDesc = !_sortDesc;
            else { SortColumn = col; SortDesc = true; }
        }, outputScheduler: App.UiScheduler);
        RankWithAiCommand  = ReactiveCommand.CreateFromTask(RankWithAiAsync, outputScheduler: App.UiScheduler);
    }

    private async Task RankWithAiAsync()
    {
        if (AiRunning) return;
        var snapshot = _latestResults;
        if (snapshot.Count == 0) { ToastRequested?.Invoke("Scanner has no data yet."); return; }

        AiRunning = true;
        try
        {
            var result = await _aiRanker.RankAsync(snapshot, topN: 5).ConfigureAwait(true);
            AiPicks.Clear();
            foreach (var o in result.Opportunities)
                AiPicks.Add(new AiPickVM { Symbol = o.Symbol, Score = o.Score, Bias = o.Bias, Reason = o.Reason });
            AiSource = result.Source;
            this.RaisePropertyChanged(nameof(HasAiPicks));
        }
        catch (Exception ex) { ToastRequested?.Invoke($"AI ranking failed: {ex.Message}"); }
        finally { AiRunning = false; }
    }

    // ── Data updates ──────────────────────────────────────────────────────────

    private void OnResultsUpdated(IReadOnlyList<ScanResult> allResults)
    {
        _latestResults = allResults;
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Stats (computed from full unfiltered set)
            TotalSymbols = allResults.Count;
            HotCount     = allResults.Count(r => r.IsHot);
            GainersCount = allResults.Count(r => r.ChangePct24h > 0);
            LosersCount  = allResults.Count(r => r.ChangePct24h < 0);

            // Apply filter
            var filtered = allResults.Where(r => _filter.Matches(r)).ToList();

            // Sort
            var sorted = Sort(filtered);

            // In-place update
            var toRemove = _rowMap.Keys.Except(sorted.Select(r => r.Symbol), StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var sym in toRemove)
            {
                if (_rowMap.TryGetValue(sym, out var old))
                {
                    Rows.Remove(old);
                    _rowMap.Remove(sym);
                }
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                var r = sorted[i];
                if (_rowMap.TryGetValue(r.Symbol, out var existing))
                {
                    existing.RefreshFrom(r);
                    // Reposition if needed
                    var curIdx = Rows.IndexOf(existing);
                    if (curIdx != i)
                    {
                        Rows.RemoveAt(curIdx);
                        Rows.Insert(Math.Min(i, Rows.Count), existing);
                    }
                }
                else
                {
                    var row = new ScanRowVM(r);
                    _rowMap[r.Symbol] = row;
                    Rows.Insert(Math.Min(i, Rows.Count), row);
                }
            }
        });
    }

    private void OnAlertFired(ScannerAlert alert)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var row = new AlertRowVM(alert);
            Alerts.Insert(0, row);
            if (Alerts.Count > 100) Alerts.RemoveAt(Alerts.Count - 1);
            ToastRequested?.Invoke($"[{alert.Symbol}] {alert.Message}");
        });
    }

    // ── Filter/preset ─────────────────────────────────────────────────────────

    private void ApplyPreset(ScanPreset preset)
    {
        switch (preset)
        {
            case ScanPreset.Gainers:
                _minChange = 2m; _maxChange = 0; _minRsi = 0; _maxRsi = 100;
                break;
            case ScanPreset.Losers:
                _maxChange = -2m; _minChange = 0; _minRsi = 0; _maxRsi = 100;
                break;
            case ScanPreset.Oversold:
                _maxRsi = 30m; _minRsi = 0; _minChange = 0; _maxChange = 0;
                break;
            case ScanPreset.Overbought:
                _minRsi = 70m; _maxRsi = 100; _minChange = 0; _maxChange = 0;
                break;
            case ScanPreset.Hot:
                _minChange = 5m; _maxChange = 0; _minRsi = 0; _maxRsi = 100;
                break;
            default:
                _minChange = 0; _maxChange = 0; _minRsi = 0; _maxRsi = 100;
                break;
        }

        this.RaisePropertyChanged(nameof(MinChange));
        this.RaisePropertyChanged(nameof(MaxChange));
        this.RaisePropertyChanged(nameof(MinRsi));
        this.RaisePropertyChanged(nameof(MaxRsi));
        RebuildFilter();
    }

    private void RebuildFilter()
    {
        _filter = new ScannerFilter
        {
            SymbolContains   = string.IsNullOrWhiteSpace(_symbolSearch) ? null : _symbolSearch,
            MinChangePct     = _minChange == 0 ? null : _minChange,
            MaxChangePct     = _maxChange == 0 ? null : _maxChange,
            MinRsi           = _minRsi    == 0 ? null : _minRsi,
            MaxRsi           = _maxRsi   == 100 ? null : _maxRsi,
            MinVolume24hUsd  = _minVolume == 0 ? null : _minVolume,
        };
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    private List<ScanResult> Sort(List<ScanResult> list)
    {
        IEnumerable<ScanResult> q = list;
        Func<ScanResult, object> key = _sortColumn switch
        {
            "symbol"   => r => r.Symbol,
            "price"    => r => r.LastPrice,
            "change"   => r => r.ChangePct24h,
            "volume"   => r => r.Volume24hUsd,
            "rsi"      => r => r.Rsi14,
            "activity" => r => r.ActivityScore,
            _          => r => r.ChangePct24h,
        };

        q = _sortDesc
            ? q.OrderByDescending(r => r.IsHot).ThenByDescending(key)
            : q.OrderByDescending(r => r.IsHot).ThenBy(key);

        return q.ToList();
    }

    // ── Price levels ──────────────────────────────────────────────────────────

    private void AddLevel()
    {
        if (string.IsNullOrWhiteSpace(_levelSymbol) || _levelPrice <= 0) return;
        var lvl = new PriceLevel
        {
            Symbol       = _levelSymbol.ToUpperInvariant().Trim(),
            Price        = _levelPrice,
            IsResistance = _levelIsResistance,
            Note         = _levelNote,
        };
        _svc.AddPriceLevel(lvl);
        Levels.Add(new PriceLevelRowVM(lvl));
        ToastRequested?.Invoke($"Level {lvl.Symbol} ${lvl.Price:N2} added");
    }

    private void RemoveLevel(PriceLevelRowVM row)
    {
        _svc.RemovePriceLevel(row.Model.Id);
        Levels.Remove(row);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _svc.ResultsUpdated -= OnResultsUpdated;
        _svc.AlertFired     -= OnAlertFired;
    }
}
