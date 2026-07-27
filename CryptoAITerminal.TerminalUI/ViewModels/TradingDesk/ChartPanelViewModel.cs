using System;
using System.Collections.Generic;
using System.Reactive;
using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.TradingDesk;

/// <summary>
/// The CEX desk's chart panel: the metric strip above the chart, the two toolbar rows
/// (timeframe · chart type · zoom · alert · fullscreen, then drawing tools · indicators) and the
/// chart's own render flags.
///
/// It owns the toolbar state and nothing beyond it. Everything the toolbar *triggers* outside the
/// chart — reloading candles, re-pointing the market's timeframe, resetting the drawing phase,
/// arming a real price alert — is orchestration, and orchestration stays in
/// <see cref="MainWindowViewModel"/>. The split is the same one
/// <see cref="MarketFeedOwnerViewModel"/> uses: this view model writes its own state, then raises
/// an event, and the shell's handler does verbatim what its own method used to do next.
///
/// The metric strip (mark price, spread, high/low/volume) and the candle source are read-through:
/// they are derived from shell state — market data, the order book, the DEX desk — that only the
/// shell can compute, so the accessors are handed in and the shell calls
/// <see cref="RaiseMarketStateChanged"/> whenever it re-raises those values on itself.
/// </summary>
public sealed class ChartPanelViewModel : ReactiveObject
{
    private readonly Func<decimal> _currentTradePrice;
    private readonly Func<string> _selectedTradingSymbol;
    private readonly Func<IReadOnlyList<DexOhlcvPoint>> _activeTradingCandles;
    private readonly Func<string> _spreadLabel;
    private readonly Func<string> _chartHighLabel;
    private readonly Func<string> _chartLowLabel;
    private readonly Func<string> _chartVolumeLabel;
    private readonly Action<string> _log;

    private string _selectedTradeTimeframe = "1M";
    private string _selectedChartType = "Candles";
    private string _selectedChartTool = "Cursor";

    private bool _showMa20 = true;
    private bool _showMa50 = true;
    private bool _showBollinger;
    private bool _showRsi;
    private bool _showWalls = true;

    // Both default on, matching CexCandlestickChart's own defaults — the overlays were always
    // drawn, there was just no button to turn them off.
    private bool _showVwap = true;
    private bool _showVolumeProfile = true;

    private int _chartZoomInVersion;
    private int _chartZoomOutVersion;
    private int _chartZoomPercent = 80;
    private int _chartClearDrawingsVersion;
    private int _chartResetViewVersion;

    private bool _isChartExpanded;
    private bool _showChartAlertInput;
    private string _chartAlertPriceInput = string.Empty;

    /// <param name="log">The shell's <c>AddLog</c> — the toolbar writes the same lines it always did.</param>
    public ChartPanelViewModel(
        Func<decimal> currentTradePrice,
        Func<string> selectedTradingSymbol,
        Func<IReadOnlyList<DexOhlcvPoint>> activeTradingCandles,
        Func<string> spreadLabel,
        Func<string> chartHighLabel,
        Func<string> chartLowLabel,
        Func<string> chartVolumeLabel,
        Action<string> log)
    {
        _currentTradePrice = currentTradePrice;
        _selectedTradingSymbol = selectedTradingSymbol;
        _activeTradingCandles = activeTradingCandles;
        _spreadLabel = spreadLabel;
        _chartHighLabel = chartHighLabel;
        _chartLowLabel = chartLowLabel;
        _chartVolumeLabel = chartVolumeLabel;
        _log = log;

        SelectTradeTimeframeCommand = ReactiveCommand.Create<string>(SelectTradeTimeframe, outputScheduler: App.UiScheduler);
        SelectChartTypeCommand = ReactiveCommand.Create<string>(SelectChartType, outputScheduler: App.UiScheduler);
        ToggleChartIndicatorCommand = ReactiveCommand.Create<string>(ToggleChartIndicator, outputScheduler: App.UiScheduler);
        ZoomChartInCommand = ReactiveCommand.Create(ZoomChartIn, outputScheduler: App.UiScheduler);
        ZoomChartOutCommand = ReactiveCommand.Create(ZoomChartOut, outputScheduler: App.UiScheduler);
        ToggleChartFullscreenCommand = ReactiveCommand.Create(ToggleChartFullscreen, outputScheduler: App.UiScheduler);
        ChartAlertCommand = ReactiveCommand.Create(ToggleOrArmChartAlert, outputScheduler: App.UiScheduler);
        SelectChartToolCommand = ReactiveCommand.Create<string>(SelectChartTool, outputScheduler: App.UiScheduler);
        ClearChartDrawingsCommand = ReactiveCommand.Create(ClearChartDrawings, outputScheduler: App.UiScheduler);
        ResetChartViewCommand = ReactiveCommand.Create(ResetChartView, outputScheduler: App.UiScheduler);
    }

    // ═════════════════════════ shell wiring ═════════════════════════════════

