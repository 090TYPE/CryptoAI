using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Trading-desk redesign additions: per-venue price switcher (same token across
/// Binance/Bybit/OKX/KuCoin), a public trade tape, and chart type / indicator
/// toolbar state. Kept in a partial so the primary view model stays focused.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly string[] TradingVenueOrder = ["Binance", "Bybit", "OKX", "KuCoin"];

    private DispatcherTimer? _venueQuoteTimer;
    private DispatcherTimer? _tapeTimer;
    private bool _isVenueRefreshRunning;
    private bool _isTapeRefreshRunning;
    private readonly Services.MarketTapeService _marketTape = new();

    private string _selectedChartType = "Candles";
    private bool _showMa20 = true;
    private bool _showMa50 = true;
    private bool _showBollinger;
    private bool _showRsi;
    private bool _showWalls = true;

    private int _chartZoomInVersion;
    private int _chartZoomOutVersion;
    private int _chartZoomPercent = 80;
    private bool _isChartExpanded;
    private bool _showChartAlertInput;
    private string _chartAlertPriceInput = string.Empty;
    private string _selectedObDepth = "0.1";

    // ── Venue switcher ────────────────────────────────────────────────

    /// <summary>The four live venues shown as switchable tabs for the active symbol.</summary>
    public ObservableCollection<TradingVenueQuoteViewModel> TradingVenues { get; } = [];

    /// <summary>Public trade tape (most-recent first) for the active symbol on the active venue.</summary>
    public ObservableCollection<TapeTradeViewModel> TapeTrades { get; } = [];

    public bool ShowTapePlaceholder => TapeTrades.Count == 0;

    public ReactiveCommand<string, Unit> SelectTradingVenueQuoteCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SelectChartTypeCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> ToggleChartIndicatorCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SelectOrderTypeCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SelectMarketModeCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SelectTradingProfileCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SelectMarginModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomChartInCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomChartOutCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleChartFullscreenCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ChartAlertCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SelectObDepthCommand { get; private set; } = null!;

    /// <summary>Order-book price-grouping preference shown in the depth toggle (0.01 / 0.1 / 1).</summary>
    public string SelectedObDepth
    {
        get => _selectedObDepth;
        private set => this.RaiseAndSetIfChanged(ref _selectedObDepth, value);
    }

    /// <summary>Taker fee shown on the exchange info strip.</summary>
    public string ExchangeFeeLabel => "0.10%";

    /// <summary>Live perp position side badge for the right-rail position card.</summary>
    public string PositionSideLabel => PositionQuantity > 0m ? "LONG" : PositionQuantity < 0m ? "SHORT" : "FLAT";
    public string PositionSideBrush => PositionQuantity > 0m ? SemanticColor.Keys.Positive : PositionQuantity < 0m ? SemanticColor.Keys.Negative : "#5a7a94";
    public string PositionSideBackground => PositionQuantity > 0m ? "#07221a" : PositionQuantity < 0m ? "#200a0a" : "#0a1622";
    public string ManualLeverageLabel => $"{ManualFuturesLeverage}×";

    private void InitializeTradingDesk()
    {
        foreach (var name in TradingVenueOrder)
        {
            TradingVenues.Add(new TradingVenueQuoteViewModel(name)
            {
                IsBase = string.Equals(name, "Binance", StringComparison.OrdinalIgnoreCase),
                IsSelected = string.Equals(name, _selectedSpotExchange, StringComparison.OrdinalIgnoreCase),
            });
        }

        SelectTradingVenueQuoteCommand = ReactiveCommand.Create<string>(SelectTradingVenueQuote, outputScheduler: App.UiScheduler);
        SelectChartTypeCommand = ReactiveCommand.Create<string>(SelectChartType, outputScheduler: App.UiScheduler);
        ToggleChartIndicatorCommand = ReactiveCommand.Create<string>(ToggleChartIndicator, outputScheduler: App.UiScheduler);
        SelectOrderTypeCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) SelectedOrderType = v; }, outputScheduler: App.UiScheduler);
        SelectMarketModeCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) SelectedCexMarketMode = v; }, outputScheduler: App.UiScheduler);
        SelectTradingProfileCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) SelectedTradingProfile = v; }, outputScheduler: App.UiScheduler);
        SelectMarginModeCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) ManualFuturesMarginMode = v; }, outputScheduler: App.UiScheduler);
        ZoomChartInCommand = ReactiveCommand.Create(ZoomChartIn, outputScheduler: App.UiScheduler);
        ZoomChartOutCommand = ReactiveCommand.Create(ZoomChartOut, outputScheduler: App.UiScheduler);
        ToggleChartFullscreenCommand = ReactiveCommand.Create(ToggleChartFullscreen, outputScheduler: App.UiScheduler);
        ChartAlertCommand = ReactiveCommand.Create(ToggleOrArmChartAlert, outputScheduler: App.UiScheduler);
        SelectObDepthCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) SelectedObDepth = v; }, outputScheduler: App.UiScheduler);

        _venueQuoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _venueQuoteTimer.Tick += (_, _) => _ = RefreshVenueQuotesAsync();
        _venueQuoteTimer.Start();

        _tapeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _tapeTimer.Tick += (_, _) => _ = RefreshTapeAsync();
        _tapeTimer.Start();

        _ = RefreshVenueQuotesAsync();
        _ = RefreshTapeAsync();
    }

    /// <summary>Called from the SelectedMarket setter when the active symbol changes.</summary>
    private void OnTradingSymbolChanged()
    {
        var activeSymbol = SelectedTradingSymbol;
        foreach (var market in Markets)
        {
            market.IsActive = string.Equals(market.Symbol, activeSymbol, StringComparison.OrdinalIgnoreCase);
        }

        // Reset venue prices so a stale delta from the previous symbol never lingers.
        foreach (var venue in TradingVenues)
        {
            venue.Update(0m, 0m, hasData: false);
        }

        TapeTrades.Clear();
        this.RaisePropertyChanged(nameof(ShowTapePlaceholder));

        _ = RefreshVenueQuotesAsync();
        _ = RefreshTapeAsync();
    }

    private void SelectTradingVenueQuote(string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return;
        }

        // Point both spot and futures order routing / display at the chosen venue,
        // so the header, order book and tape all re-source from it.
        SelectedSpotExchange = exchange;
        SelectedFuturesExchange = exchange;

        foreach (var venue in TradingVenues)
        {
            venue.IsSelected = string.Equals(venue.Exchange, exchange, StringComparison.OrdinalIgnoreCase);
        }

        _ = RefreshTapeAsync();
    }

    private async Task RefreshVenueQuotesAsync()
    {
        if (_isVenueRefreshRunning || _spotGatewaysMap is null)
        {
            return;
        }

        var symbol = SelectedTradingSymbol;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        _isVenueRefreshRunning = true;
        try
        {
            var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            async Task<(string Exchange, decimal Mid)> QuoteAsync(string exchange)
            {
                if (!_spotGatewaysMap.TryGetValue(exchange, out var gateway))
                {
                    return (exchange, 0m);
                }

                try
                {
                    var book = await gateway.GetOrderBookAsync(symbol, depth: 5);
                    var bestBid = book.Bids.Count > 0 ? book.Bids.Max(l => l.Price) : 0m;
                    var bestAsk = book.Asks.Count > 0 ? book.Asks.Min(l => l.Price) : 0m;
                    var mid = bestBid > 0m && bestAsk > 0m ? (bestBid + bestAsk) / 2m
                        : bestBid > 0m ? bestBid
                        : bestAsk;
                    return (exchange, mid);
                }
                catch
                {
                    return (exchange, 0m);
                }
            }

            var results = await Task.WhenAll(TradingVenueOrder.Select(QuoteAsync));
            foreach (var (exchange, mid) in results)
            {
                prices[exchange] = mid;
            }

            var basePrice = prices.TryGetValue("Binance", out var bp) && bp > 0m
                ? bp
                : prices.Values.FirstOrDefault(p => p > 0m);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Guard against a symbol change mid-flight.
                if (!string.Equals(symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                foreach (var venue in TradingVenues)
                {
                    var price = prices.TryGetValue(venue.Exchange, out var p) ? p : 0m;
                    venue.Update(price, basePrice, hasData: price > 0m);
                }
            });
        }
        finally
        {
            _isVenueRefreshRunning = false;
        }
    }

    private async Task RefreshTapeAsync()
    {
        if (_isTapeRefreshRunning)
        {
            return;
        }

        var symbol = SelectedTradingSymbol;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        _isTapeRefreshRunning = true;
        try
        {
            // Keyless public fills for the symbol (Binance /api/v3/trades) — works
            // for every venue selection and needs no private API credentials.
            IReadOnlyList<Services.TapeTrade> trades;
            try
            {
                trades = await _marketTape.GetRecentTradesAsync(symbol, limit: 30);
            }
            catch
            {
                return;
            }

            var ordered = trades
                .Where(t => t.Price > 0m)
                .OrderByDescending(t => t.TimeUtc)
                .Take(24)
                .ToList();

            if (ordered.Count == 0)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                TapeTrades.Clear();
                foreach (var trade in ordered)
                {
                    TapeTrades.Add(new TapeTradeViewModel(
                        string.Equals(trade.Side, "BUY", StringComparison.OrdinalIgnoreCase),
                        trade.Price,
                        trade.Quantity,
                        trade.TimeUtc.ToLocalTime()));
                }

                this.RaisePropertyChanged(nameof(ShowTapePlaceholder));
            });
        }
        finally
        {
            _isTapeRefreshRunning = false;
        }
    }

    // ── Chart type + indicators ───────────────────────────────────────

    public string SelectedChartType
    {
        get => _selectedChartType;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedChartType, value);
            RaiseChartToolbarState();
        }
    }

    public bool ShowMa20
    {
        get => _showMa20;
        private set { this.RaiseAndSetIfChanged(ref _showMa20, value); RaiseChartToolbarState(); }
    }

    public bool ShowMa50
    {
        get => _showMa50;
        private set { this.RaiseAndSetIfChanged(ref _showMa50, value); RaiseChartToolbarState(); }
    }

    public bool ShowBollinger
    {
        get => _showBollinger;
        private set { this.RaiseAndSetIfChanged(ref _showBollinger, value); RaiseChartToolbarState(); }
    }

    public bool ShowRsi
    {
        get => _showRsi;
        private set { this.RaiseAndSetIfChanged(ref _showRsi, value); RaiseChartToolbarState(); }
    }

    public bool ShowWalls
    {
        get => _showWalls;
        private set { this.RaiseAndSetIfChanged(ref _showWalls, value); RaiseChartToolbarState(); }
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
        }
    }

    // ── Chart zoom / fullscreen / alert (toolbar row-1 right cluster) ──

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

    /// <summary>Compact execution-guard badge for the ticket (mockup shows PASS/BLOCK).</summary>
    public string GuardPassLabel => CanPlacePrimaryOrder ? "PASS" : "BLOCK";

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
            AlertsVM.NewAlertSymbol = SelectedTradingSymbol;
            AlertsVM.NewAlertThreshold = price;
            AlertsVM.SelectedCondition = price >= CurrentTradePrice ? "PriceAbove" : "PriceBelow";
            AlertsVM.AddAlertCommand.Execute().Subscribe();
            AddLog($"Price alert armed: {SelectedTradingSymbol} @ {price}");
        }

        ChartAlertPriceInput = string.Empty;
        ShowChartAlertInput = false;
    }

    private static string ChipBg(bool active) => active ? "#0E2A2E" : "#07111A";
    private static string ChipFg(bool active) => active ? "#21E6C1" : "#8FA3B8";

    public string ChartTypeCandlesBackground => ChipBg(SelectedChartType == "Candles");
    public string ChartTypeCandlesForeground => ChipFg(SelectedChartType == "Candles");
    public string ChartTypeLineBackground => ChipBg(SelectedChartType == "Line");
    public string ChartTypeLineForeground => ChipFg(SelectedChartType == "Line");
    public string ChartTypeAreaBackground => ChipBg(SelectedChartType == "Area");
    public string ChartTypeAreaForeground => ChipFg(SelectedChartType == "Area");
    public string ChartTypeHaBackground => ChipBg(SelectedChartType == "HA");
    public string ChartTypeHaForeground => ChipFg(SelectedChartType == "HA");

    public string Ma20IndicatorBackground => ChipBg(ShowMa20);
    public string Ma20IndicatorForeground => ChipFg(ShowMa20);
    public string Ma50IndicatorBackground => ChipBg(ShowMa50);
    public string Ma50IndicatorForeground => ChipFg(ShowMa50);
    public string BollingerIndicatorBackground => ChipBg(ShowBollinger);
    public string BollingerIndicatorForeground => ChipFg(ShowBollinger);
    public string RsiIndicatorBackground => ChipBg(ShowRsi);
    public string RsiIndicatorForeground => ChipFg(ShowRsi);
    public string WallsIndicatorBackground => ChipBg(ShowWalls);
    public string WallsIndicatorForeground => ChipFg(ShowWalls);

    private void RaiseChartToolbarState()
    {
        this.RaisePropertyChanged(nameof(ChartTypeCandlesBackground));
        this.RaisePropertyChanged(nameof(ChartTypeCandlesForeground));
        this.RaisePropertyChanged(nameof(ChartTypeLineBackground));
        this.RaisePropertyChanged(nameof(ChartTypeLineForeground));
        this.RaisePropertyChanged(nameof(ChartTypeAreaBackground));
        this.RaisePropertyChanged(nameof(ChartTypeAreaForeground));
        this.RaisePropertyChanged(nameof(ChartTypeHaBackground));
        this.RaisePropertyChanged(nameof(ChartTypeHaForeground));
        this.RaisePropertyChanged(nameof(Ma20IndicatorBackground));
        this.RaisePropertyChanged(nameof(Ma20IndicatorForeground));
        this.RaisePropertyChanged(nameof(Ma50IndicatorBackground));
        this.RaisePropertyChanged(nameof(Ma50IndicatorForeground));
        this.RaisePropertyChanged(nameof(BollingerIndicatorBackground));
        this.RaisePropertyChanged(nameof(BollingerIndicatorForeground));
        this.RaisePropertyChanged(nameof(RsiIndicatorBackground));
        this.RaisePropertyChanged(nameof(RsiIndicatorForeground));
        this.RaisePropertyChanged(nameof(WallsIndicatorBackground));
        this.RaisePropertyChanged(nameof(WallsIndicatorForeground));
    }

    private void StopTradingDeskTimers()
    {
        _venueQuoteTimer?.Stop();
        _tapeTimer?.Stop();
        _marketTape.Dispose();
    }
}
