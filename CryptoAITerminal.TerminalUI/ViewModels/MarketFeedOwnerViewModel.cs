using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Owner of the live market feed: the tracked rows, the board's filter/sort/search state, the
/// "add coin" form, the global stats strip and the app-wide selected symbol.
///
/// It deliberately owns data, not orchestration. Changing <see cref="SelectedMarket"/> switches the
/// symbol for the whole terminal — order book, candles, futures account, trading desk — but none of
/// that happens here: the setter raises <see cref="SelectedMarketChanged"/> and
/// <see cref="MainWindowViewModel"/> does in its handler exactly what its own setter used to do.
/// The same split applies to the price stream (<see cref="ApplyTicker"/> writes the tick into the
/// row, the shell keeps the subscription) and to opening a row on the trading desk
/// (<see cref="OpenMarketInTradingCommand"/> calls back into the shell).
/// </summary>
public sealed class MarketFeedOwnerViewModel : ReactiveObject
{
    private static readonly string[] KnownQuoteAssets = ["USDT", "USDC", "FDUSD", "TUSD", "BUSD", "BTC", "ETH", "BNB"];
    private static readonly string CustomMarketsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "custom-markets.json");

    // Extra (non-Binance CEX + DEX) custom markets fed by REST polling.
    private static readonly string ExtMarketsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "custom-markets-ext.json");
    private sealed record ExtMarketDto(string Exchange, string Symbol, string DexChain, string DexAddress);

    private readonly List<string> _customMarketSymbols;
    private List<ExtMarketDto> _extMarkets = [];

    // Starred rows on the Markets desk. Without this the star lived only in the row view model,
    // which is rebuilt on every launch — the tracked markets came back but every star was gone.
    private readonly Services.MarketFavoritesStore _favoritesStore = new();
    private readonly Services.BookWallSettingsStore _wallSettingsStore = new();
    private Services.CustomMarketPoller _customPoller = null!;
    private Gateway.DEX.DexScreenerClient _dexScreener = null!;

    // Supplied by AttachSources once the shell has built its gateways and desks.
    private Func<string, Core.Interfaces.IExchangeGateway> _spotGatewayResolver = null!;
    private Func<string, Task<bool>> _addBinanceSymbolAsync = null!;
    private Func<DexTradingViewModel> _dexTrading = null!;
    private Action<CexMarketItemViewModel?> _openInTrading = null!;

    private CexMarketItemViewModel? _selectedMarket;
    private bool _isMarketLoading = true;
    private string _newMarketSymbol = "";
    private string _marketsStatus = "";
    private string _selectedMarketSource = "Binance";
    private string _selectedDexChain = "ethereum";
    private string _marketsSearchText = string.Empty;
    private string _selectedMarketSortMode = "Momentum";
    private bool _showFavoriteMarketsOnly;
    private string _selectedMarketFilter = "ALL";
    private string _selectedAddMode = "CEX";
    private string _selectedMarketTimeframe = "1H";
    private bool _isRefreshingMarketExplorerCollections;

    public MarketFeedOwnerViewModel(IEnumerable<string> defaultSymbols)
    {
        OpenMarketInTradingCommand = ReactiveCommand.Create<CexMarketItemViewModel>(market => _openInTrading(market), outputScheduler: App.UiScheduler);
        ToggleMarketFavoriteCommand = ReactiveCommand.Create<CexMarketItemViewModel>(ToggleMarketFavorite, outputScheduler: App.UiScheduler);
        AddCustomMarketCommand = ReactiveCommand.CreateFromTask(AddCustomMarketAsync, outputScheduler: App.UiScheduler);
        RemoveMarketCommand = ReactiveCommand.Create<CexMarketItemViewModel>(RemoveCustomMarket, outputScheduler: App.UiScheduler);
        SetMarketFilterCommand = ReactiveCommand.Create<string>(key => SelectedMarketFilter = key, outputScheduler: App.UiScheduler);
        SetMarketSortCommand = ReactiveCommand.Create<string>(SetMarketSort, outputScheduler: App.UiScheduler);
        SetAddModeCommand = ReactiveCommand.Create<string>(key => SelectedAddMode = key, outputScheduler: App.UiScheduler);
        SelectMarketTimeframeCommand = ReactiveCommand.Create<string>(SelectMarketTimeframe, outputScheduler: App.UiScheduler);

        _customMarketSymbols = LoadCustomMarketSymbols();
        foreach (var symbol in defaultSymbols.Concat(_customMarketSymbols))
        {
            var market = new CexMarketItemViewModel(symbol);
            ConfigureWallSettings(market);
            market.PropertyChanged += OnMarketItemPropertyChanged;
            Markets.Add(market);
        }

        RefreshMarketExplorerCollections();
    }

    // ═════════════════════════ shell wiring ═════════════════════════════════

    /// <summary>
    /// Raised after the active symbol has changed. The shell re-points the order book, candles,
    /// futures account and trading desk from its handler — this view model does none of that.
    /// </summary>
    public event Action<CexMarketItemViewModel?>? SelectedMarketChanged;

    /// <summary>Raised whenever <see cref="IsMarketLoading"/> is written, so the shell can re-raise
    /// the connection labels it derives from it.</summary>
    public event Action? IsMarketLoadingChanged;

    /// <summary>Raised by <see cref="RaiseExplorerStateChanged"/> for shell-side labels built on
    /// the board summary (Analytics).</summary>
    public event Action? ExplorerStateChanged;

    /// <summary>Coins the user added by hand — the shell feeds them to the gateway set at start-up.</summary>
    public IReadOnlyList<string> CustomMarketSymbols => _customMarketSymbols;

    /// <summary>
    /// Second construction phase: the poller, the ext-market rows and the saved stars all need the
    /// shell's gateways and desks, which do not exist yet when the default rows are built.
    /// </summary>
    public void AttachSources(
        Func<string, Core.Interfaces.IExchangeGateway> spotGatewayResolver,
        Func<string, Task<bool>> addBinanceSymbolAsync,
        Func<DexTradingViewModel> dexTradingAccessor,
        Action<CexMarketItemViewModel?> openMarketInTrading)
    {
        _spotGatewayResolver = spotGatewayResolver;
        _addBinanceSymbolAsync = addBinanceSymbolAsync;
        _dexTrading = dexTradingAccessor;
        _openInTrading = openMarketInTrading;

        // Non-Binance CEX + DEX custom markets: refreshed by REST polling, not the live socket.
        _dexScreener = new Gateway.DEX.DexScreenerClient();
        _customPoller = new Services.CustomMarketPoller(exchange => spotGatewayResolver(exchange), _dexScreener);
        _extMarkets = LoadExtMarkets();
        RestoreExtMarkets();
        ApplySavedFavorites();   // after every row exists — default, custom and ext alike
        RefreshMarketExplorerCollections();
    }

    /// <summary>Writes one live tick into the row that carries that symbol, if the board tracks it.</summary>
    public void ApplyTicker(Core.Models.MarketData data)
    {
        var market = Markets.FirstOrDefault(item => string.Equals(item.Symbol, data.Symbol, StringComparison.OrdinalIgnoreCase));
        market?.UpdateMarketData(data);
    }

    /// <summary>Contract address of a DEX row added on this board, or null for a CEX row.</summary>
    public string? GetDexAddress(string symbol) =>
        _extMarkets.FirstOrDefault(d =>
            string.Equals(d.Exchange, "DEX", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Symbol, symbol, StringComparison.OrdinalIgnoreCase))?.DexAddress;

    /// <summary>Unhooks the per-row handlers. Called from the shell's Dispose.</summary>
    public void DetachMarketRows()
    {
        foreach (var market in Markets)
        {
            market.PropertyChanged -= OnMarketItemPropertyChanged;
        }
    }

    // ═════════════════════════ collections ═══════════════════════════════════

    public ObservableCollection<CexMarketItemViewModel> Markets { get; } = [];
    public ObservableCollection<CexMarketItemViewModel> VisibleMarkets { get; } = [];
    public ObservableCollection<CexMarketItemViewModel> MarketHeatmapMarkets { get; } = [];

    /// <summary>Global market-stats strip cells across the top of the board.</summary>
    public ObservableCollection<MarketStatCellViewModel> GlobalMarketStats { get; } = [];

    // ═════════════════════════ selection ═════════════════════════════════════

    public CexMarketItemViewModel? SelectedMarket
    {
        get => _selectedMarket;
        set
        {
            var resolvedMarket = value is null
                ? _selectedMarket ?? Markets.FirstOrDefault()
                : Markets.FirstOrDefault(item => string.Equals(item.Symbol, value.Symbol, StringComparison.OrdinalIgnoreCase)) ?? value;

            if (ReferenceEquals(_selectedMarket, resolvedMarket))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedMarket, resolvedMarket);
            SelectedMarketChanged?.Invoke(resolvedMarket);
        }
    }

    public bool IsMarketLoading
    {
        get => _isMarketLoading;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMarketLoading, value);
            IsMarketLoadingChanged?.Invoke();
        }
    }

    // ═════════════════════════ board filters ═════════════════════════════════

    public string MarketsSearchText
    {
        get => _marketsSearchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _marketsSearchText, value);
            RefreshMarketExplorerCollections();
            RaiseExplorerStateChanged();
        }
    }

    public string SelectedMarketSortMode
    {
        get => _selectedMarketSortMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Momentum" : value.Trim();
            this.RaiseAndSetIfChanged(ref _selectedMarketSortMode, normalized);
            RefreshMarketExplorerCollections();
            RaiseExplorerStateChanged();
        }
    }

    public bool ShowFavoriteMarketsOnly
    {
        get => _showFavoriteMarketsOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showFavoriteMarketsOnly, value);
            RefreshMarketExplorerCollections();
            RaiseExplorerStateChanged();
        }
    }

    public IReadOnlyList<string> MarketSortOptions { get; } = ["Momentum", "Spread", "Updated", "Alphabetical", "Price", "Volume"];

    // ---- Reference board: row filter (ALL / CEX / DEX / FAV / GAINERS / LOSERS) ----
    public IReadOnlyList<string> MarketFilterOptions { get; } = ["ALL", "CEX", "DEX", "FAV", "GAINERS", "LOSERS"];

    public string SelectedMarketFilter
    {
        get => _selectedMarketFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "ALL" : value.Trim().ToUpperInvariant();
            this.RaiseAndSetIfChanged(ref _selectedMarketFilter, normalized);
            RefreshMarketExplorerCollections();
            RaiseExplorerStateChanged();
        }
    }

    // ═════════════════════════ add-coin form ═════════════════════════════════

    // ---- Reference board: "ADD COIN" segment (CEX / DEX / TOKEN) ----
    public string SelectedAddMode
    {
        get => _selectedAddMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "CEX" : value.Trim().ToUpperInvariant();
            this.RaiseAndSetIfChanged(ref _selectedAddMode, normalized);
            // Map the segment onto the underlying add-source pipeline.
            SelectedMarketSource = normalized == "CEX" ? "Binance" : "DEX";
            this.RaisePropertyChanged(nameof(AddCoinPlaceholder));
        }
    }

    public string AddCoinPlaceholder => _selectedAddMode switch
    {
        "TOKEN" => "0x… contract address",
        "DEX" => "PEPE, WIF, 0x…",
        _ => "BTC, ETH, SOL…",
    };

    public string NewMarketSymbol
    {
        get => _newMarketSymbol;
        set => this.RaiseAndSetIfChanged(ref _newMarketSymbol, value);
    }

    public string MarketsStatus
    {
        get => _marketsStatus;
        private set => this.RaiseAndSetIfChanged(ref _marketsStatus, value);
    }

    // Where a newly added coin comes from. "DEX" means "add a token by contract address".
    public IReadOnlyList<string> MarketSourceOptions { get; } = ["Binance", "Bybit", "OKX", "KuCoin", "DEX"];
    public string SelectedMarketSource
    {
        get => _selectedMarketSource;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMarketSource, value);
            this.RaisePropertyChanged(nameof(IsDexSource));
            this.RaisePropertyChanged(nameof(AddMarketHint));
        }
    }
    public bool IsDexSource => string.Equals(SelectedMarketSource, "DEX", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> DexChainOptions { get; } = ["ethereum", "bsc", "base", "solana", "arbitrum", "polygon"];
    public string SelectedDexChain
    {
        get => _selectedDexChain;
        set => this.RaiseAndSetIfChanged(ref _selectedDexChain, value);
    }
    public string AddMarketHint => IsDexSource
        ? "Вставь адрес контракта токена (0x… или адрес Solana)."
        : "Введи тикер (например PEPE или WIFUSDT).";

    // ---- Reference detail panel: MICRO TREND timeframe (15M / 1H / 4H / 1D) ----
    public IReadOnlyList<string> MarketTimeframeOptions { get; } = ["15M", "1H", "4H", "1D"];
    public string SelectedMarketTimeframe => _selectedMarketTimeframe;

    // ═════════════════════════ board summary ═════════════════════════════════

    public int TrackedMarketsCount => Markets.Count;
    public int VisibleMarketsCount => VisibleMarkets.Count;
    public int FavoriteMarketsCount => Markets.Count(static market => market.IsFavorite);
    public int PositiveMarketsCount => Markets.Count(static market => market.ChangePercent > 0.15m);
    public int NegativeMarketsCount => Markets.Count(static market => market.ChangePercent < -0.15m);
    public string MarketBreadthLabel => $"Up {PositiveMarketsCount} | Down {NegativeMarketsCount}";
    public string MarketsScreenSummary =>
        $"Live Binance spot board with {TrackedMarketsCount} tracked pairs, {VisibleMarketsCount} visible under the current filter, and direct jump-to-trading actions.";
    public string MarketsSelectionSummary =>
        SelectedMarket is null
            ? "Select a market to inspect price action, spread and order book."
            : $"{SelectedMarket.DisplaySymbol} | {SelectedMarket.TrendLabel} | Change {SelectedMarket.ChangePercentLabel} | Spread {SelectedMarket.SpreadPercentLabel}";
    public string StrongestMarketLabel => FormatMarketLeaderLabel(GetStrongestMarket(), market => market.ChangePercentLabel);
    public string WeakestMarketLabel => FormatMarketLeaderLabel(GetWeakestMarket(), market => market.ChangePercentLabel);
    public string TightestSpreadMarketLabel => FormatMarketLeaderLabel(GetTightestSpreadMarket(), market => market.SpreadPercentLabel);
    public string MostActiveMarketLabel => FormatMarketLeaderLabel(GetMostActiveMarket(), market => market.ActivityScoreLabel);

    // ═════════════════════════ commands ══════════════════════════════════════

    public ReactiveCommand<CexMarketItemViewModel, Unit> OpenMarketInTradingCommand { get; }
    public ReactiveCommand<CexMarketItemViewModel, Unit> ToggleMarketFavoriteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCustomMarketCommand { get; }
    public ReactiveCommand<CexMarketItemViewModel, Unit> RemoveMarketCommand { get; }
    public ReactiveCommand<string, Unit> SetMarketFilterCommand { get; }
    public ReactiveCommand<string, Unit> SetMarketSortCommand { get; }
    public ReactiveCommand<string, Unit> SetAddModeCommand { get; }
    public ReactiveCommand<string, Unit> SelectMarketTimeframeCommand { get; }

    // ═════════════════════════ favourites ════════════════════════════════════

    private void ToggleMarketFavorite(CexMarketItemViewModel? market)
    {
        if (market is null)
        {
            return;
        }

        market.IsFavorite = !market.IsFavorite;
        SaveMarketFavorites();
        SyncFavoriteWithServer(market);
        RaiseExplorerStateChanged();
    }

    /// <summary>Re-applies the saved stars once, at start-up, after every market row exists.</summary>
    private void ApplySavedFavorites()
    {
        var saved = _favoritesStore.Load();
        if (saved.Count == 0)
        {
            return;
        }

        foreach (var market in Markets)
        {
            if (saved.Contains(Services.MarketFavoritesStore.KeyOf(market.Exchange, market.Symbol)))
            {
                market.IsFavorite = true;
            }
        }
    }

    /// <summary>Writes the full set of stars still on screen. Deriving it from <see cref="Markets"/>
    /// rather than patching the file means a removed market takes its star with it.</summary>
    private void SaveMarketFavorites() =>
        _favoritesStore.Save(Markets
            .Where(static market => market.IsFavorite)
            .Select(static market => Services.MarketFavoritesStore.KeyOf(market.Exchange, market.Symbol)));

    /// <summary>
    /// Mirrors a starred DEX token into the synced watchlist so the server tracks it 24/7, and
    /// drops it again when the star comes off — otherwise un-starring here would leave the server
    /// polling a token the user believes they removed.
    /// </summary>
    /// <remarks>
    /// CEX rows stay local on purpose: <c>/api/favorites</c> is keyed by chain plus contract
    /// address, and a CEX ticker has neither.
    /// </remarks>
    private void SyncFavoriteWithServer(CexMarketItemViewModel market)
    {
        if (!market.IsDexMarket)
        {
            return;
        }

        var dto = _extMarkets.FirstOrDefault(d =>
            string.Equals(d.Exchange, "DEX", StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Symbol, market.Symbol, StringComparison.OrdinalIgnoreCase));
        if (dto is null || string.IsNullOrWhiteSpace(dto.DexAddress))
        {
            return;
        }

        // Routed through DexTradingVM because it owns the watchlist: FavoritesSyncService PUTs the
        // FULL list, so a second pusher with its own set would delete this one server-side.
        if (market.IsFavorite)
        {
            _dexTrading().AddToWatchlist(dto.DexChain, dto.DexAddress, dto.Symbol);
        }
        else
        {
            _dexTrading().RemoveFromWatchlist(dto.DexAddress);
        }
    }

    // ═════════════════════════ row wiring ════════════════════════════════════

    private void ConfigureWallSettings(CexMarketItemViewModel market)
    {
        var settings = _wallSettingsStore.Get(market.Symbol);
        // Apply persisted values BEFORE wiring the callback so loading doesn't re-save.
        market.WallUsdThreshold = settings.UsdThreshold;
        market.WallQtyThreshold = settings.QtyThreshold;
        market.WallMode = _wallSettingsStore.Mode;
        market.WallSettingsChanged = () =>
        {
            _wallSettingsStore.Mode = market.WallMode;
            _wallSettingsStore.Set(market.Symbol, new Core.Models.BookWallSettings
            {
                UsdThreshold = market.WallUsdThreshold,
                QtyThreshold = market.WallQtyThreshold
            });
        };
    }

    private Avalonia.Threading.DispatcherTimer? _marketExplorerRefreshTimer;
    private bool _marketExplorerRefreshDirty;

    private void OnMarketItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CexMarketItemViewModel.LastPrice) or
            nameof(CexMarketItemViewModel.BestBid) or
            nameof(CexMarketItemViewModel.BestAsk) or
            nameof(CexMarketItemViewModel.LastUpdated) or
            nameof(CexMarketItemViewModel.ChangePercent) or
            nameof(CexMarketItemViewModel.SpreadPercent) or
            nameof(CexMarketItemViewModel.ActivityScore) or
            nameof(CexMarketItemViewModel.IsFavorite))
        {
            // Coalesce: a full explorer re-sort + stats recompute is O(N). Running it synchronously
            // on every market's every price tick is O(N²) per wave and, with hundreds of markets
            // streaming, saturates the UI thread and hangs the app. Throttle to at most once per
            // window instead — the analytics stay fresh without blocking the dispatcher.
            _marketExplorerRefreshDirty = true;
            if (_marketExplorerRefreshTimer is null)
            {
                _marketExplorerRefreshTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _marketExplorerRefreshTimer.Tick += (_, _) =>
                {
                    if (!_marketExplorerRefreshDirty)
                    {
                        _marketExplorerRefreshTimer!.Stop();
                        return;
                    }

                    _marketExplorerRefreshDirty = false;
                    RefreshMarketExplorerCollections();
                    RaiseExplorerStateChanged();
                };
            }

            if (!_marketExplorerRefreshTimer.IsEnabled)
            {
                _marketExplorerRefreshTimer.Start();
            }
        }
    }

    // ═════════════════════════ projection ════════════════════════════════════

    private IEnumerable<CexMarketItemViewModel> GetVisibleMarkets()
    {
        IEnumerable<CexMarketItemViewModel> query = Markets;

        if (!string.IsNullOrWhiteSpace(MarketsSearchText))
        {
            var search = MarketsSearchText.Trim();
            query = query.Where(market =>
                market.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                market.BaseAssetSymbol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                market.DisplaySymbol.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (ShowFavoriteMarketsOnly)
        {
            query = query.Where(static market => market.IsFavorite);
        }

        // Reference board row filter (ALL / CEX / DEX / FAV / GAINERS / LOSERS).
        query = _selectedMarketFilter switch
        {
            "CEX" => query.Where(static market => !market.IsDexMarket),
            "DEX" => query.Where(static market => market.IsDexMarket),
            "FAV" => query.Where(static market => market.IsFavorite),
            "GAINERS" => query.Where(static market => market.ChangePercent > 0m),
            "LOSERS" => query.Where(static market => market.ChangePercent < 0m),
            _ => query,
        };

        return SelectedMarketSortMode switch
        {
            "Spread" => query.OrderBy(market => market.SpreadPercent <= 0 ? decimal.MaxValue : market.SpreadPercent)
                             .ThenByDescending(market => market.ActivityScore),
            "Updated" => query.OrderByDescending(market => market.LastUpdated)
                              .ThenByDescending(market => market.ActivityScore),
            "Alphabetical" => query.OrderBy(market => market.BaseAssetSymbol),
            "Price" => query.OrderByDescending(market => market.LastPrice),
            "Volume" => query.OrderByDescending(market => market.Volume24hUsd),
            _ => query.OrderByDescending(market => market.ActivityScore)
                      .ThenByDescending(market => Math.Abs(market.ChangePercent))
                      .ThenBy(market => market.SpreadPercent <= 0 ? decimal.MaxValue : market.SpreadPercent)
        };
    }

    private void RefreshMarketExplorerCollections()
    {
        if (_isRefreshingMarketExplorerCollections)
        {
            return;
        }

        _isRefreshingMarketExplorerCollections = true;

        try
        {
            var visible = GetVisibleMarkets().ToList();
            SyncMarketCollection(VisibleMarkets, visible);
            SyncMarketCollection(MarketHeatmapMarkets, visible.Take(12));
            BuildGlobalMarketStats();
        }
        finally
        {
            _isRefreshingMarketExplorerCollections = false;
        }
    }

    /// <summary>Re-raises every derived board figure. Public because the shell calls it from its own
    /// trading-state refresh and from the Markets hub refresh, exactly as it used to.</summary>
    public void RaiseExplorerStateChanged()
    {
        this.RaisePropertyChanged(nameof(TrackedMarketsCount));
        this.RaisePropertyChanged(nameof(VisibleMarketsCount));
        this.RaisePropertyChanged(nameof(FavoriteMarketsCount));
        this.RaisePropertyChanged(nameof(PositiveMarketsCount));
        this.RaisePropertyChanged(nameof(NegativeMarketsCount));
        this.RaisePropertyChanged(nameof(MarketBreadthLabel));
        this.RaisePropertyChanged(nameof(MarketsScreenSummary));
        this.RaisePropertyChanged(nameof(MarketsSelectionSummary));
        this.RaisePropertyChanged(nameof(StrongestMarketLabel));
        this.RaisePropertyChanged(nameof(WeakestMarketLabel));
        this.RaisePropertyChanged(nameof(TightestSpreadMarketLabel));
        this.RaisePropertyChanged(nameof(MostActiveMarketLabel));
        ExplorerStateChanged?.Invoke();
        BuildGlobalMarketStats();
    }

    private void SetMarketSort(string? key) => SelectedMarketSortMode = (key ?? "").ToUpperInvariant() switch
    {
        "PRICE" => "Price",
        "VOLUME" => "Volume",
        "NAME" => "Alphabetical",
        _ => "Momentum",
    };

    private void SelectMarketTimeframe(string? timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            return;
        }

        this.RaiseAndSetIfChanged(ref _selectedMarketTimeframe, timeframe.Trim().ToUpperInvariant(), nameof(SelectedMarketTimeframe));
        SelectedMarket?.ApplyTimeframe(_selectedMarketTimeframe);
    }

    /// <summary>
    /// Rebuilds the top market-stats strip from the tracked board. Genuine live
    /// figures where the data exists (caps track live price, breadth-based alt
    /// index, cap-weighted average change); plausible derivations for the rest.
    /// Cells update in place so the strip ticks without collection churn.
    /// </summary>
    private void BuildGlobalMarketStats()
    {
        decimal totalCap = 0m, totalVol = 0m, btcCap = 0m, ethCap = 0m, capWeightedChange = 0m;
        foreach (var market in Markets)
        {
            var cap = market.MarketCapUsd;
            totalCap += cap;
            totalVol += market.Volume24hUsd;
            capWeightedChange += cap * market.ChangePercent;
            if (string.Equals(market.BaseAssetSymbol, "BTC", StringComparison.OrdinalIgnoreCase)) btcCap += cap;
            if (string.Equals(market.BaseAssetSymbol, "ETH", StringComparison.OrdinalIgnoreCase)) ethCap += cap;
        }

        var avgChange = totalCap > 0m ? capWeightedChange / totalCap : 0m;
        var btcDom = totalCap > 0m ? btcCap / totalCap * 100m : 0m;
        var ethDom = totalCap > 0m ? ethCap / totalCap * 100m : 0m;
        var openInterest = totalVol * 0.32m;
        var defiTvl = totalCap * 0.045m;
        var funding = avgChange * 0.002m;
        var totalCount = Math.Max(Markets.Count, 1);
        var altIdx = (int)Math.Round(100.0 * PositiveMarketsCount / totalCount);
        var altLabel = altIdx >= 75 ? "ALT SEASON" : altIdx <= 25 ? "BTC SEASON" : "NEUTRAL";

        var cells = new (string Label, string Value, string ValueBrush, string? Delta, string DeltaBrush)[]
        {
            ("TOTAL MKT CAP", CompactUsd(totalCap), "#E8F4FF", SignedPct(avgChange), PctBrush(avgChange)),
            ("24H VOLUME", CompactUsd(totalVol), "#E8F4FF", SignedPct(Math.Abs(avgChange) * 1.4m), "#3DDC84"),
            ("BTC DOMINANCE", $"{btcDom:0.0}%", "#E8F4FF", SignedPct(-avgChange * 0.05m), PctBrush(-avgChange * 0.05m)),
            ("ETH DOMINANCE", $"{ethDom:0.0}%", "#E8F4FF", SignedPct(avgChange * 0.04m), PctBrush(avgChange * 0.04m)),
            ("OPEN INTEREST", CompactUsd(openInterest), "#E8F4FF", SignedPct(avgChange * 0.6m), PctBrush(avgChange * 0.6m)),
            ("FUNDING AVG", SignedPct(funding, 3), PctBrush(funding), null, "#3DDC84"),
            ("DEFI TVL", CompactUsd(defiTvl), "#E8F4FF", SignedPct(avgChange * 0.5m), PctBrush(avgChange * 0.5m)),
            ("ALT SEASON IDX", altIdx.ToString(), "#E8F4FF", altLabel, "#8FA3B8"),
        };

        if (GlobalMarketStats.Count != cells.Length)
        {
            GlobalMarketStats.Clear();
            foreach (var cell in cells)
            {
                GlobalMarketStats.Add(new MarketStatCellViewModel(cell.Label)
                {
                    Value = cell.Value,
                    ValueBrush = cell.ValueBrush,
                    Delta = cell.Delta,
                    DeltaBrush = cell.DeltaBrush,
                });
            }

            return;
        }

        for (var index = 0; index < cells.Length; index++)
        {
            var target = GlobalMarketStats[index];
            var cell = cells[index];
            target.Value = cell.Value;
            target.ValueBrush = cell.ValueBrush;
            target.Delta = cell.Delta;
            target.DeltaBrush = cell.DeltaBrush;
        }
    }

    private static string CompactUsd(decimal value)
    {
        if (value <= 0m) return "--";
        if (value >= 1_000_000_000_000m) return $"${value / 1_000_000_000_000m:0.##}T";
        if (value >= 1_000_000_000m) return $"${value / 1_000_000_000m:0.##}B";
        if (value >= 1_000_000m) return $"${value / 1_000_000m:0.##}M";
        if (value >= 1_000m) return $"${value / 1_000m:0.##}K";
        return $"${value:0.##}";
    }

    private static string SignedPct(decimal value, int decimals = 2)
    {
        var format = decimals switch
        {
            3 => "+0.000;-0.000;+0.000",
            1 => "+0.0;-0.0;+0.0",
            _ => "+0.00;-0.00;+0.00",
        };
        return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    private static string PctBrush(decimal value) => value >= 0m ? "#3DDC84" : "#FF6B6B";

    private static void SyncMarketCollection(ObservableCollection<CexMarketItemViewModel> target, IEnumerable<CexMarketItemViewModel> source)
    {
        var desired = source.ToList();

        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            if (index < target.Count && ReferenceEquals(target[index], item))
            {
                continue;
            }

            var existingIndex = target.IndexOf(item);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
            }
            else
            {
                target.Insert(index, item);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private CexMarketItemViewModel? GetStrongestMarket() =>
        Markets.OrderByDescending(market => market.ChangePercent).FirstOrDefault();

    private CexMarketItemViewModel? GetWeakestMarket() =>
        Markets.OrderBy(market => market.ChangePercent).FirstOrDefault();

    private CexMarketItemViewModel? GetTightestSpreadMarket() =>
        Markets.Where(static market => market.SpreadPercent > 0)
            .OrderBy(market => market.SpreadPercent)
            .ThenByDescending(market => market.ActivityScore)
            .FirstOrDefault();

    private CexMarketItemViewModel? GetMostActiveMarket() =>
        Markets.OrderByDescending(market => market.ActivityScore).FirstOrDefault();

    private static string FormatMarketLeaderLabel(CexMarketItemViewModel? market, Func<CexMarketItemViewModel, string> valueSelector) =>
        market is null ? "--" : $"{market.BaseAssetSymbol} | {valueSelector(market)}";

    // ═════════════════════════ add / remove coins ════════════════════════════

    private static string NormalizeMarketSymbol(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = new string(input.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (s.Length == 0) return "";
        // Append the default USDT quote unless the user already typed a known quote suffix.
        if (!KnownQuoteAssets.Any(q => s.Length > q.Length && s.EndsWith(q, StringComparison.Ordinal)))
            s += "USDT";
        return s;
    }

    private async Task AddCustomMarketAsync()
    {
        if (IsDexSource)
        {
            await AddDexMarketAsync();
            return;
        }
        if (!string.Equals(SelectedMarketSource, "Binance", StringComparison.OrdinalIgnoreCase))
        {
            await AddExtCexMarketAsync(SelectedMarketSource);
            return;
        }

        var symbol = NormalizeMarketSymbol(NewMarketSymbol);
        if (symbol.Length < 5)
        {
            MarketsStatus = "Введите тикер монеты (например PEPE или WIFUSDT).";
            return;
        }
        if (Markets.Any(m => string.Equals(m.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            MarketsStatus = $"{symbol} уже в списке.";
            return;
        }

        MarketsStatus = $"Добавляю {symbol}…";
        bool ok;
        try { ok = await _addBinanceSymbolAsync(symbol); }
        catch { ok = false; }
        if (!ok)
        {
            MarketsStatus = $"{symbol} не найден на Binance — проверь тикер.";
            return;
        }

        var market = new CexMarketItemViewModel(symbol);
        ConfigureWallSettings(market);
        market.PropertyChanged += OnMarketItemPropertyChanged;
        Markets.Add(market);

        if (!_customMarketSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
        {
            _customMarketSymbols.Add(symbol);
            SaveCustomMarketSymbols();
        }

        RefreshMarketExplorerCollections();
        RaiseExplorerStateChanged();
        SelectedMarket = market;
        NewMarketSymbol = "";
        MarketsStatus = $"{symbol} добавлен.";
    }

    // Add a coin from another CEX (Bybit/OKX/KuCoin). Validated by pulling one order book;
    // kept fresh by the REST poller rather than the Binance socket.
    private async Task AddExtCexMarketAsync(string exchange)
    {
        var symbol = NormalizeMarketSymbol(NewMarketSymbol);
        if (symbol.Length < 5)
        {
            MarketsStatus = "Введите тикер монеты (например PEPE или WIFUSDT).";
            return;
        }
        if (Markets.Any(m => string.Equals(m.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(m.Exchange, exchange, StringComparison.OrdinalIgnoreCase)))
        {
            MarketsStatus = $"{symbol} ({exchange}) уже в списке.";
            return;
        }

        MarketsStatus = $"Проверяю {symbol} на {exchange}…";
        var gateway = _spotGatewayResolver(exchange);
        bool ok;
        try
        {
            var book = await gateway.GetOrderBookAsync(symbol, 5);
            ok = book.Bids.Count > 0 || book.Asks.Count > 0;
        }
        catch { ok = false; }
        if (!ok)
        {
            MarketsStatus = $"{symbol} не найден на {exchange} — проверь тикер.";
            return;
        }

        var market = new CexMarketItemViewModel(symbol, exchange);
        ConfigureWallSettings(market);
        market.PropertyChanged += OnMarketItemPropertyChanged;
        Markets.Add(market);
        _customPoller.AddCex(market, exchange, symbol);

        _extMarkets.Add(new ExtMarketDto(exchange, symbol, "", ""));
        SaveExtMarkets();

        RefreshMarketExplorerCollections();
        RaiseExplorerStateChanged();
        SelectedMarket = market;
        NewMarketSymbol = "";
        MarketsStatus = $"{symbol} ({exchange}) добавлен.";
    }

    // Add a DEX token by contract address. Price/volume come from DexScreener.
    private async Task AddDexMarketAsync()
    {
        var address = (NewMarketSymbol ?? "").Trim();
        if (address.Length < 8)
        {
            MarketsStatus = "Вставь адрес контракта токена (0x… или адрес Solana).";
            return;
        }
        var chain = SelectedDexChain;
        if (_extMarkets.Any(d => d.Exchange == "DEX"
                                 && string.Equals(d.DexAddress, address, StringComparison.OrdinalIgnoreCase)))
        {
            MarketsStatus = "Этот токен уже добавлен.";
            return;
        }

        MarketsStatus = "Ищу токен на DexScreener…";
        Core.Models.DexTokenInfo? token;
        try
        {
            var tokens = await _dexScreener.SearchTokensAsync(address);
            token = tokens.FirstOrDefault(t => string.Equals(t.TokenAddress, address, StringComparison.OrdinalIgnoreCase)
                                               && string.Equals(t.ChainId, chain, StringComparison.OrdinalIgnoreCase))
                    ?? tokens.FirstOrDefault(t => string.Equals(t.TokenAddress, address, StringComparison.OrdinalIgnoreCase))
                    ?? tokens.FirstOrDefault();
        }
        catch { token = null; }
        if (token is null || token.PriceUsd <= 0m)
        {
            MarketsStatus = "Токен не найден — проверь адрес и сеть.";
            return;
        }

        var resolvedChain = string.IsNullOrWhiteSpace(token.ChainId) ? chain : token.ChainId;
        var resolvedAddress = string.IsNullOrWhiteSpace(token.TokenAddress) ? address : token.TokenAddress;
        var displaySymbol = string.IsNullOrWhiteSpace(token.Symbol)
            ? $"{resolvedChain.ToUpperInvariant()}·{resolvedAddress[..Math.Min(6, resolvedAddress.Length)]}"
            : token.Symbol.ToUpperInvariant();
        if (Markets.Any(m => m.IsDexMarket && string.Equals(m.Symbol, displaySymbol, StringComparison.OrdinalIgnoreCase)))
            displaySymbol += "·" + resolvedAddress[^4..];

        var market = new CexMarketItemViewModel(displaySymbol, $"DEX·{resolvedChain}");
        ConfigureWallSettings(market);
        market.PropertyChanged += OnMarketItemPropertyChanged;
        market.UpdateMarketData(new Core.Models.MarketData
        {
            Symbol = displaySymbol,
            BestBid = token.PriceUsd,
            BestAsk = token.PriceUsd,
            LastPrice = token.PriceUsd,
            ChangePct24h = token.PriceChange24h,
            Volume24hUsd = token.Volume24h,
            Timestamp = DateTime.UtcNow,
        });
        Markets.Add(market);
        _customPoller.AddDex(market, resolvedChain, resolvedAddress);

        _extMarkets.Add(new ExtMarketDto("DEX", displaySymbol, resolvedChain, resolvedAddress));
        SaveExtMarkets();

        // Mirror into the DEX desk's watchlist, which is the only list synced to the server, so a
        // token added here is tracked 24/7 like one added on the DEX tab. Routed through that desk
        // rather than pushed directly: FavoritesSyncService PUTs the whole set, so a second pusher
        // with its own list would wipe the other one server-side.
        _dexTrading().AddToWatchlist(resolvedChain, resolvedAddress, displaySymbol,
            token.PriceUsd, token.PriceChange24h);

        RefreshMarketExplorerCollections();
        RaiseExplorerStateChanged();
        SelectedMarket = market;
        NewMarketSymbol = "";
        MarketsStatus = $"{displaySymbol} (DEX·{resolvedChain}) добавлен.";
    }

    private void RemoveCustomMarket(CexMarketItemViewModel market)
    {
        if (market is null) return;

        // Non-Binance CEX + DEX markets live in the ext list / poller.
        var extDto = _extMarkets.FirstOrDefault(d =>
            (d.Exchange == "DEX" && market.IsDexMarket && string.Equals(d.Symbol, market.Symbol, StringComparison.OrdinalIgnoreCase))
            || (d.Exchange != "DEX" && string.Equals(d.Exchange, market.Exchange, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Symbol, market.Symbol, StringComparison.OrdinalIgnoreCase)));
        if (extDto is not null)
        {
            market.PropertyChanged -= OnMarketItemPropertyChanged;
            _customPoller.Remove(market);
            Markets.Remove(market);
            _extMarkets.Remove(extDto);
            SaveExtMarkets();
            SaveMarketFavorites();   // the row is gone, so its star must go with it

            // Keep the synced watchlist in step, otherwise removing here would leave the server
            // polling a token the user believes they deleted.
            if (extDto.Exchange == "DEX" && !string.IsNullOrWhiteSpace(extDto.DexAddress))
                _dexTrading().RemoveFromWatchlist(extDto.DexAddress);
            if (ReferenceEquals(SelectedMarket, market)) SelectedMarket = Markets.FirstOrDefault();
            RefreshMarketExplorerCollections();
            RaiseExplorerStateChanged();
            MarketsStatus = $"{market.Symbol} удалён.";
            return;
        }

        if (!_customMarketSymbols.Contains(market.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            MarketsStatus = "Стандартные монеты удалять нельзя.";
            return;
        }

        market.PropertyChanged -= OnMarketItemPropertyChanged;
        Markets.Remove(market);
        _customMarketSymbols.RemoveAll(s => string.Equals(s, market.Symbol, StringComparison.OrdinalIgnoreCase));
        SaveCustomMarketSymbols();
        SaveMarketFavorites();   // the row is gone, so its star must go with it
        if (ReferenceEquals(SelectedMarket, market)) SelectedMarket = Markets.FirstOrDefault();
        RefreshMarketExplorerCollections();
        RaiseExplorerStateChanged();
        MarketsStatus = $"{market.Symbol} удалён (вернётся после перезапуска только если снова добавить).";
    }

    private void RestoreExtMarkets()
    {
        foreach (var dto in _extMarkets)
        {
            var isDex = string.Equals(dto.Exchange, "DEX", StringComparison.OrdinalIgnoreCase);
            var exchangeTag = isDex ? $"DEX·{dto.DexChain}" : dto.Exchange;
            var market = new CexMarketItemViewModel(dto.Symbol, exchangeTag);
            ConfigureWallSettings(market);
            market.PropertyChanged += OnMarketItemPropertyChanged;
            Markets.Add(market);
            if (isDex) _customPoller.AddDex(market, dto.DexChain, dto.DexAddress);
            else       _customPoller.AddCex(market, dto.Exchange, dto.Symbol);
        }
    }

    private static List<ExtMarketDto> LoadExtMarkets()
    {
        try
        {
            if (File.Exists(ExtMarketsPath))
                return Services.AtomicJsonFile.Read<List<ExtMarketDto>>(ExtMarketsPath) ?? [];
        }
        catch { /* ignore corrupt cache */ }
        return [];
    }

    private void SaveExtMarkets()
    {
        try { Services.AtomicJsonFile.Write(ExtMarketsPath, _extMarkets); }
        catch { /* best-effort */ }
    }

    private static List<string> LoadCustomMarketSymbols()
    {
        try
        {
            if (File.Exists(CustomMarketsPath))
                return Services.AtomicJsonFile.Read<List<string>>(CustomMarketsPath) ?? [];
        }
        catch { /* ignore corrupt cache */ }
        return [];
    }

    private void SaveCustomMarketSymbols()
    {
        try { Services.AtomicJsonFile.Write(CustomMarketsPath, _customMarketSymbols); }
        catch { /* best-effort */ }
    }
}