    /// <summary>
    /// Raised after <see cref="SelectedTradeTimeframe"/> has been written by the toolbar. The shell
    /// re-points the active market, forces the next candle refresh to focus the latest bar, reloads
    /// the candles and re-raises its timeframe chips from the handler.
    /// </summary>
    public event Action<string>? TimeframeChanged;

    /// <summary>
    /// Raised after <see cref="SelectedChartTool"/> has been written. The shell resets the drawing
    /// phase and re-raises its toolbar chips from the handler.
    /// </summary>
    public event Action? ChartToolChanged;

    /// <summary>
    /// Raised when the alert button is pressed a second time with a valid price. The shell owns the
    /// alerts desk, so it is the one that arms the alert.
    /// </summary>
    public event Action<decimal>? AlertArmRequested;

    /// <summary>
    /// Re-raises the read-through metric strip and candle source. Called by the shell from the same
    /// places where it re-raises those values on itself.
    /// </summary>
    public void RaiseMarketStateChanged()
    {
        this.RaisePropertyChanged(nameof(CurrentTradePrice));
        this.RaisePropertyChanged(nameof(SelectedTradingSymbol));
        this.RaisePropertyChanged(nameof(ActiveTradingCandles));
        this.RaisePropertyChanged(nameof(SpreadLabel));
        this.RaisePropertyChanged(nameof(ChartHighLabel));
        this.RaisePropertyChanged(nameof(ChartLowLabel));
        this.RaisePropertyChanged(nameof(ChartVolumeLabel));
    }

    // ═════════════════════════ metric strip (read-through) ══════════════════

    /// <summary>Mark price shown above the chart — owned by the shell, mirrored here for binding.</summary>
    public decimal CurrentTradePrice => _currentTradePrice();

    /// <summary>Active symbol; the chart uses it as its drawing-persistence key.</summary>
    public string SelectedTradingSymbol => _selectedTradingSymbol();

    /// <summary>CEX or DEX candles, whichever desk is active.</summary>
    public IReadOnlyList<DexOhlcvPoint> ActiveTradingCandles => _activeTradingCandles();

    public string SpreadLabel => _spreadLabel();
    public string ChartHighLabel => _chartHighLabel();
    public string ChartLowLabel => _chartLowLabel();
    public string ChartVolumeLabel => _chartVolumeLabel();

    // ═════════════════════════ commands ═════════════════════════════════════

    public ReactiveCommand<string, Unit> SelectTradeTimeframeCommand { get; }
    public ReactiveCommand<string, Unit> SelectChartTypeCommand { get; }
    public ReactiveCommand<string, Unit> ToggleChartIndicatorCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomChartInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomChartOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleChartFullscreenCommand { get; }
    public ReactiveCommand<Unit, Unit> ChartAlertCommand { get; }
    public ReactiveCommand<string, Unit> SelectChartToolCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearChartDrawingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetChartViewCommand { get; }

    // ═════════════════════════ timeframe ════════════════════════════════════

    public string SelectedTradeTimeframe
    {
        get => _selectedTradeTimeframe;
        set => this.RaiseAndSetIfChanged(ref _selectedTradeTimeframe, value);
    }

    private void SelectTradeTimeframe(string timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            return;
        }

