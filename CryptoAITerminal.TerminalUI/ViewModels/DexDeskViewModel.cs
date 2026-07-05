using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.Core.Trading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>DEX terminal sub-mode: real spot AMM swap vs paper perpetuals.</summary>
public enum DexDeskMode
{
    Swap,
    Perp
}

/// <summary>A selectable DEX exchange / protocol (Hyperliquid, dYdX, GMX, …) with the
/// venue metadata shown in the reference mock.</summary>
public sealed class DexExchangeViewModel : ReactiveObject
{
    private bool _isSelected;
    private bool _isLive;
    private string _vol;
    private string _fundingRate;
    private string _maxLev;

    public string Key { get; }
    public string Name { get; }
    public string DotBrush { get; }
    public string NameBrush { get; }
    public string Fee { get; }
    public string Type { get; }         // PERP / SWAP
    public string ChainBadge { get; }
    public string Settlement { get; }
    public string NetGas { get; }

    // Live-updatable (start from the reference values, replaced by real API data).
    public string Vol { get => _vol; set => this.RaiseAndSetIfChanged(ref _vol, value); }
    public string FundingRate { get => _fundingRate; set => this.RaiseAndSetIfChanged(ref _fundingRate, value); }
    public string MaxLev { get => _maxLev; set => this.RaiseAndSetIfChanged(ref _maxLev, value); }
    public bool IsLive { get => _isLive; set => this.RaiseAndSetIfChanged(ref _isLive, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public DexExchangeViewModel(string key, string name, string dot, string nameBrush, string vol, string fee,
        string type, string chainBadge, string settlement, string netGas, string fundingRate, string maxLev)
    {
        Key = key;
        Name = name;
        DotBrush = dot;
        NameBrush = nameBrush;
        _vol = vol;
        Fee = fee;
        Type = type;
        ChainBadge = chainBadge;
        Settlement = settlement;
        NetGas = netGas;
        _fundingRate = fundingRate;
        _maxLev = maxLev;
    }
}

/// <summary>One live market row for the selected DEX exchange.</summary>
public sealed class DexExchangeMarketViewModel : ReactiveObject
{
    public DexExchangeMarketViewModel(DexExchangeMarket m)
    {
        Symbol = m.Symbol;
        PriceLabel = m.Price >= 1000m ? $"{m.Price:N1}" : m.Price >= 1m ? $"{m.Price:N3}" : $"{m.Price:N5}";
        ChangeLabel = $"{m.Change24hPct:+0.00;-0.00;0.00}%";
        ChangeBrush = m.Change24hPct >= 0m ? "#3ddc84" : "#ff6b6b";
        VolLabel = DexExchangeDataService.FormatUsdCompact(m.Volume24hUsd);
        FundingLabel = $"{m.Funding8hPct:+0.###;-0.###;0}%";
        FundingBrush = m.Funding8hPct >= 0m ? "#f4b860" : "#3ddc84";
    }

    public string Symbol { get; }
    public string PriceLabel { get; }
    public string ChangeLabel { get; }
    public string ChangeBrush { get; }
    public string VolLabel { get; }
    public string FundingLabel { get; }
    public string FundingBrush { get; }
}

/// <summary>A single gas-priority tier shown in the DEX ticket (real preference,
/// consumed by the paper engine to model network cost).</summary>
public sealed class DexGasTierViewModel : ReactiveObject
{
    private bool _isSelected;
    private string _gweiLabel;

    public string Key { get; }
    public string Label { get; }

    public string GweiLabel
    {
        get => _gweiLabel;
        set => this.RaiseAndSetIfChanged(ref _gweiLabel, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public DexGasTierViewModel(string key, string label, string gweiLabel)
    {
        Key = key;
        Label = label;
        _gweiLabel = gweiLabel;
    }
}

/// <summary>
/// Thin coordinator for the redesigned DEX terminal. Owns the DEX-specific chrome
/// that is shared across order types — venue sub-mode (SWAP/PERP), gas priority and
/// MEV-protection preferences — while the existing <see cref="DexTradingViewModel"/>
/// keeps driving the real spot-swap execution. Kept separate so the already large
/// swap view model stays focused. The paper-perp surface is added in a later phase.
/// </summary>
public sealed class DexDeskViewModel : ReactiveObject, IDisposable
{
    private readonly GasOracleService _gasOracle = new();
    private readonly DexExchangeDataService _exchangeData = new();
    private readonly DispatcherTimer _exchangeTimer;
    private DexDeskMode _mode = DexDeskMode.Swap;
    private string _selectedGasTierKey = "Standard";
    private string _selectedExchangeKey = "HYPERLIQUID";
    private bool _mevProtectionEnabled = true;

    public DexTradingViewModel Swap { get; }
    public DexPerpTradingViewModel Perp { get; }
    public AiTradeAssistantViewModel Assistant { get; }

    public DexDeskViewModel(DexTradingViewModel swap)
    {
        Swap = swap ?? throw new ArgumentNullException(nameof(swap));
        Perp = new DexPerpTradingViewModel(
            anchorPrice: () => Swap.SelectedToken?.PriceUsd ?? 0m,
            symbol: () => Swap.SelectedToken?.TokenInfo.Symbol ?? "TOKEN");
        Assistant = new AiTradeAssistantViewModel(
            venueLabel: "DEX",
            candles: () => Swap.ChartCandles,
            price: () => _mode == DexDeskMode.Perp ? Perp.MarkPrice : (Swap.SelectedToken?.PriceUsd ?? 0m),
            apply: ApplyDexSetup,
            equity: () => _mode == DexDeskMode.Perp ? Perp.Equity : 0m);

        GasTiers = new ObservableCollection<DexGasTierViewModel>(
            GasOracleService.BuildTiers(GasOracleService.DefaultBaseGwei(MapChain(Swap.SelectedChainFilter)))
                .Select(t => new DexGasTierViewModel(t.Key, t.Label, $"{t.Gwei:0.###} gwei")));

        Exchanges = new ObservableCollection<DexExchangeViewModel>
        {
            new("HYPERLIQUID", "Hyperliquid", "#1a9aff", "#4ab6ff", "$1.2B", "0.02%", "PERP", "HyperEVM L1", "On-chain perpetuals", "$0 (gasless)", "+0.008%/8h", "50×"),
            new("DYDX",        "dYdX v4",     "#6966ff", "#8a87ff", "$380M", "0.05%", "PERP", "Cosmos Chain", "Cosmos app-chain", "~$0.01", "+0.011%/8h", "25×"),
            new("GMX",         "GMX v2",      "#1bbf8d", "#21e6c1", "$210M", "0.01%", "PERP", "Arbitrum One", "GLP pool backed", "~$0.40", "+0.006%/8h", "100×"),
            new("UNISWAP",     "Uniswap V3",  "#ff007a", "#ff4da6", "$840M", "0.30%", "SWAP", "Ethereum", "AMM swap", "~$8.20", "N/A", "N/A"),
            new("VERTEX",      "Vertex",      "#f4b860", "#f4c87e", "$95M",  "0.02%", "PERP", "Arbitrum One", "CLOB on-chain", "~$0.20", "+0.009%/8h", "20×"),
        };

        SelectModeCommand = ReactiveCommand.Create<string>(SelectMode, outputScheduler: App.UiScheduler);
        SelectGasTierCommand = ReactiveCommand.Create<string>(SelectGasTier, outputScheduler: App.UiScheduler);
        SelectExchangeCommand = ReactiveCommand.Create<string>(SelectExchange, outputScheduler: App.UiScheduler);
        ToggleMevCommand = ReactiveCommand.Create(ToggleMev, outputScheduler: App.UiScheduler);

        SyncGasTierSelection();
        SyncExchangeSelection();

        Swap.PropertyChanged += OnSwapPropertyChanged;
        _ = RefreshGasAsync();

        _exchangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _exchangeTimer.Tick += (_, _) => _ = RefreshExchangeAsync();
        _exchangeTimer.Start();
        _ = RefreshExchangeAsync();
    }

    // ── Live exchange market data (real API) ──────────────────────────────────
    public ObservableCollection<DexExchangeMarketViewModel> ExchangeMarkets { get; } = new();
    public bool ShowExchangeMarkets => ExchangeMarkets.Count > 0;
    public bool SelectedExchangeIsLive => SelectedExchange?.IsLive ?? false;
    public string SelectedExchangeFeedLabel => SelectedExchangeIsLive ? "LIVE" : "demo feed";

    private async Task RefreshExchangeAsync()
    {
        var key = _selectedExchangeKey;
        var stats = await _exchangeData.FetchAsync(key);

        var ex = Exchanges.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        if (stats is null)
        {
            if (ex is not null)
            {
                ex.IsLive = false;
            }
            this.RaisePropertyChanged(nameof(SelectedExchangeIsLive));
            this.RaisePropertyChanged(nameof(SelectedExchangeFeedLabel));
            return;
        }

        if (ex is not null)
        {
            if (stats.Vol24hUsd > 0m)
            {
                ex.Vol = DexExchangeDataService.FormatUsdCompact(stats.Vol24hUsd);
            }
            if (!string.Equals(ex.Type, "SWAP", StringComparison.OrdinalIgnoreCase))
            {
                if (stats.RepFunding8hPct != 0m)
                {
                    ex.FundingRate = $"{stats.RepFunding8hPct:+0.###;-0.###;0}%/8h";
                }
                if (stats.MaxLeverage > 0)
                {
                    ex.MaxLev = $"{stats.MaxLeverage}×";
                }
            }
            ex.IsLive = true;
        }

        ExchangeMarkets.Clear();
        foreach (var m in stats.Markets)
        {
            ExchangeMarkets.Add(new DexExchangeMarketViewModel(m));
        }

        SyncExchangeSelection();
        this.RaisePropertyChanged(nameof(ShowExchangeMarkets));
        this.RaisePropertyChanged(nameof(SelectedExchangeIsLive));
        this.RaisePropertyChanged(nameof(SelectedExchangeFeedLabel));
    }

    private void OnSwapPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DexTradingViewModel.SelectedChainFilter))
        {
            _ = RefreshGasAsync();
        }
    }

    private async Task RefreshGasAsync()
    {
        var chain = MapChain(Swap.SelectedChainFilter);
        var live = await _gasOracle.FetchBaseGweiAsync(chain).ConfigureAwait(true);
        var baseGwei = live ?? GasOracleService.DefaultBaseGwei(chain);
        var tiers = GasOracleService.BuildTiers(baseGwei);

        foreach (var tier in tiers)
        {
            var vm = GasTiers.FirstOrDefault(g => string.Equals(g.Key, tier.Key, StringComparison.OrdinalIgnoreCase));
            if (vm is not null)
            {
                vm.GweiLabel = $"{tier.Gwei:0.###} gwei";
            }
        }

        this.RaisePropertyChanged(nameof(SelectedGasTierGweiLabel));
    }

    private static string MapChain(string? filter) => filter?.Trim().ToLowerInvariant() switch
    {
        "bsc" => "bsc",
        "ethereum" => "ethereum",
        "base" => "base",
        "arbitrum" => "arbitrum",
        "polygon" => "polygon",
        _ => "ethereum",
    };

    // ── Venue sub-mode ────────────────────────────────────────────────────────
    public DexDeskMode Mode
    {
        get => _mode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(ModeKey));
            this.RaisePropertyChanged(nameof(IsSwapMode));
            this.RaisePropertyChanged(nameof(IsPerpMode));
            this.RaisePropertyChanged(nameof(SymbolTypeLabel));
            Perp.SetActive(_mode == DexDeskMode.Perp);
        }
    }

    /// <summary>Upper-case key for driving <c>active</c> styles via StringEqualsConverter.</summary>
    public string ModeKey => _mode == DexDeskMode.Perp ? "PERP" : "SWAP";
    public bool IsSwapMode => _mode == DexDeskMode.Swap;
    public bool IsPerpMode => _mode == DexDeskMode.Perp;
    public string SymbolTypeLabel => _mode == DexDeskMode.Perp ? "PERP · DEX" : "SWAP · DEX";

    /// <summary>Shown in the PERP zone until the paper engine is wired (Phase 3).</summary>
    public string PerpPlaceholderTitle => "Paper PERP engine warming up";
    public string PerpPlaceholderDetail =>
        "The simulated perpetuals desk (mark price, funding, order book, positions, "
        + "liquidation) is being wired in. Switch to SWAP to trade spot now.";

    public ReactiveCommand<string, Unit> SelectModeCommand { get; }

    private void SelectMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        Mode = string.Equals(mode, "PERP", StringComparison.OrdinalIgnoreCase)
            ? DexDeskMode.Perp
            : DexDeskMode.Swap;
    }

    // ── DEX exchange selector (Hyperliquid / dYdX / GMX / Uniswap / Vertex) ────
    public ObservableCollection<DexExchangeViewModel> Exchanges { get; }
    public ReactiveCommand<string, Unit> SelectExchangeCommand { get; }

    public DexExchangeViewModel? SelectedExchange =>
        Exchanges.FirstOrDefault(e => string.Equals(e.Key, _selectedExchangeKey, StringComparison.OrdinalIgnoreCase));

    public string SelectedExchangeName => SelectedExchange?.Name ?? "--";
    public string SelectedExchangeChainBadge => SelectedExchange?.ChainBadge ?? "--";
    public string SelectedExchangeFee => SelectedExchange?.Fee ?? "--";
    public string SelectedExchangeVol => SelectedExchange?.Vol ?? "--";
    public string SelectedExchangeFunding => SelectedExchange?.FundingRate ?? "--";
    public string SelectedExchangeMaxLev => SelectedExchange?.MaxLev ?? "--";
    public string SelectedExchangeSettlement => SelectedExchange?.Settlement ?? "--";
    public string SelectedExchangeNetGas => SelectedExchange?.NetGas ?? "--";
    public string SelectedExchangeType => SelectedExchange?.Type ?? "--";

    private void SelectExchange(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _selectedExchangeKey = key;
        SyncExchangeSelection();

        // Switch SWAP/PERP to match the chosen exchange's product, like the reference.
        var ex = SelectedExchange;
        if (ex is not null)
        {
            Mode = string.Equals(ex.Type, "SWAP", StringComparison.OrdinalIgnoreCase) ? DexDeskMode.Swap : DexDeskMode.Perp;
        }

        ExchangeMarkets.Clear();
        this.RaisePropertyChanged(nameof(ShowExchangeMarkets));
        _ = RefreshExchangeAsync();
    }

    private void SyncExchangeSelection()
    {
        foreach (var ex in Exchanges)
        {
            ex.IsSelected = string.Equals(ex.Key, _selectedExchangeKey, StringComparison.OrdinalIgnoreCase);
        }

        this.RaisePropertyChanged(nameof(SelectedExchange));
        this.RaisePropertyChanged(nameof(SelectedExchangeName));
        this.RaisePropertyChanged(nameof(SelectedExchangeChainBadge));
        this.RaisePropertyChanged(nameof(SelectedExchangeFee));
        this.RaisePropertyChanged(nameof(SelectedExchangeVol));
        this.RaisePropertyChanged(nameof(SelectedExchangeFunding));
        this.RaisePropertyChanged(nameof(SelectedExchangeMaxLev));
        this.RaisePropertyChanged(nameof(SelectedExchangeSettlement));
        this.RaisePropertyChanged(nameof(SelectedExchangeNetGas));
        this.RaisePropertyChanged(nameof(SelectedExchangeType));
    }

    // ── Gas priority ──────────────────────────────────────────────────────────
    public ObservableCollection<DexGasTierViewModel> GasTiers { get; }

    public string SelectedGasTierKey
    {
        get => _selectedGasTierKey;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedGasTierKey, value);
            this.RaisePropertyChanged(nameof(SelectedGasTierGweiLabel));
        }
    }

    public string SelectedGasTierGweiLabel =>
        GasTiers.FirstOrDefault(t => string.Equals(t.Key, _selectedGasTierKey, StringComparison.OrdinalIgnoreCase))?.GweiLabel
        ?? "—";

    public ReactiveCommand<string, Unit> SelectGasTierCommand { get; }

    private void SelectGasTier(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            SelectedGasTierKey = key;
            SyncGasTierSelection();
        }
    }

    private void SyncGasTierSelection()
    {
        foreach (var tier in GasTiers)
        {
            tier.IsSelected = string.Equals(tier.Key, _selectedGasTierKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── MEV protection ────────────────────────────────────────────────────────
    public bool MevProtectionEnabled
    {
        get => _mevProtectionEnabled;
        private set
        {
            this.RaiseAndSetIfChanged(ref _mevProtectionEnabled, value);
            this.RaisePropertyChanged(nameof(MevLabel));
            this.RaisePropertyChanged(nameof(MevBrush));
        }
    }

    public string MevLabel => _mevProtectionEnabled ? "ON" : "OFF";
    public string MevBrush => _mevProtectionEnabled ? "#a855f7" : "#5a7a94";

    public ReactiveCommand<Unit, Unit> ToggleMevCommand { get; }

    private void ToggleMev() => MevProtectionEnabled = !_mevProtectionEnabled;

    /// <summary>Apply an AI-assistant setup to the active DEX ticket (PERP fully; SWAP
    /// maps the sizing suggestion onto the buy-amount preset).</summary>
    private void ApplyDexSetup(TradeSetup setup)
    {
        if (_mode == DexDeskMode.Perp)
        {
            Perp.Side = setup.Bias == "LONG" ? "Long" : "Short";
            Perp.OrderType = "Limit";
            Perp.TriggerPrice = setup.Entry;
            Perp.TakeProfit = setup.TakeProfit;
            Perp.StopLoss = setup.StopLoss;
            Perp.Leverage = setup.Leverage;

            var price = Perp.MarkPrice > 0m ? Perp.MarkPrice : setup.Entry;
            if (price > 0m)
            {
                var notional = Perp.Equity * (setup.SizePercent / 100m) * setup.Leverage;
                Perp.SizeTokens = Math.Round(notional / price, 4, MidpointRounding.AwayFromZero);
            }
        }
        else
        {
            var preset = setup.SizePercent >= 75m ? "100"
                : setup.SizePercent >= 50m ? "75"
                : setup.SizePercent >= 25m ? "50"
                : "25";
            Swap.ApplyBuyBalancePresetCommand.Execute(preset).Subscribe();
        }
    }

    public void Dispose()
    {
        _exchangeTimer.Stop();
        Swap.PropertyChanged -= OnSwapPropertyChanged;
        Perp.Dispose();
    }
}