        SelectedTradeTimeframe = timeframe;
        TimeframeChanged?.Invoke(timeframe);
        _log($"Trading timeframe focus switched to {timeframe}.");
    }

    // ═════════════════════════ chart type + indicators ══════════════════════

    public string SelectedChartType
    {
        get => _selectedChartType;
        private set => this.RaiseAndSetIfChanged(ref _selectedChartType, value);
    }

    public bool ShowMa20
    {
        get => _showMa20;
        private set => this.RaiseAndSetIfChanged(ref _showMa20, value);
    }

    public bool ShowMa50
    {
        get => _showMa50;
        private set => this.RaiseAndSetIfChanged(ref _showMa50, value);
    }

    public bool ShowBollinger
    {
        get => _showBollinger;
        private set => this.RaiseAndSetIfChanged(ref _showBollinger, value);
    }

    public bool ShowRsi
    {
        get => _showRsi;
        private set => this.RaiseAndSetIfChanged(ref _showRsi, value);
    }

    public bool ShowWalls
    {
        get => _showWalls;
        private set => this.RaiseAndSetIfChanged(ref _showWalls, value);
    }

    public bool ShowVwap
    {
        get => _showVwap;
        private set => this.RaiseAndSetIfChanged(ref _showVwap, value);
    }

    public bool ShowVolumeProfile
    {
        get => _showVolumeProfile;
        private set => this.RaiseAndSetIfChanged(ref _showVolumeProfile, value);
    }

    private void SelectChartType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return;
        SelectedChartType = type;
    }

    private void ToggleChartIndicator(string indicator)
    {
        switch (indicator?.ToUpperInvariant())
        {
            case "MA20": ShowMa20 = !ShowMa20; break;
            case "MA50": ShowMa50 = !ShowMa50; break;
            case "BB": ShowBollinger = !ShowBollinger; break;
            case "RSI": ShowRsi = !ShowRsi; break;
            case "WALLS": ShowWalls = !ShowWalls; break;
            case "VWAP":
                ShowVwap = !ShowVwap;
                _log($"VWAP overlay {(ShowVwap ? "enabled" : "disabled")}.");
                break;
            case "VP":
                ShowVolumeProfile = !ShowVolumeProfile;
                _log($"Volume Profile {(ShowVolumeProfile ? "enabled" : "disabled")}.");
                break;
        }
    }

    // ═════════════════════════ zoom / fullscreen / alert ════════════════════

    public int ChartZoomInVersion
    {
        get => _chartZoomInVersion;
        private set => this.RaiseAndSetIfChanged(ref _chartZoomInVersion, value);
    }

    public int ChartZoomOutVersion
    {
        get => _chartZoomOutVersion;
        private set => this.RaiseAndSetIfChanged(ref _chartZoomOutVersion, value);
    }

    public string ChartZoomLabel => $"{_chartZoomPercent}%";

    public bool IsChartExpanded
    {
        get => _isChartExpanded;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isChartExpanded, value);
            this.RaisePropertyChanged(nameof(ChartHeight));
            this.RaisePropertyChanged(nameof(ChartFullscreenGlyph));
        }
    }

    public double ChartHeight => _isChartExpanded ? 620d : 360d;
    public string ChartFullscreenGlyph => _isChartExpanded ? "⤢" : "⛶";

    public bool ShowChartAlertInput
    {
        get => _showChartAlertInput;
        private set => this.RaiseAndSetIfChanged(ref _showChartAlertInput, value);
    }

    public string ChartAlertPriceInput
    {
        get => _chartAlertPriceInput;
        set => this.RaiseAndSetIfChanged(ref _chartAlertPriceInput, value);
    }

    private void ZoomChartIn()
    {
        _chartZoomPercent = Math.Min(200, _chartZoomPercent + 10);
        this.RaisePropertyChanged(nameof(ChartZoomLabel));
        ChartZoomInVersion++;
    }

    private void ZoomChartOut()
    {
        _chartZoomPercent = Math.Max(20, _chartZoomPercent - 10);
        this.RaisePropertyChanged(nameof(ChartZoomLabel));
        ChartZoomOutVersion++;
    }

    private void ToggleChartFullscreen() => IsChartExpanded = !IsChartExpanded;

    /// <summary>First click reveals the price field; second (with a valid price) arms a real alert.</summary>
    private void ToggleOrArmChartAlert()
    {
        if (!ShowChartAlertInput)
        {
            ShowChartAlertInput = true;
            return;
        }

        if (decimal.TryParse(_chartAlertPriceInput, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var price) && price > 0m)
        {
            AlertArmRequested?.Invoke(price);
        }

        ChartAlertPriceInput = string.Empty;
        ShowChartAlertInput = false;
    }

    // ═════════════════════════ drawing tools ════════════════════════════════

    public string SelectedChartTool
    {
        get => _selectedChartTool;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedChartTool, value);
            this.RaisePropertyChanged(nameof(ChartInteractionHint));
        }
    }

    /// <summary>
    /// What the active drawing tool expects the operator to do next. Shown under the chart, which
    /// is the only guidance the drawing tools have — clicking TREND or CHANNEL otherwise gives no
    /// hint that more than one click is needed.
    /// </summary>
    public string ChartInteractionHint => _selectedChartTool switch
    {
        "Trend" => "Trend line: click first point, then second point on the chart.",
        "Horizontal" => "Horizontal line: click a price level on the chart.",
        "Rectangle" => "Rectangle: click first corner, then opposite corner.",
        "Channel" => "Channel: click first point, second point, then width.",
        "Erase" => "Erase mode: click a drawing to remove only that one.",
        _ => "Cursor mode: mouse wheel zooms, drag moves history, hover shows time and price."
    };

    /// <summary>Bumped to tell <c>CexCandlestickChart</c> to drop every saved drawing.</summary>
    public int ChartClearDrawingsVersion
    {
        get => _chartClearDrawingsVersion;
        set => this.RaiseAndSetIfChanged(ref _chartClearDrawingsVersion, value);
    }

    /// <summary>Bumped to tell <c>CexCandlestickChart</c> to re-frame on the latest candles.</summary>
    public int ChartResetViewVersion
    {
        get => _chartResetViewVersion;
        set => this.RaiseAndSetIfChanged(ref _chartResetViewVersion, value);
    }

    private void SelectChartTool(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return;
        }

        SelectedChartTool = tool;
        ChartToolChanged?.Invoke();
        _log($"Chart tool switched to {tool}.");
    }

    private void ClearChartDrawings()
    {
        ChartClearDrawingsVersion++;
        _log("Chart drawings cleared.");
    }

    private void ResetChartView()
    {
        ChartResetViewVersion++;
        _log("Chart view reset.");
    }
}
