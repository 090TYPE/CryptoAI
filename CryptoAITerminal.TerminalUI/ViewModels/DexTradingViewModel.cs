using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System.Reactive;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class DexTradingViewModel : ReactiveObject, IDisposable
{
    private readonly DexScreenerClient _dexClient;
    private readonly GeckoTerminalClient _geckoTerminalClient;
    private readonly WalletWorkspaceViewModel _walletWorkspace;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chartDebounceTimer;
    private readonly Dictionary<string, List<DexPriceSample>> _priceHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly DexPriceHistoryCache _historyCache = new();
    private readonly HashSet<string> _onChainScanStarted = new(StringComparer.OrdinalIgnoreCase);
    private string _onChainScanStatus = string.Empty;

    private IReadOnlyList<DexTokenInfo> _loadedTokens = System.Array.Empty<DexTokenInfo>();
    private string _lastLoadSuccessMessage = "Latest DEX tokens refreshed.";
    private string _selectedChainFilter = "All";
    private string _selectedMinLiquidity = "Any";
    private string _selectedMinVolume = "Any";
    private string _selectedSortMode = "Volume";

    private readonly TokenSecurityAiService _tokenAiService = new();
    private readonly TokenSecurityService _securityScanner = new();
    private int _verdictSeq;

    private DexTokenItemViewModel? _selectedToken;
    private string _searchText = string.Empty;
    private string _statusMessage = "DEX feed is loading...";
    private bool _isLoading;
    private decimal _buyAmountBnb = 0.01m;
    private decimal _sellAmountTokens;
    private DateTime _lastUpdatedLocal = DateTime.MinValue;
    private string _selectedChartRange = "1W";
    private bool _isChartLoading;
    private string _chartStatusMessage = "Select a token to load price history.";
    private decimal _chartHigh;
    private decimal _chartLow;
    private decimal _chartChangePercent;
    private string _chartStartLabel = string.Empty;
    private string _chartEndLabel = string.Empty;
    private string _chartSource = "Local sampler";
    private string _chartDiagnostics = "Waiting for local price samples.";
    private string _chartTelemetry = "Waiting for local samples...";
    private string _selectedQuoteAssetSymbol = string.Empty;
    private decimal _selectedQuoteAssetBalance;
    private bool _isQuoteAssetBalanceLoading;
    private bool _pendingGlobalSizingApply = true;
    private bool _isSynchronizingQuoteAssetSelection;
    private decimal _slippagePercent = 3m;
    private string _buyQuoteLabel = string.Empty;
    private string _buyQuoteBrush = "#8FA3B8";
    private decimal _tokenBalance;
    private string _tokenBalanceLabel = string.Empty;
    private bool _isTokenBalanceLoading;
    private readonly DispatcherTimer _quoteDebounceTimer;

    private const double ChartWidth = 620;
    private const double ChartHeight = 240;

    public ObservableCollection<DexTokenItemViewModel> Tokens { get; } = new();
    public ObservableCollection<Point> ChartPoints { get; } = new();
    public ObservableCollection<DexOhlcvPoint> ChartCandles { get; } = new();
    public ObservableCollection<string> QuoteAssetOptions { get; } = new();

    public ObservableCollection<string> ChainFilterOptions { get; } = new() { "All", "BSC", "Ethereum", "Base", "Solana", "Tron" };
    public ObservableCollection<string> MinLiquidityOptions { get; } = new() { "Any", "$10k", "$50k", "$100k", "$500k" };
    public ObservableCollection<string> MinVolumeOptions { get; } = new() { "Any", "$10k", "$50k", "$250k", "$1M" };
    public ObservableCollection<string> SortModeOptions { get; } = new() { "Volume", "Liquidity", "24h Change" };

    public string SelectedChainFilter
    {
        get => _selectedChainFilter;
        // Changing the chain reloads the list for that chain (the latest-profiles
        // feed is Solana/ETH-heavy, so client-only filtering leaves BSC/Tron empty).
        set { this.RaiseAndSetIfChanged(ref _selectedChainFilter, value); _ = RefreshAsync(); }
    }

    public string SelectedMinLiquidity
    {
        get => _selectedMinLiquidity;
        set { this.RaiseAndSetIfChanged(ref _selectedMinLiquidity, value); ApplyTokenFilter(); }
    }

    public string SelectedMinVolume
    {
        get => _selectedMinVolume;
        set { this.RaiseAndSetIfChanged(ref _selectedMinVolume, value); ApplyTokenFilter(); }
    }

    public string SelectedSortMode
    {
        get => _selectedSortMode;
        set { this.RaiseAndSetIfChanged(ref _selectedSortMode, value); ApplyTokenFilter(); }
    }

    public DexTokenItemViewModel? SelectedToken
    {
        get => _selectedToken;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedToken, value);
            this.RaisePropertyChanged(nameof(HasSelectedToken));
            this.RaisePropertyChanged(nameof(CanTradeSelectedToken));
            this.RaisePropertyChanged(nameof(CanBuySelectedTokenLive));
            this.RaisePropertyChanged(nameof(CanSellSelectedTokenLive));
            this.RaisePropertyChanged(nameof(DexGuardStatusLabel));
            this.RaisePropertyChanged(nameof(DexGuardStatusBrush));
            this.RaisePropertyChanged(nameof(DexGuardSummary));
            this.RaisePropertyChanged(nameof(DexGuardDetail));
            this.RaisePropertyChanged(nameof(SelectedTokenTitle));
            this.RaisePropertyChanged(nameof(SelectedTokenSubtitle));
            this.RaisePropertyChanged(nameof(IsSelectedPairCompatible));
            this.RaisePropertyChanged(nameof(PairCompatibilityMessage));
            this.RaisePropertyChanged(nameof(ManualBuyBlockedReason));
            this.RaisePropertyChanged(nameof(ManualSellBlockedReason));
            QueueChartReload();
            QueueBuyQuote();
            _ = RefreshQuoteAssetBalanceAsync();
            _ = RefreshTokenBalanceAsync();
            _ = RefreshTokenVerdictAsync(value);
        }
    }

    public bool HasSelectedToken => SelectedToken is not null;
    public bool CanTradeSelectedToken =>
        SelectedToken is not null &&
        IsSelectedPairCompatible &&
        _walletWorkspace.CanTradeChainId(SelectedToken.TokenInfo.ChainId) &&
        _walletWorkspace.ActiveDexGateway?.SupportsDex(SelectedToken.TokenInfo.DexId) == true;
    public bool CanBuySelectedTokenLive => string.IsNullOrWhiteSpace(ManualBuyBlockedReason);
    public bool CanSellSelectedTokenLive => string.IsNullOrWhiteSpace(ManualSellBlockedReason);
    public string ManualBuyBlockedReason => GetManualBuyBlockedReason();
    public string ManualSellBlockedReason => GetManualSellBlockedReason();
    public string DexGuardStatusLabel => CanBuySelectedTokenLive && CanSellSelectedTokenLive
        ? "ROUTE READY"
        : _walletWorkspace.GlobalPaperOnlyMode
            ? "PAPER LOCK"
            : _walletWorkspace.IsReadOnly
                ? "WATCH MODE"
                : "ROUTE BLOCKED";
    public string DexGuardStatusBrush => DexGuardStatusLabel switch
    {
        "ROUTE READY" => "#21E6C1",
        "PAPER LOCK" => "#F4B860",
        "WATCH MODE" => "#5BC0EB",
        _ => "#FF8A65"
    };
    public string DexGuardSummary => CanBuySelectedTokenLive && CanSellSelectedTokenLive
        ? $"{SelectedTokenTitle} is ready for live routing with {EffectiveQuoteSymbolLabel}."
        : !string.IsNullOrWhiteSpace(ManualBuyBlockedReason)
            ? ManualBuyBlockedReason
            : ManualSellBlockedReason;
    public string DexGuardDetail => $"{_walletWorkspace.RouteReadinessSummary} | {QuoteAssetSummary}";

    public string SelectedTokenTitle => SelectedToken?.DisplayName ?? "Choose a token";

    public string SelectedTokenSubtitle => SelectedToken is null
        ? "Select any DEX token from the list to see the live price and trade controls."
        : $"{SelectedToken.TokenInfo.ChainId.ToUpperInvariant()} / {SelectedToken.TokenInfo.DexId} / {SelectedToken.TokenInfo.QuoteSymbol}";

    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public decimal BuyAmountBnb
    {
        get => _buyAmountBnb;
        set
        {
            this.RaiseAndSetIfChanged(ref _buyAmountBnb, value);
            this.RaisePropertyChanged(nameof(HasEnoughQuoteBalanceForBuy));
            this.RaisePropertyChanged(nameof(BuyAvailabilityMessage));
            this.RaisePropertyChanged(nameof(BuyAvailabilityBrush));
            this.RaisePropertyChanged(nameof(CanBuySelectedTokenLive));
            this.RaisePropertyChanged(nameof(ManualBuyBlockedReason));
            this.RaisePropertyChanged(nameof(DexGuardStatusLabel));
            this.RaisePropertyChanged(nameof(DexGuardSummary));
            QueueBuyQuote();
        }
    }

    public decimal SellAmountTokens
    {
        get => _sellAmountTokens;
        set
        {
            this.RaiseAndSetIfChanged(ref _sellAmountTokens, value);
            this.RaisePropertyChanged(nameof(CanSellSelectedTokenLive));
            this.RaisePropertyChanged(nameof(ManualSellBlockedReason));
            this.RaisePropertyChanged(nameof(DexGuardStatusLabel));
            this.RaisePropertyChanged(nameof(DexGuardSummary));
        }
    }

    public DateTime LastUpdatedLocal
    {
        get => _lastUpdatedLocal;
        set => this.RaiseAndSetIfChanged(ref _lastUpdatedLocal, value);
    }

    public string SelectedChartRange
    {
        get => _selectedChartRange;
        set => this.RaiseAndSetIfChanged(ref _selectedChartRange, value);
    }

    public bool IsChartLoading
    {
        get => _isChartLoading;
        set => this.RaiseAndSetIfChanged(ref _isChartLoading, value);
    }

    public string ChartStatusMessage
    {
        get => _chartStatusMessage;
        set => this.RaiseAndSetIfChanged(ref _chartStatusMessage, value);
    }

    public decimal ChartHigh
    {
        get => _chartHigh;
        set
        {
            this.RaiseAndSetIfChanged(ref _chartHigh, value);
            this.RaisePropertyChanged(nameof(ChartHighLabel));
        }
    }

    public decimal ChartLow
    {
        get => _chartLow;
        set
        {
            this.RaiseAndSetIfChanged(ref _chartLow, value);
            this.RaisePropertyChanged(nameof(ChartLowLabel));
        }
    }

    public decimal ChartChangePercent
    {
        get => _chartChangePercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _chartChangePercent, value);
            this.RaisePropertyChanged(nameof(ChartChangePctLabel));
            this.RaisePropertyChanged(nameof(ChartChangePctBrush));
        }
    }

    public string ChartStartLabel
    {
        get => _chartStartLabel;
        set => this.RaiseAndSetIfChanged(ref _chartStartLabel, value);
    }

    public string ChartEndLabel
    {
        get => _chartEndLabel;
        set => this.RaiseAndSetIfChanged(ref _chartEndLabel, value);
    }

    public string ChartSource
    {
        get => _chartSource;
        set => this.RaiseAndSetIfChanged(ref _chartSource, value);
    }

    public string ChartDiagnostics
    {
        get => _chartDiagnostics;
        set => this.RaiseAndSetIfChanged(ref _chartDiagnostics, value);
    }

    public string ChartTelemetry
    {
        get => _chartTelemetry;
        set => this.RaiseAndSetIfChanged(ref _chartTelemetry, value);
    }

    public string WalletSessionSummary => _walletWorkspace.ActiveWalletBanner;
    public string WalletBalanceSummary => _walletWorkspace.NativeBalanceLabel;
    public string WalletTradeStatus => _walletWorkspace.WalletCapabilityText;
    public string GlobalQuoteModeLabel => _walletWorkspace.GlobalQuoteAssetModeLabel;
    public string GlobalQuoteModeBrush => _walletWorkspace.GlobalQuoteAssetModeBrush;
    public string GlobalQuoteSummary => _walletWorkspace.GlobalQuoteAssetSummary;
    public string GlobalPositionSizingLabel => _walletWorkspace.GlobalPositionSizingLabel;
    public string GlobalPositionSizingSummary => _walletWorkspace.GlobalPositionSizingSummary;
    public string SelectedQuoteAssetSymbol
    {
        get => _selectedQuoteAssetSymbol;
        set
        {
            var normalized = NormalizeQuoteAssetSymbol(value);
            if (normalized == _selectedQuoteAssetSymbol)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedQuoteAssetSymbol, normalized);
            if (!_isSynchronizingQuoteAssetSelection)
            {
                _walletWorkspace.SyncGlobalQuoteAssetFromEffectiveSymbol(normalized);
            }

            this.RaisePropertyChanged(nameof(BuyAmountLabel));
            this.RaisePropertyChanged(nameof(QuoteAssetSummary));
            this.RaisePropertyChanged(nameof(QuoteAssetModeLabel));
            this.RaisePropertyChanged(nameof(QuoteAssetModeBrush));
            this.RaisePropertyChanged(nameof(IsSelectedPairCompatible));
            this.RaisePropertyChanged(nameof(PairCompatibilityMessage));
            this.RaisePropertyChanged(nameof(CanTradeSelectedToken));
            this.RaisePropertyChanged(nameof(CanBuySelectedTokenLive));
            this.RaisePropertyChanged(nameof(CanSellSelectedTokenLive));
            this.RaisePropertyChanged(nameof(DexGuardStatusLabel));
            this.RaisePropertyChanged(nameof(DexGuardStatusBrush));
            this.RaisePropertyChanged(nameof(DexGuardSummary));
            this.RaisePropertyChanged(nameof(DexGuardDetail));
            this.RaisePropertyChanged(nameof(SpendableQuoteBalanceLabel));
            this.RaisePropertyChanged(nameof(HasEnoughQuoteBalanceForBuy));
            this.RaisePropertyChanged(nameof(BuyAvailabilityMessage));
            this.RaisePropertyChanged(nameof(BuyAvailabilityBrush));
            this.RaisePropertyChanged(nameof(ManualBuyBlockedReason));
            this.RaisePropertyChanged(nameof(ManualSellBlockedReason));
            this.RaisePropertyChanged(nameof(EffectiveQuoteSymbolLabel));
            _ = RefreshQuoteAssetBalanceAsync();
        }
    }
    public string QuoteAssetSummary => IsNativeQuoteMode(SelectedQuoteAssetSymbol)
        ? $"Spends and receives the native {NativeQuoteSymbol} asset."
        : $"Spends and receives {SelectedQuoteAssetSymbol} on supported routers.";
    public string QuoteAssetModeLabel => IsNativeQuoteMode(SelectedQuoteAssetSymbol)
        ? "NATIVE MODE"
        : string.Equals(SelectedQuoteAssetSymbol, "USDT", StringComparison.OrdinalIgnoreCase)
            ? "USDT ACTIVE"
            : $"{SelectedQuoteAssetSymbol} ACTIVE";
    public string QuoteAssetModeBrush => IsNativeQuoteMode(SelectedQuoteAssetSymbol)
        ? "#8FA3B8"
        : string.Equals(SelectedQuoteAssetSymbol, "USDT", StringComparison.OrdinalIgnoreCase)
            ? "#14E0C1"
            : "#F4B860";
    public decimal SelectedQuoteAssetBalance
    {
        get => _selectedQuoteAssetBalance;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedQuoteAssetBalance, value);
            this.RaisePropertyChanged(nameof(QuoteAssetBalanceLabel));
            this.RaisePropertyChanged(nameof(SpendableQuoteBalanceLabel));
            this.RaisePropertyChanged(nameof(HasEnoughQuoteBalanceForBuy));
            this.RaisePropertyChanged(nameof(BuyAvailabilityMessage));
            this.RaisePropertyChanged(nameof(BuyAvailabilityBrush));
            this.RaisePropertyChanged(nameof(CanBuySelectedTokenLive));
            this.RaisePropertyChanged(nameof(ManualBuyBlockedReason));
            this.RaisePropertyChanged(nameof(DexGuardStatusLabel));
            this.RaisePropertyChanged(nameof(DexGuardSummary));
        }
    }
    public bool IsQuoteAssetBalanceLoading
    {
        get => _isQuoteAssetBalanceLoading;
        set
        {
            this.RaiseAndSetIfChanged(ref _isQuoteAssetBalanceLoading, value);
            this.RaisePropertyChanged(nameof(QuoteAssetBalanceLabel));
            this.RaisePropertyChanged(nameof(SpendableQuoteBalanceLabel));
        }
    }
    public string QuoteAssetBalanceLabel => IsQuoteAssetBalanceLoading
        ? "Loading balance..."
        : IsNativeQuoteMode(SelectedQuoteAssetSymbol)
            ? WalletBalanceSummary
            : $"{SelectedQuoteAssetBalance:0.######} {SelectedQuoteAssetSymbol}";
    public string SpendableQuoteBalanceLabel => IsQuoteAssetBalanceLoading
        ? "Calculating spendable balance..."
        : $"Available to spend: {AvailableSpendableQuoteBalance:0.######} {EffectiveQuoteSymbolLabel}";
    public decimal AvailableSpendableQuoteBalance => IsNativeQuoteMode(SelectedQuoteAssetSymbol)
        ? 0m
        : Math.Max(0m, SelectedQuoteAssetBalance);
    public bool HasEnoughQuoteBalanceForBuy => BuyAmountBnb <= 0m || IsNativeQuoteMode(SelectedQuoteAssetSymbol) || BuyAmountBnb <= AvailableSpendableQuoteBalance;
    public string BuyAvailabilityBrush => HasEnoughQuoteBalanceForBuy ? "#14E0C1" : "#FF6B6B";
    public string BuyAvailabilityMessage => IsNativeQuoteMode(SelectedQuoteAssetSymbol)
        ? "Native buy mode uses the connected wallet native balance."
        : HasEnoughQuoteBalanceForBuy
            ? $"Spend amount fits the available {SelectedQuoteAssetSymbol} balance."
            : $"Not enough {SelectedQuoteAssetSymbol}. Need {BuyAmountBnb:0.######}, available {AvailableSpendableQuoteBalance:0.######}.";
    public bool IsSelectedPairCompatible => SelectedToken is null || IsPairCompatibleWithQuoteMode(SelectedToken.TokenInfo);
    public string PairCompatibilityMessage => SelectedToken is null
        ? "Pick a token to validate the current quote mode."
        : IsSelectedPairCompatible
            ? $"Pair quote {SelectedToken.TokenInfo.QuoteSymbol} matches {QuoteAssetModeLabel}."
            : $"Pair quote {SelectedToken.TokenInfo.QuoteSymbol} is blocked in {QuoteAssetModeLabel}. Choose a token quoted in {EffectiveQuoteSymbolLabel}.";
    public string EffectiveQuoteSymbolLabel => IsNativeQuoteMode(SelectedQuoteAssetSymbol)
        ? NativeQuoteSymbol
        : SelectedQuoteAssetSymbol;
    public string BuyAmountLabel => $"Buy ({(IsNativeQuoteMode(SelectedQuoteAssetSymbol) ? NativeQuoteSymbol : SelectedQuoteAssetSymbol)})";

    public decimal SlippagePercent
    {
        get => _slippagePercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _slippagePercent, Math.Max(0.1m, Math.Min(50m, value)));
            this.RaisePropertyChanged(nameof(SlippageSummary));
            QueueBuyQuote();
        }
    }
    public string SlippageSummary => $"Slippage: {_slippagePercent:0.##}% — router will revert if output drops below this tolerance.";

    public string BuyQuoteLabel
    {
        get => _buyQuoteLabel;
        private set => this.RaiseAndSetIfChanged(ref _buyQuoteLabel, value);
    }
    public string BuyQuoteBrush
    {
        get => _buyQuoteBrush;
        private set => this.RaiseAndSetIfChanged(ref _buyQuoteBrush, value);
    }

    public decimal TokenBalance
    {
        get => _tokenBalance;
        private set => this.RaiseAndSetIfChanged(ref _tokenBalance, value);
    }
    public string TokenBalanceLabel
    {
        get => _tokenBalanceLabel;
        private set => this.RaiseAndSetIfChanged(ref _tokenBalanceLabel, value);
    }
    public bool IsTokenBalanceLoading
    {
        get => _isTokenBalanceLoading;
        private set => this.RaiseAndSetIfChanged(ref _isTokenBalanceLoading, value);
    }

    private string GetManualBuyBlockedReason()
    {
        if (SelectedToken is null)
        {
            return "Select a token first.";
        }

        var routeReason = _walletWorkspace.GetDexExecutionBlockReason(
            "DEX manual buy",
            SelectedToken.TokenInfo.ChainId,
            SelectedToken.TokenInfo.DexId);
        if (!string.IsNullOrWhiteSpace(routeReason))
        {
            return routeReason;
        }

        if (!IsSelectedPairCompatible)
        {
            return PairCompatibilityMessage;
        }

        if (BuyAmountBnb <= 0m)
        {
            return "Enter a buy amount above zero.";
        }

        if (!HasEnoughQuoteBalanceForBuy)
        {
            return BuyAvailabilityMessage;
        }

        return string.Empty;
    }

    private string GetManualSellBlockedReason()
    {
        if (SelectedToken is null)
        {
            return "Select a token first.";
        }

        var routeReason = _walletWorkspace.GetDexExecutionBlockReason(
            "DEX manual sell",
            SelectedToken.TokenInfo.ChainId,
            SelectedToken.TokenInfo.DexId);
        if (!string.IsNullOrWhiteSpace(routeReason))
        {
            return routeReason;
        }

        if (!IsSelectedPairCompatible)
        {
            return PairCompatibilityMessage;
        }

        if (SellAmountTokens <= 0m)
        {
            return "Enter a token amount above zero to sell.";
        }

        return string.Empty;
    }

    public bool HasChartData => ChartCandles.Count > 0;
    public string ChartPolylinePoints => string.Join(
        ' ',
        ChartPoints.Select(static point =>
            $"{point.X.ToString("0.##", CultureInfo.InvariantCulture)},{point.Y.ToString("0.##", CultureInfo.InvariantCulture)}"));

    // ── Chart metric labels (CEX-style header) ────────────────────────────────
    public string ChartHighLabel => ChartHigh > 0 ? FormatPrice(ChartHigh) : "--";
    public string ChartLowLabel  => ChartLow  > 0 ? FormatPrice(ChartLow)  : "--";
    public string ChartChangePctLabel => ChartChangePercent == 0 ? "--" : $"{ChartChangePercent:+0.00;-0.00}%";
    public string ChartChangePctBrush => ChartChangePercent >= 0 ? "#21E6C1" : "#FF6B6B";
    public string DexVolume24hLabel => SelectedToken is null
        ? "--"
        : SelectedToken.TokenInfo.Volume24h > 0
            ? $"$ {SelectedToken.TokenInfo.Volume24h:N0}"
            : "--";
    public string DexLiquidityLabel => SelectedToken is null
        ? "--"
        : SelectedToken.TokenInfo.LiquidityUsd > 0
            ? $"$ {SelectedToken.TokenInfo.LiquidityUsd:N0}"
            : "--";

    private static string FormatPrice(decimal price) => price switch
    {
        >= 1000m   => $"$ {price:N2}",
        >= 1m      => $"$ {price:N4}",
        >= 0.0001m => $"$ {price:N6}",
        _          => $"$ {price:N8}"
    };

    // ── Timeframe button states (CEX-style active highlighting) ───────────────
    public string DexTimeframe15MBackground => GetDexTimeframeBackground("15M");
    public string DexTimeframe1HBackground  => GetDexTimeframeBackground("1H");
    public string DexTimeframe4HBackground  => GetDexTimeframeBackground("4H");
    public string DexTimeframe1DBackground  => GetDexTimeframeBackground("1D");
    public string DexTimeframe1WBackground  => GetDexTimeframeBackground("1W");
    public string DexTimeframe15MForeground => GetDexTimeframeForeground("15M");
    public string DexTimeframe1HForeground  => GetDexTimeframeForeground("1H");
    public string DexTimeframe4HForeground  => GetDexTimeframeForeground("4H");
    public string DexTimeframe1DForeground  => GetDexTimeframeForeground("1D");
    public string DexTimeframe1WForeground  => GetDexTimeframeForeground("1W");

    private string GetDexTimeframeBackground(string tf) =>
        string.Equals(SelectedChartRange, tf, StringComparison.OrdinalIgnoreCase) ? "#17373B" : "#0F1721";
    private string GetDexTimeframeForeground(string tf) =>
        string.Equals(SelectedChartRange, tf, StringComparison.OrdinalIgnoreCase) ? "#F4F7FB" : "#8FA3B8";

    // ── Recent DEX trade history (blotter) ────────────────────────────────────
    public ObservableCollection<DexTradeRecordViewModel> RecentDexTrades { get; } = new();
    public bool ShowDexTradesPlaceholder => RecentDexTrades.Count == 0;

    private void AddTradeRecord(string side, string symbol, decimal amount, decimal price, string txHash, bool success)
    {
        var record = new DexTradeRecordViewModel(side, symbol, amount, price, txHash, success, DateTime.UtcNow);
        RecentDexTrades.Insert(0, record);
        if (RecentDexTrades.Count > 100) RecentDexTrades.RemoveAt(RecentDexTrades.Count - 1);
        this.RaisePropertyChanged(nameof(ShowDexTradesPlaceholder));
    }

    // ── LP Calculator ─────────────────────────────────────────────────────────
    private decimal _lpEntryPrice;
    private decimal _lpPriceLower;
    private decimal _lpPriceUpper;
    private string  _lpIlResult    = string.Empty;
    private string  _lpIlBrush     = "#8FA3B8";
    private string  _lpV3IlResult  = string.Empty;

    public decimal LpEntryPrice
    {
        get => _lpEntryPrice;
        set { this.RaiseAndSetIfChanged(ref _lpEntryPrice, value); RecalcLp(); }
    }
    public decimal LpPriceLower
    {
        get => _lpPriceLower;
        set { this.RaiseAndSetIfChanged(ref _lpPriceLower, value); RecalcLp(); }
    }
    public decimal LpPriceUpper
    {
        get => _lpPriceUpper;
        set { this.RaiseAndSetIfChanged(ref _lpPriceUpper, value); RecalcLp(); }
    }
    public string LpIlResult   { get => _lpIlResult;   private set => this.RaiseAndSetIfChanged(ref _lpIlResult, value); }
    public string LpIlBrush    { get => _lpIlBrush;    private set => this.RaiseAndSetIfChanged(ref _lpIlBrush, value); }
    public string LpV3IlResult { get => _lpV3IlResult; private set => this.RaiseAndSetIfChanged(ref _lpV3IlResult, value); }

    private void RecalcLp()
    {
        var current = SelectedToken?.TokenInfo.PriceUsd ?? 0m;
        if (current <= 0m || _lpEntryPrice <= 0m) { LpIlResult = "--"; return; }

        // V2 IL
        var v2Il = LiquidityPoolCalculator.CalculateV2ImpermanentLoss(current / _lpEntryPrice);
        LpIlResult = $"{v2Il:+0.00;-0.00;0}%";
        LpIlBrush  = v2Il >= 0 ? "#21E6C1" : "#FF6B6B";

        // V3 IL (only when range is set)
        if (_lpPriceLower > 0m && _lpPriceUpper > _lpPriceLower)
        {
            var v3Il = LiquidityPoolCalculator.CalculateV3ImpermanentLoss(
                _lpEntryPrice, current, _lpPriceLower, _lpPriceUpper);
            LpV3IlResult = $"V3 IL: {v3Il:+0.00;-0.00;0}%";
        }
        else
        {
            LpV3IlResult = string.Empty;
        }
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> BuyCommand { get; }
    public ReactiveCommand<Unit, Unit> SellCommand { get; }
    public ReactiveCommand<Unit, Unit> UseMaxBuyAmountCommand { get; }
    public ReactiveCommand<string, Unit> ApplyBuyBalancePresetCommand { get; }
    public ReactiveCommand<string, Unit> ApplySellBalancePresetCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshTokenBalanceCommand { get; }
    public ReactiveCommand<string, Unit> SelectChartRangeCommand { get; }

    public DexTokenAiVerdictViewModel TokenVerdict { get; } = new();
    public ReactiveCommand<Unit, Unit> DeepScanTokenCommand { get; }

    public DexTradingViewModel(WalletWorkspaceViewModel walletWorkspace)
    {
        _walletWorkspace = walletWorkspace;
        _dexClient = new DexScreenerClient();
        _geckoTerminalClient = new GeckoTerminalClient();
        _walletWorkspace.PropertyChanged += OnWalletWorkspacePropertyChanged;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync, outputScheduler: App.UiScheduler);
        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync, outputScheduler: App.UiScheduler);
        BuyCommand = ReactiveCommand.CreateFromTask(BuyAsync, outputScheduler: App.UiScheduler);
        SellCommand = ReactiveCommand.CreateFromTask(SellAsync, outputScheduler: App.UiScheduler);
        UseMaxBuyAmountCommand = ReactiveCommand.Create(UseMaxBuyAmount, outputScheduler: App.UiScheduler);
        ApplyBuyBalancePresetCommand = ReactiveCommand.Create<string>(ApplyBuyBalancePreset, outputScheduler: App.UiScheduler);
        ApplySellBalancePresetCommand = ReactiveCommand.Create<string>(ApplySellBalancePreset, outputScheduler: App.UiScheduler);
        RefreshTokenBalanceCommand = ReactiveCommand.CreateFromTask(RefreshTokenBalanceAsync, outputScheduler: App.UiScheduler);
        SelectChartRangeCommand = ReactiveCommand.CreateFromTask<string>(SelectChartRangeAsync, outputScheduler: App.UiScheduler);
        DeepScanTokenCommand = ReactiveCommand.CreateFromTask(DeepScanTokenAsync, outputScheduler: App.UiScheduler);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _refreshTimer.Tick += async (_, _) => await AutoRefreshAsync();
        _refreshTimer.Start();

        _chartDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _chartDebounceTimer.Tick += async (_, _) =>
        {
            _chartDebounceTimer.Stop();
            await LoadChartAsync();
        };

        _quoteDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _quoteDebounceTimer.Tick += async (_, _) =>
        {
            _quoteDebounceTimer.Stop();
            await RefreshBuyQuoteAsync();
        };

        RefreshQuoteAssetOptions();
        EnsureValidQuoteAssetSelection();
        ApplyGlobalDexSizingIfReady();
        _ = RefreshAsync();
    }

    private async Task RefreshTokenVerdictAsync(DexTokenItemViewModel? token)
    {
        if (token is null)
        {
            TokenVerdict.Reset();
            return;
        }

        var seq = ++_verdictSeq;
        TokenVerdict.IsBusy = true;
        TokenVerdict.DeepScanNote = null;
        try
        {
            var verdict = await _tokenAiService.AssessAsync(token.TokenInfo);
            if (seq != _verdictSeq || !ReferenceEquals(SelectedToken, token))
                return; // a newer selection superseded this one
            TokenVerdict.ApplyVerdict(verdict);
        }
        catch
        {
            // Never let verdict failure break token selection; keep prior state.
        }
        finally
        {
            if (seq == _verdictSeq)
                TokenVerdict.IsBusy = false;
        }
    }

    private async Task DeepScanTokenAsync()
    {
        var token = SelectedToken;
        if (token is null)
            return;

        TokenVerdict.DeepScanBusy = true;
        TokenVerdict.DeepScanNote = null;
        try
        {
            var scan = await _securityScanner.ScanAsync(
                token.TokenInfo.TokenAddress, token.TokenInfo.ChainId);

            if (!ReferenceEquals(SelectedToken, token))
                return; // token changed while scanning

            if (scan.ScanFailed)
            {
                TokenVerdict.DeepScanNote = $"Deep scan unavailable: {scan.Source}";
                return;
            }

            var summary = DexSecuritySummary.Build(scan);
            _tokenAiService.Invalidate(token.TokenInfo);
            var verdict = await _tokenAiService.AssessAsync(token.TokenInfo, summary);

            if (!ReferenceEquals(SelectedToken, token))
                return;

            TokenVerdict.ApplyVerdict(verdict);
        }
        catch (System.Exception ex)
        {
            TokenVerdict.DeepScanNote = $"Deep scan error: {ex.Message}";
        }
        finally
        {
            TokenVerdict.DeepScanBusy = false;
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _chartDebounceTimer.Stop();
        _quoteDebounceTimer.Stop();
        _walletWorkspace.PropertyChanged -= OnWalletWorkspacePropertyChanged;
        _securityScanner.Dispose();
    }

    private string NativeQuoteSymbol =>
        DexQuoteAssetCatalog.GetOptions(_walletWorkspace.SelectedNetwork)
            .FirstOrDefault(static option => option.IsNative)?.Symbol
        ?? _walletWorkspace.SelectedNetwork.ToUpperInvariant();

    private async Task RunOnUiAsync(Action action)
    {
        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private async Task<T> RunOnUiAsync<T>(Func<T> action)
    {
        return await Dispatcher.UIThread.InvokeAsync(action);
    }

    private async Task AutoRefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await RefreshAsync();
        }
        else
        {
            await SearchAsync();
        }
    }

    private async Task RefreshAsync()
    {
        var chainId = ChainIdForFilter(_selectedChainFilter);
        if (!string.IsNullOrEmpty(chainId))
        {
            await LoadTokensAsync(
                () => _dexClient.GetMomentumScoutTokensAsync(new[] { chainId }, 60),
                $"{_selectedChainFilter} tokens loaded.");
            return;
        }

        await LoadTokensAsync(() => _dexClient.GetLatestTokensAsync(), "Latest DEX tokens refreshed.");
    }

    private async Task SearchAsync()
    {
        await LoadTokensAsync(() => _dexClient.SearchTokensAsync(SearchText), $"Search refreshed for '{SearchText}'.");
    }

    private async Task LoadTokensAsync(Func<Task<IReadOnlyList<DexTokenInfo>>> loader, string successMessage)
    {
        try
        {
            await RunOnUiAsync(() => IsLoading = true);
            var tokens = await loader();
            var sampleTimeUtc = DateTime.UtcNow;

            foreach (var token in tokens)
            {
                RecordPriceSample(token, sampleTimeUtc);
            }

            await RunOnUiAsync(() =>
            {
                _loadedTokens = tokens;
                _lastLoadSuccessMessage = successMessage;
                ApplyTokenFilter();
                LastUpdatedLocal = DateTime.Now;
            });

            await RunOnUiAsync(QueueChartReload);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => StatusMessage = $"DEX refresh failed: {ex.Message}");
        }
        finally
        {
            await RunOnUiAsync(() => IsLoading = false);
        }
    }

    private void ApplyTokenFilter()
    {
        var previousTokenAddress = SelectedToken?.TokenAddress;

        var filtered = DexTokenFilter.Apply(
            _loadedTokens,
            ChainIdForFilter(_selectedChainFilter),
            ThresholdValue(_selectedMinLiquidity),
            ThresholdValue(_selectedMinVolume),
            SortModeKey(_selectedSortMode));

        Tokens.Clear();
        foreach (var token in filtered)
        {
            var item = new DexTokenItemViewModel();
            item.Update(token);
            Tokens.Add(item);
        }

        SelectedToken = Tokens.FirstOrDefault(t => string.Equals(t.TokenAddress, previousTokenAddress, StringComparison.OrdinalIgnoreCase))
            ?? Tokens.FirstOrDefault();

        StatusMessage = Tokens.Count == 0
            ? (_loadedTokens.Count == 0 ? "No tokens found." : "No tokens match filters.")
            : _lastLoadSuccessMessage;
    }

    private static string? ChainIdForFilter(string display) => display switch
    {
        "BSC"      => "bsc",
        "Ethereum" => "ethereum",
        "Base"     => "base",
        "Solana"   => "solana",
        "Tron"     => "tron",
        _          => null,
    };

    private static decimal ThresholdValue(string preset) => preset switch
    {
        "$10k"  => 10_000m,
        "$50k"  => 50_000m,
        "$100k" => 100_000m,
        "$250k" => 250_000m,
        "$500k" => 500_000m,
        "$1M"   => 1_000_000m,
        _       => 0m,
    };

    private static string SortModeKey(string display) => display == "24h Change" ? "Change" : display;

    private void RecordPriceSample(DexTokenInfo token, DateTime sampleTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(token.TokenAddress) || token.PriceUsd <= 0)
        {
            return;
        }

        if (!_priceHistory.TryGetValue(token.TokenAddress, out var history))
        {
            history = [];
            _priceHistory[token.TokenAddress] = history;
        }

        var hasSameTimestamp = history.Count > 0
            && history[^1].TimestampUtc == sampleTimeUtc;

        if (!hasSameTimestamp)
        {
            history.Add(new DexPriceSample(sampleTimeUtc, token.PriceUsd));
        }

        var trimBefore = sampleTimeUtc.AddDays(-8);
        history.RemoveAll(sample => sample.TimestampUtc < trimBefore);
        history.Sort(static (left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));

        // Persist to disk every 20 samples so history survives app restarts
        if (history.Count % 20 == 0)
        {
            var chainId = token.ChainId;
            var address = token.TokenAddress;
            var snapshot = history.Select(s => new DexPriceSampleDto(s.TimestampUtc, s.PriceUsd)).ToList();
            _ = Task.Run(() => _historyCache.Save(chainId, address, snapshot));
        }
    }

    private static void SeedSyntheticHistory(List<DexPriceSample> history, DexTokenInfo token, DateTime nowUtc)
    {
        if (token.PriceUsd <= 0)
        {
            return;
        }

        var currentPrice = token.PriceUsd;
        var price24hAgo = ReverseChange(currentPrice, token.PriceChange24h);
        var price1hAgo = ReverseChange(currentPrice, token.PriceChange1h);
        var price5mAgo = ReverseChange(currentPrice, token.PriceChange5m);
        var price7dAgo = BuildCompoundedPrice(price24hAgo, token.PriceChange24h, 6);

        foreach (var anchor in BuildSyntheticAnchors(token, nowUtc)
                     .Where(static sample => sample.PriceUsd > 0)
                     .OrderBy(static sample => sample.TimestampUtc))
        {
            if (history.Count == 0 || history[^1].TimestampUtc != anchor.TimestampUtc)
            {
                history.Add(anchor);
            }
        }
    }

    private async Task BuyAsync()
    {
        var selectedToken = await RunOnUiAsync(() => SelectedToken);
        if (selectedToken is null)
        {
            await RunOnUiAsync(() => StatusMessage = "Choose a token before buying.");
            return;
        }

        if (!IsPairCompatibleWithQuoteMode(selectedToken.TokenInfo))
        {
            await RunOnUiAsync(() => StatusMessage = PairCompatibilityMessage);
            return;
        }

        if (!HasEnoughQuoteBalanceForBuy)
        {
            await RunOnUiAsync(() => StatusMessage = BuyAvailabilityMessage);
            return;
        }

        if (!_walletWorkspace.CanTradeChainId(selectedToken.TokenInfo.ChainId))
        {
            await RunOnUiAsync(() => StatusMessage = $"Manual trading requires a trade-enabled wallet on the same network as the selected token. Current wallet network: {_walletWorkspace.SelectedNetwork}.");
            return;
        }

        var dexGateway = _walletWorkspace.ActiveDexGateway;
        if (dexGateway is null)
        {
            await RunOnUiAsync(() => StatusMessage = "Connect a trade-enabled wallet in the Wallet tab to enable DEX trading.");
            return;
        }

        if (!dexGateway.SupportsDex(selectedToken.TokenInfo.DexId))
        {
            await RunOnUiAsync(() => StatusMessage = $"DEX connector '{selectedToken.TokenInfo.DexId}' is not wired on {_walletWorkspace.SelectedNetwork}. Supported: {dexGateway.SupportedDexesLabel}.");
            return;
        }

        if (!_walletWorkspace.TryApproveLiveExecution("DEX manual buy", out var executionReason))
        {
            await RunOnUiAsync(() => StatusMessage = executionReason);
            return;
        }

        var (buyAmount, quoteAssetSymbol) = await RunOnUiAsync(() => (BuyAmountBnb, SelectedQuoteAssetSymbol));
        if (!IsNativeQuoteMode(quoteAssetSymbol) &&
            !_walletWorkspace.TryApproveUsdRisk(buyAmount, 0m, 0m, out var riskReason))
        {
            await RunOnUiAsync(() => StatusMessage = riskReason);
            return;
        }

        var slippage = await RunOnUiAsync(() => SlippagePercent);
        try
        {
            var spendAssetSymbol = IsNativeQuoteMode(quoteAssetSymbol) || string.Equals(quoteAssetSymbol, dexGateway.NativeSymbol, StringComparison.OrdinalIgnoreCase) ? null : quoteAssetSymbol;
            var transactionHash = await dexGateway.BuyTokenAsync(selectedToken.TokenAddress, buyAmount, slippagePercent: slippage, dexId: selectedToken.TokenInfo.DexId, spendAssetSymbol: spendAssetSymbol);
            await RunOnUiAsync(() =>
            {
                StatusMessage = $"Buy sent via {(spendAssetSymbol ?? dexGateway.NativeSymbol)}: {transactionHash}";
                AddTradeRecord("BUY", selectedToken.DisplayName, buyAmount, selectedToken.TokenInfo.PriceUsd, transactionHash, true);
            });
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                StatusMessage = $"Buy failed: {ex.Message}";
                AddTradeRecord("BUY", selectedToken.DisplayName, buyAmount, selectedToken.TokenInfo.PriceUsd, string.Empty, false);
            });
        }
    }

    private async Task SellAsync()
    {
        var selectedToken = await RunOnUiAsync(() => SelectedToken);
        if (selectedToken is null)
        {
            await RunOnUiAsync(() => StatusMessage = "Choose a token before selling.");
            return;
        }

        if (!IsPairCompatibleWithQuoteMode(selectedToken.TokenInfo))
        {
            await RunOnUiAsync(() => StatusMessage = PairCompatibilityMessage);
            return;
        }

        if (!_walletWorkspace.CanTradeChainId(selectedToken.TokenInfo.ChainId))
        {
            await RunOnUiAsync(() => StatusMessage = $"Manual trading requires a trade-enabled wallet on the same network as the selected token. Current wallet network: {_walletWorkspace.SelectedNetwork}.");
            return;
        }

        var dexGateway = _walletWorkspace.ActiveDexGateway;
        if (dexGateway is null)
        {
            await RunOnUiAsync(() => StatusMessage = "Connect a trade-enabled wallet in the Wallet tab to enable DEX trading.");
            return;
        }

        if (!dexGateway.SupportsDex(selectedToken.TokenInfo.DexId))
        {
            await RunOnUiAsync(() => StatusMessage = $"DEX connector '{selectedToken.TokenInfo.DexId}' is not wired on {_walletWorkspace.SelectedNetwork}. Supported: {dexGateway.SupportedDexesLabel}.");
            return;
        }

        if (!_walletWorkspace.TryApproveLiveExecution("DEX manual sell", out var executionReason))
        {
            await RunOnUiAsync(() => StatusMessage = executionReason);
            return;
        }

        var (sellAmount, quoteAssetSymbol, slippage) = await RunOnUiAsync(() => (SellAmountTokens, SelectedQuoteAssetSymbol, SlippagePercent));

        try
        {
            var receiveAssetSymbol = IsNativeQuoteMode(quoteAssetSymbol) || string.Equals(quoteAssetSymbol, dexGateway.NativeSymbol, StringComparison.OrdinalIgnoreCase) ? null : quoteAssetSymbol;
            var transactionHash = await dexGateway.SellTokenAsync(selectedToken.TokenAddress, sellAmount, slippagePercent: slippage, dexId: selectedToken.TokenInfo.DexId, receiveAssetSymbol: receiveAssetSymbol);
            await RunOnUiAsync(() =>
            {
                StatusMessage = $"Sell sent to {(receiveAssetSymbol ?? dexGateway.NativeSymbol)}: {transactionHash}";
                AddTradeRecord("SELL", selectedToken.DisplayName, sellAmount, selectedToken.TokenInfo.PriceUsd, transactionHash, true);
            });
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                StatusMessage = $"Sell failed: {ex.Message}";
                AddTradeRecord("SELL", selectedToken.DisplayName, sellAmount, selectedToken.TokenInfo.PriceUsd, string.Empty, false);
            });
        }
    }

    private async Task SelectChartRangeAsync(string range)
    {
        await RunOnUiAsync(() =>
        {
            SelectedChartRange = string.IsNullOrWhiteSpace(range) ? "1D" : range;
            this.RaisePropertyChanged(nameof(DexTimeframe15MBackground));
            this.RaisePropertyChanged(nameof(DexTimeframe1HBackground));
            this.RaisePropertyChanged(nameof(DexTimeframe4HBackground));
            this.RaisePropertyChanged(nameof(DexTimeframe1DBackground));
            this.RaisePropertyChanged(nameof(DexTimeframe1WBackground));
            this.RaisePropertyChanged(nameof(DexTimeframe15MForeground));
            this.RaisePropertyChanged(nameof(DexTimeframe1HForeground));
            this.RaisePropertyChanged(nameof(DexTimeframe4HForeground));
            this.RaisePropertyChanged(nameof(DexTimeframe1DForeground));
            this.RaisePropertyChanged(nameof(DexTimeframe1WForeground));
        });
        QueueChartReload();
    }

    private void UseMaxBuyAmount()
    {
        if (IsNativeQuoteMode(SelectedQuoteAssetSymbol))
        {
            StatusMessage = "MAX currently works for quote-asset mode. Switch to USDT/USDC to auto-fill the spendable amount.";
            return;
        }

        BuyAmountBnb = RoundBuyAmount(AvailableSpendableQuoteBalance);
        StatusMessage = $"Buy amount filled to MAX: {BuyAmountBnb:0.######} {SelectedQuoteAssetSymbol}.";
    }

    private void ApplyBuyBalancePreset(string? preset)
    {
        _walletWorkspace.GlobalPositionSizingPercent = decimal.TryParse(preset, out var globalPercent) ? globalPercent : _walletWorkspace.GlobalPositionSizingPercent;
        _pendingGlobalSizingApply = true;

        if (IsNativeQuoteMode(SelectedQuoteAssetSymbol))
        {
            StatusMessage = "Balance presets currently work for quote-asset mode. Switch to USDT/USDC first.";
            return;
        }

        var ratio = preset?.Trim() switch
        {
            "25" => 0.25m,
            "50" => 0.50m,
            "75" => 0.75m,
            "100" => 1.00m,
            _ => 0m
        };

        if (ratio <= 0m)
        {
            return;
        }

        BuyAmountBnb = RoundBuyAmount(AvailableSpendableQuoteBalance * ratio);
        StatusMessage = $"Buy amount set to {preset}% of available {SelectedQuoteAssetSymbol}: {BuyAmountBnb:0.######}.";
    }

    private decimal RoundBuyAmount(decimal amount)
    {
        var decimals = ResolveSpendPrecision();
        var rounded = Math.Round(Math.Max(0m, amount), decimals, MidpointRounding.AwayFromZero);
        var factor = (decimal)Math.Pow(10, decimals);
        return factor <= 0 ? rounded : Math.Floor(rounded * factor) / factor;
    }

    private int ResolveSpendPrecision()
    {
        if (IsNativeQuoteMode(SelectedQuoteAssetSymbol))
        {
            return 4;
        }

        return SelectedQuoteAssetSymbol.ToUpperInvariant() switch
        {
            "USDT" => 6,
            "USDC" => 6,
            _ => 4
        };
    }

    private bool IsPairCompatibleWithQuoteMode(DexTokenInfo tokenInfo)
    {
        if (IsNativeQuoteMode(SelectedQuoteAssetSymbol))
        {
            return true;
        }

        if (string.Equals(tokenInfo.QuoteSymbol, SelectedQuoteAssetSymbol, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(_walletWorkspace.SelectedNetwork, "Base", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(SelectedQuoteAssetSymbol, "USDT", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(tokenInfo.QuoteSymbol, "USDC", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshQuoteAssetBalanceAsync()
    {
        var gateway = _walletWorkspace.ActiveDexGateway;
        if (gateway is null || !_walletWorkspace.CanUseDexTradingOnSelectedNetwork)
        {
            await RunOnUiAsync(() =>
            {
                IsQuoteAssetBalanceLoading = false;
                SelectedQuoteAssetBalance = 0m;
                this.RaisePropertyChanged(nameof(QuoteAssetBalanceLabel));
                this.RaisePropertyChanged(nameof(SpendableQuoteBalanceLabel));
            });
            ApplyGlobalDexSizingIfReady();
            return;
        }

        var quoteAsset = DexQuoteAssetCatalog.Find(_walletWorkspace.SelectedNetwork, SelectedQuoteAssetSymbol);
        if (quoteAsset is null || quoteAsset.IsNative || string.IsNullOrWhiteSpace(quoteAsset.ContractAddress))
        {
            await RunOnUiAsync(() =>
            {
                IsQuoteAssetBalanceLoading = false;
                SelectedQuoteAssetBalance = 0m;
                this.RaisePropertyChanged(nameof(QuoteAssetBalanceLabel));
                this.RaisePropertyChanged(nameof(SpendableQuoteBalanceLabel));
            });
            ApplyGlobalDexSizingIfReady();
            return;
        }

        try
        {
            await RunOnUiAsync(() => IsQuoteAssetBalanceLoading = true);
            var balance = await gateway.GetTokenBalanceAsync(quoteAsset.ContractAddress);
            await RunOnUiAsync(() =>
            {
                SelectedQuoteAssetBalance = balance;
                IsQuoteAssetBalanceLoading = false;
                this.RaisePropertyChanged(nameof(QuoteAssetBalanceLabel));
                this.RaisePropertyChanged(nameof(SpendableQuoteBalanceLabel));
            });
            ApplyGlobalDexSizingIfReady();
        }
        catch
        {
            await RunOnUiAsync(() =>
            {
                SelectedQuoteAssetBalance = 0m;
                IsQuoteAssetBalanceLoading = false;
                this.RaisePropertyChanged(nameof(QuoteAssetBalanceLabel));
                this.RaisePropertyChanged(nameof(SpendableQuoteBalanceLabel));
            });
            ApplyGlobalDexSizingIfReady();
        }
    }

    private void QueueChartReload()
    {
        _chartDebounceTimer.Stop();
        _chartDebounceTimer.Start();
    }

    private async Task LoadChartAsync()
    {
        var selectedToken = await RunOnUiAsync(() => SelectedToken);
        if (selectedToken is null)
        {
            ClearChart("Select a token to load price history.");
            return;
        }

        try
        {
            await RunOnUiAsync(() =>
            {
                IsChartLoading = true;
                ChartStatusMessage = "Loading DEX chart...";
            });

            // Merge disk cache into in-memory history first
            MergePersistedHistory(selectedToken.TokenInfo);

            var chartRange = await RunOnUiAsync(() => SelectedChartRange);
            IReadOnlyList<DexOhlcvPoint> candles;
            string diagnostics;
            string sourceLabel;
            string statusLabel;

            // ── Source 1: GeckoTerminal live OHLCV ───────────────────────────
            candles = await TryLoadLiveCandlesAsync(selectedToken.TokenInfo, chartRange);
            if (candles.Count >= 2)
            {
                // Persist to disk cache so future sessions start with real data
                _ = Task.Run(() => _historyCache.SaveCandles(
                    selectedToken.TokenInfo.ChainId,
                    selectedToken.TokenInfo.TokenAddress,
                    candles));

                candles     = NormalizeDexCandlesForDisplay(candles, selectedToken.TokenInfo, chartRange);
                diagnostics = $"Live pool OHLCV from GeckoTerminal ({selectedToken.TokenInfo.DexId}).";
                sourceLabel = "GeckoTerminal OHLCV";
                statusLabel = $"Live DEX chart · {chartRange} range · {candles.Count} candles.";
            }
            else
            {
                // ── Source 2: local samples + disk cache ─────────────────────
                candles = BuildLocalCandles(selectedToken.TokenInfo, chartRange, out diagnostics);
                if (candles.Count >= 2)
                {
                    candles     = NormalizeDexCandlesForDisplay(candles, selectedToken.TokenInfo, chartRange);
                    sourceLabel = _historyCache.HasCache(selectedToken.TokenInfo.ChainId, selectedToken.TokenInfo.TokenAddress)
                        ? "Disk cache + live samples"
                        : "Internal sampler";
                    statusLabel = $"Cached DEX chart · {chartRange} · {candles.Count} candles.";
                }
                else
                {
                    // ── Source 3: on-chain Swap event reconstruction ──────────
                    candles = await TryLoadOnChainHistoryAsync(selectedToken.TokenInfo, chartRange);
                    if (candles.Count >= 2)
                    {
                        _ = Task.Run(() => _historyCache.SaveCandles(
                            selectedToken.TokenInfo.ChainId,
                            selectedToken.TokenInfo.TokenAddress,
                            candles));

                        candles     = NormalizeDexCandlesForDisplay(candles, selectedToken.TokenInfo, chartRange);
                        diagnostics = $"On-chain Swap events reconstructed from {candles.Count} trades.";
                        sourceLabel = "On-chain reconstruction";
                        statusLabel = $"On-chain chart · {chartRange} · {candles.Count} candles.";
                    }
                    else
                    {
                        // ── No real data available: honest empty state ───────
                        ClearChart(
                            "No live chart data for this pair yet — collecting live ticks; on-chain scan running.",
                            "No live OHLCV, on-chain history or local samples available yet.");
                        TriggerBackgroundOnChainScan(selectedToken.TokenInfo);
                        return;
                    }
                }
            }

            if (candles.Count < 2)
            {
                ClearChart("Not enough history yet. On-chain scan is running in background.", diagnostics);
                TriggerBackgroundOnChainScan(selectedToken.TokenInfo);
                return;
            }

            var rawSampleCount = _priceHistory.TryGetValue(selectedToken.TokenInfo.TokenAddress, out var hist)
                ? hist.Count
                : 0;
            var lastSampleLocal = hist?.LastOrDefault()?.TimestampUtc.ToLocalTime();

            await RunOnUiAsync(() =>
            {
                ChartCandles.Clear();
                foreach (var candle in candles) ChartCandles.Add(candle);
                RebuildChartGeometry(candles);
                ChartSource     = sourceLabel;
                ChartDiagnostics = diagnostics;
                ChartStatusMessage = statusLabel;
                ChartTelemetry = $"Samples: {rawSampleCount} | Candles: {candles.Count} | Last tick: {lastSampleLocal:HH:mm:ss} | {_onChainScanStatus}";
            });
        }
        catch (Exception ex)
        {
            ClearChart($"Chart build failed: {ex.Message}");
        }
        finally
        {
            await RunOnUiAsync(() => IsChartLoading = false);
        }
    }

    // ── On-chain history helpers ──────────────────────────────────────────────

    private async Task<IReadOnlyList<DexOhlcvPoint>> TryLoadOnChainHistoryAsync(
        DexTokenInfo token, string range)
    {
        if (!EvmHistoryScannerNetworks.TryGet(token.ChainId, out var netConfig))
            return Array.Empty<DexOhlcvPoint>();

        if (string.IsNullOrWhiteSpace(token.PairAddress))
            return Array.Empty<DexOhlcvPoint>();

        try
        {
            var scanner = new EvmPoolSwapHistoryScanner(netConfig.RpcUrl);
            var bucketSize = GetLocalChartRequest(range).BucketSize;

            // Determine max lookback in blocks from the requested time range
            var localReq = GetLocalChartRequest(range);
            var lookbackSeconds = (int)localReq.Lookback.TotalSeconds;
            var maxBlocks = Math.Min(
                lookbackSeconds / Math.Max(1, netConfig.AvgBlockTimeSeconds),
                netConfig.DefaultMaxBlockLookback);

            return await scanner.ScanAsync(
                token.PairAddress,
                isToken0Base: true,
                token0Decimals: 18,
                token1Decimals: 18,
                token1PriceUsd: token.PriceNative > 0 ? token.PriceUsd / token.PriceNative : 1m,
                bucketSize: bucketSize,
                maxBlockLookback: maxBlocks);
        }
        catch
        {
            return Array.Empty<DexOhlcvPoint>();
        }
    }

    private void TriggerBackgroundOnChainScan(DexTokenInfo token)
    {
        var key = $"{token.ChainId}:{token.TokenAddress}";
        if (_onChainScanStarted.Contains(key)) return;
        if (!EvmHistoryScannerNetworks.TryGet(token.ChainId, out var netConfig)) return;
        if (string.IsNullOrWhiteSpace(token.PairAddress)) return;

        _onChainScanStarted.Add(key);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunOnUiAsync(() => _onChainScanStatus = "On-chain scan running...");
                var scanner  = new EvmPoolSwapHistoryScanner(netConfig.RpcUrl);
                var candles  = await scanner.ScanAsync(
                    token.PairAddress,
                    isToken0Base: true,
                    token0Decimals: 18,
                    token1Decimals: 18,
                    token1PriceUsd: token.PriceNative > 0 ? token.PriceUsd / token.PriceNative : 1m,
                    bucketSize: TimeSpan.FromMinutes(5),
                    maxBlockLookback: netConfig.DefaultMaxBlockLookback);

                if (candles.Count > 0)
                {
                    _historyCache.SaveCandles(token.ChainId, token.TokenAddress, candles);
                    MergeOnChainCandlesIntoHistory(token.TokenAddress, candles);
                    await RunOnUiAsync(() =>
                    {
                        _onChainScanStatus = $"On-chain scan complete: {candles.Count} candles loaded.";
                        QueueChartReload();
                    });
                }
                else
                {
                    await RunOnUiAsync(() => _onChainScanStatus = "On-chain scan: no swaps found in range.");
                }
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => _onChainScanStatus = $"On-chain scan failed: {ex.Message}");
            }
        });
    }

    private void MergePersistedHistory(DexTokenInfo token)
    {
        var cached = _historyCache.Load(token.ChainId, token.TokenAddress);
        if (cached.Count == 0) return;

        if (!_priceHistory.TryGetValue(token.TokenAddress, out var hist))
        {
            hist = new List<DexPriceSample>();
            _priceHistory[token.TokenAddress] = hist;
        }

        var existingTimes = hist.Select(s => s.TimestampUtc).ToHashSet();
        foreach (var c in cached.Where(c => !existingTimes.Contains(c.TimestampUtc)))
        {
            hist.Add(new DexPriceSample(c.TimestampUtc, c.PriceUsd));
        }

        hist.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
    }

    private void MergeOnChainCandlesIntoHistory(string tokenAddress, IReadOnlyList<DexOhlcvPoint> candles)
    {
        if (!_priceHistory.TryGetValue(tokenAddress, out var hist))
        {
            hist = new List<DexPriceSample>();
            _priceHistory[tokenAddress] = hist;
        }

        var existingTimes = hist.Select(s => s.TimestampUtc).ToHashSet();
        foreach (var c in candles.Where(c => c.Close > 0 && !existingTimes.Contains(c.Timestamp)))
        {
            hist.Add(new DexPriceSample(c.Timestamp.ToUniversalTime(), c.Close));
        }

        hist.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
    }

    private async Task<IReadOnlyList<DexOhlcvPoint>> TryLoadLiveCandlesAsync(DexTokenInfo token, string range)
    {
        var network = MapGeckoNetwork(token.ChainId);
        if (string.IsNullOrWhiteSpace(network) || string.IsNullOrWhiteSpace(token.PairAddress))
        {
            return Array.Empty<DexOhlcvPoint>();
        }

        var request = GetGeckoChartRequest(range);
        try
        {
            var candles = await _geckoTerminalClient.GetPoolOhlcvAsync(
                network,
                token.PairAddress,
                request.Timeframe,
                request.Aggregate,
                request.Limit);

            return candles
                .Where(static candle => candle.Open > 0 && candle.High > 0 && candle.Low > 0 && candle.Close > 0)
                .OrderBy(candle => candle.Timestamp)
                .ToList();
        }
        catch
        {
            return Array.Empty<DexOhlcvPoint>();
        }
    }

    private IReadOnlyList<DexOhlcvPoint> BuildLocalCandles(DexTokenInfo token, string range, out string diagnostics)
    {
        diagnostics = string.Empty;
        if (!_priceHistory.TryGetValue(token.TokenAddress, out var history) || history.Count == 0)
        {
            diagnostics = "No local samples collected for the selected pair yet.";
            return Array.Empty<DexOhlcvPoint>();
        }

        var request = GetLocalChartRequest(range);
        var nowUtc = DateTime.UtcNow;
        var fromUtc = nowUtc - request.Lookback;
        var samples = history
            .Where(sample => sample.TimestampUtc >= fromUtc)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        if (samples.Count < 2)
        {
            diagnostics = "Local samples are still too sparse to form candles.";
            return Array.Empty<DexOhlcvPoint>();
        }

        diagnostics = $"Chart built from {samples.Count} locally collected price samples.";
        var candles = DexCandleBuilder.Bucketize(samples, fromUtc, nowUtc, request.BucketSize, request.MaxCandles);
        if (candles.Count < 2)
        {
            diagnostics = "Local samples are still too sparse to form candles.";
        }

        return candles;
    }

    private static List<DexPriceSample> BuildSyntheticWindow(
        DexTokenInfo token,
        LocalChartRequest request,
        DateTime nowUtc)
    {
        var windowStart = nowUtc - request.Lookback;
        var anchors = BuildSyntheticAnchors(token, nowUtc);
        if (token.PriceUsd <= 0)
        {
            return anchors;
        }

        var visibleAnchors = anchors
            .Where(sample => sample.TimestampUtc >= windowStart && sample.PriceUsd > 0)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        if (visibleAnchors.Count == 0)
        {
            return visibleAnchors;
        }

        if (visibleAnchors[0].TimestampUtc > windowStart)
        {
            visibleAnchors.Insert(0, new DexPriceSample(windowStart, visibleAnchors[0].PriceUsd));
        }

        if (visibleAnchors[^1].TimestampUtc < nowUtc)
        {
            visibleAnchors.Add(new DexPriceSample(nowUtc, token.PriceUsd));
        }

        return ExpandSyntheticSamples(visibleAnchors, request, windowStart, nowUtc, token);
    }

    private void RebuildChartGeometry(IReadOnlyList<DexOhlcvPoint> candles)
    {
        ChartPoints.Clear();

        var minPrice = candles.Min(candle => candle.Low);
        var maxPrice = candles.Max(candle => candle.High);
        var priceRange = Math.Max((double)(maxPrice - minPrice), 0.00000001d);
        var maxIndex = Math.Max(candles.Count - 1, 1);

        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            var centerX = maxIndex == 0 ? 0 : ChartWidth * index / maxIndex;
            var closeY = ChartHeight - (((double)(candle.Close - minPrice) / priceRange) * ChartHeight);
            ChartPoints.Add(new Point(centerX, closeY));
        }

        ChartHigh = maxPrice;
        ChartLow = minPrice;

        var firstClose = candles.First().Close;
        var lastClose = candles.Last().Close;
        ChartChangePercent = firstClose == 0 ? 0 : ((lastClose - firstClose) / firstClose) * 100;
        ChartStartLabel = candles.First().Timestamp.ToString("dd.MM HH:mm");
        ChartEndLabel = candles.Last().Timestamp.ToString("dd.MM HH:mm");

        this.RaisePropertyChanged(nameof(HasChartData));
        this.RaisePropertyChanged(nameof(ChartPolylinePoints));
    }

    private void ClearChart(string message, string diagnostics = "")
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ClearChart(message, diagnostics));
            return;
        }

        ChartCandles.Clear();
        ChartPoints.Clear();
        ChartHigh = 0;
        ChartLow = 0;
        ChartChangePercent = 0;
        ChartStartLabel = string.Empty;
        ChartEndLabel = string.Empty;
        ChartSource = "Internal Dex sampler";
        ChartDiagnostics = diagnostics;
        ChartStatusMessage = message;
        ChartTelemetry = "No visible chart yet. Waiting for valid local samples.";
        this.RaisePropertyChanged(nameof(HasChartData));
        this.RaisePropertyChanged(nameof(ChartPolylinePoints));
    }

    private static LocalChartRequest GetLocalChartRequest(string range)
    {
        return range switch
        {
            "15M" => new LocalChartRequest(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), 15),
            "4H"  => new LocalChartRequest(TimeSpan.FromMinutes(5), TimeSpan.FromHours(4), 48),
            "12H" => new LocalChartRequest(TimeSpan.FromMinutes(15), TimeSpan.FromHours(12), 48),
            "1W"  => new LocalChartRequest(TimeSpan.FromHours(1), TimeSpan.FromDays(7), 168),
            _ => new LocalChartRequest(TimeSpan.FromMinutes(5), TimeSpan.FromDays(1), 288)
        };
    }

    private static GeckoChartRequest GetGeckoChartRequest(string range)
    {
        // GeckoTerminal only accepts these aggregates: minute → 1/5/15, hour → 1/4/12,
        // day → 1. "minute" with aggregate 30 is rejected with HTTP 400, so longer
        // ranges must use the hour/day timeframes.
        return range switch
        {
            "15M" => new GeckoChartRequest("minute", 1, 15),
            "1H"  => new GeckoChartRequest("minute", 5, 12),
            "4H"  => new GeckoChartRequest("minute", 5, 48),
            "12H" => new GeckoChartRequest("minute", 15, 48),
            "1D"  => new GeckoChartRequest("hour", 1, 24),
            "1W"  => new GeckoChartRequest("hour", 1, 168),
            _ => new GeckoChartRequest("minute", 5, 288)
        };
    }

    private static IReadOnlyList<DexOhlcvPoint> NormalizeDexCandlesForDisplay(
        IReadOnlyList<DexOhlcvPoint> candles,
        DexTokenInfo token,
        string range)
    {
        return candles
            .Where(static candle => candle.Open > 0 && candle.High > 0 && candle.Low > 0 && candle.Close > 0)
            .OrderBy(candle => candle.Timestamp)
            .ToList();
    }

    private static string MapGeckoNetwork(string chainId)
    {
        return chainId.Trim().ToLowerInvariant() switch
        {
            "ethereum" => "eth",
            "eth" => "eth",
            "bsc" => "bsc",
            "base" => "base",
            "solana" => "solana",
            "tron" => "tron",
            "polygon" => "polygon_pos",
            "arbitrum" => "arbitrum",
            "avalanche" => "avax",
            _ => string.Empty
        };
    }

    private static List<DexPriceSample> BuildSyntheticAnchors(DexTokenInfo token, DateTime nowUtc)
    {
        if (token.PriceUsd <= 0)
        {
            return [];
        }

        var currentPrice = token.PriceUsd;
        var price24hAgo = ReverseChange(currentPrice, token.PriceChange24h);
        var price1hAgo = ReverseChange(currentPrice, token.PriceChange1h);
        var price5mAgo = ReverseChange(currentPrice, token.PriceChange5m);
        var price7dAgo = BuildCompoundedPrice(price24hAgo, token.PriceChange24h, 6);

        return
        [
            new DexPriceSample(nowUtc.AddDays(-7), price7dAgo),
            new DexPriceSample(nowUtc.AddDays(-5), Interpolate(price7dAgo, price24hAgo, 0.30m)),
            new DexPriceSample(nowUtc.AddDays(-3), Interpolate(price7dAgo, price24hAgo, 0.65m)),
            new DexPriceSample(nowUtc.AddDays(-2), Interpolate(price7dAgo, price24hAgo, 0.82m)),
            new DexPriceSample(nowUtc.AddDays(-1), price24hAgo),
            new DexPriceSample(nowUtc.AddHours(-12), Interpolate(price24hAgo, currentPrice, 0.35m)),
            new DexPriceSample(nowUtc.AddHours(-6), Interpolate(price24hAgo, currentPrice, 0.55m)),
            new DexPriceSample(nowUtc.AddHours(-3), Interpolate(price24hAgo, currentPrice, 0.72m)),
            new DexPriceSample(nowUtc.AddHours(-1), price1hAgo),
            new DexPriceSample(nowUtc.AddMinutes(-30), Interpolate(price1hAgo, price5mAgo, 0.50m)),
            new DexPriceSample(nowUtc.AddMinutes(-15), Interpolate(price1hAgo, price5mAgo, 0.75m)),
            new DexPriceSample(nowUtc.AddMinutes(-5), price5mAgo),
            new DexPriceSample(nowUtc, currentPrice)
        ];
    }

    private static List<DexPriceSample> ExpandSyntheticSamples(
        IReadOnlyList<DexPriceSample> anchors,
        LocalChartRequest request,
        DateTime windowStart,
        DateTime nowUtc,
        DexTokenInfo token)
    {
        var sampleStep = request.BucketSize switch
        {
            var bucket when bucket <= TimeSpan.FromMinutes(1) => TimeSpan.FromSeconds(20),
            var bucket when bucket <= TimeSpan.FromMinutes(5) => TimeSpan.FromMinutes(1),
            var bucket when bucket <= TimeSpan.FromMinutes(15) => TimeSpan.FromMinutes(3),
            var bucket when bucket <= TimeSpan.FromHours(1) => TimeSpan.FromMinutes(12),
            _ => TimeSpan.FromMinutes(30)
        };

        var expanded = new List<DexPriceSample>();
        var normalizedVolatility = Math.Min(
            0.035d,
            Math.Max(
                0.0025d,
                (double)Math.Max(
                    Math.Abs(token.PriceChange5m),
                    Math.Max(Math.Abs(token.PriceChange1h), Math.Abs(token.PriceChange24h))) / 100d * 0.18d));

        for (var timestamp = windowStart; timestamp <= nowUtc; timestamp = timestamp.Add(sampleStep))
        {
            var basePrice = InterpolateAnchoredPrice(anchors, timestamp);
            var index = expanded.Count + 1;
            var wave = (Math.Sin(index * 0.82d) * 0.72d) + (Math.Sin(index * 0.21d + 1.4d) * 0.28d);
            var adjustedPrice = Math.Max((double)basePrice * (1d + (wave * normalizedVolatility)), 0.00000001d);
            expanded.Add(new DexPriceSample(timestamp, Convert.ToDecimal(adjustedPrice)));
        }

        if (expanded.Count == 0 || expanded[^1].TimestampUtc < nowUtc)
        {
            expanded.Add(new DexPriceSample(nowUtc, token.PriceUsd));
        }
        else
        {
            expanded[^1] = new DexPriceSample(nowUtc, token.PriceUsd);
        }

        return expanded;
    }

    private static decimal InterpolateAnchoredPrice(IReadOnlyList<DexPriceSample> anchors, DateTime timestampUtc)
    {
        if (anchors.Count == 0)
        {
            return 0;
        }

        if (timestampUtc <= anchors[0].TimestampUtc)
        {
            return anchors[0].PriceUsd;
        }

        for (var index = 1; index < anchors.Count; index++)
        {
            var right = anchors[index];
            if (timestampUtc > right.TimestampUtc)
            {
                continue;
            }

            var left = anchors[index - 1];
            var span = right.TimestampUtc - left.TimestampUtc;
            if (span <= TimeSpan.Zero)
            {
                return right.PriceUsd;
            }

            var elapsed = timestampUtc - left.TimestampUtc;
            var factor = (decimal)(elapsed.TotalSeconds / span.TotalSeconds);
            return Interpolate(left.PriceUsd, right.PriceUsd, factor);
        }

        return anchors[^1].PriceUsd;
    }

    private static decimal ReverseChange(decimal currentPrice, decimal percentChange)
    {
        var denominator = 1m + (percentChange / 100m);
        if (currentPrice <= 0 || denominator <= 0.000001m)
        {
            return currentPrice;
        }

        return currentPrice / denominator;
    }

    private static decimal BuildCompoundedPrice(decimal startPrice, decimal dailyPercentChange, int extraDays)
    {
        var price = startPrice;
        for (var index = 0; index < extraDays; index++)
        {
            price = ReverseChange(price, dailyPercentChange);
        }

        return price;
    }

    private static decimal Interpolate(decimal start, decimal end, decimal factor)
    {
        return start + ((end - start) * factor);
    }

    private void OnWalletWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletWorkspaceViewModel.CanUseDexTradingOnSelectedNetwork) or
            nameof(WalletWorkspaceViewModel.SelectedNetwork) or
            nameof(WalletWorkspaceViewModel.ActiveWalletBanner) or
            nameof(WalletWorkspaceViewModel.NativeBalanceLabel) or
            nameof(WalletWorkspaceViewModel.WalletCapabilityText) or
            nameof(WalletWorkspaceViewModel.ActiveDexGateway) or
            nameof(WalletWorkspaceViewModel.GlobalPaperOnlyMode) or
            nameof(WalletWorkspaceViewModel.GlobalQuoteAssetSymbol) or
            nameof(WalletWorkspaceViewModel.EffectiveGlobalQuoteAssetSymbol) or
            nameof(WalletWorkspaceViewModel.GlobalQuoteAssetModeLabel) or
            nameof(WalletWorkspaceViewModel.GlobalQuoteAssetSummary) or
            nameof(WalletWorkspaceViewModel.GlobalPositionSizingPercent) or
            nameof(WalletWorkspaceViewModel.GlobalPositionSizingLabel) or
            nameof(WalletWorkspaceViewModel.GlobalPositionSizingSummary))
        {
            if (e.PropertyName is nameof(WalletWorkspaceViewModel.GlobalPositionSizingPercent))
            {
                _pendingGlobalSizingApply = true;
            }

            RefreshQuoteAssetOptions();
            EnsureValidQuoteAssetSelection();
            this.RaisePropertyChanged(nameof(CanTradeSelectedToken));
            this.RaisePropertyChanged(nameof(CanBuySelectedTokenLive));
            this.RaisePropertyChanged(nameof(CanSellSelectedTokenLive));
            this.RaisePropertyChanged(nameof(DexGuardStatusLabel));
            this.RaisePropertyChanged(nameof(DexGuardStatusBrush));
            this.RaisePropertyChanged(nameof(DexGuardSummary));
            this.RaisePropertyChanged(nameof(DexGuardDetail));
            this.RaisePropertyChanged(nameof(WalletSessionSummary));
            this.RaisePropertyChanged(nameof(WalletBalanceSummary));
            this.RaisePropertyChanged(nameof(WalletTradeStatus));
            this.RaisePropertyChanged(nameof(GlobalQuoteModeLabel));
            this.RaisePropertyChanged(nameof(GlobalQuoteModeBrush));
            this.RaisePropertyChanged(nameof(GlobalQuoteSummary));
            this.RaisePropertyChanged(nameof(GlobalPositionSizingLabel));
            this.RaisePropertyChanged(nameof(GlobalPositionSizingSummary));
            this.RaisePropertyChanged(nameof(BuyAmountLabel));
            this.RaisePropertyChanged(nameof(QuoteAssetSummary));
            this.RaisePropertyChanged(nameof(QuoteAssetModeLabel));
            this.RaisePropertyChanged(nameof(QuoteAssetModeBrush));
            this.RaisePropertyChanged(nameof(IsSelectedPairCompatible));
            this.RaisePropertyChanged(nameof(PairCompatibilityMessage));
            this.RaisePropertyChanged(nameof(QuoteAssetBalanceLabel));
            this.RaisePropertyChanged(nameof(SpendableQuoteBalanceLabel));
            this.RaisePropertyChanged(nameof(HasEnoughQuoteBalanceForBuy));
            this.RaisePropertyChanged(nameof(BuyAvailabilityMessage));
            this.RaisePropertyChanged(nameof(ManualBuyBlockedReason));
            this.RaisePropertyChanged(nameof(ManualSellBlockedReason));
            _ = RefreshQuoteAssetBalanceAsync();
            ApplyGlobalDexSizingIfReady();
        }
    }

    private void RefreshQuoteAssetOptions()
    {
        var desiredOptions = (_walletWorkspace.ActiveDexGateway?.SupportedQuoteAssets ?? DexQuoteAssetCatalog.GetOptions(_walletWorkspace.SelectedNetwork))
            .Select(static asset => asset.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (QuoteAssetOptions.SequenceEqual(desiredOptions, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        QuoteAssetOptions.Clear();
        foreach (var option in desiredOptions)
        {
            QuoteAssetOptions.Add(option);
        }
    }

    private void EnsureValidQuoteAssetSelection()
    {
        var options = QuoteAssetOptions;
        _isSynchronizingQuoteAssetSelection = true;

        try
        {
            if (options.Count == 0)
            {
                SelectedQuoteAssetSymbol = NativeQuoteSymbol;
                return;
            }

            var preferred = NormalizeQuoteAssetSymbol(_walletWorkspace.EffectiveGlobalQuoteAssetSymbol);
            if (options.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            {
                SelectedQuoteAssetSymbol = preferred;
                return;
            }

            if (!options.Contains(SelectedQuoteAssetSymbol, StringComparer.OrdinalIgnoreCase))
            {
                SelectedQuoteAssetSymbol = options.Contains(_walletWorkspace.EffectiveGlobalQuoteAssetSymbol, StringComparer.OrdinalIgnoreCase)
                    ? _walletWorkspace.EffectiveGlobalQuoteAssetSymbol
                    : options.Contains("USDT", StringComparer.OrdinalIgnoreCase)
                    ? "USDT"
                    : options[0];
            }
        }
        finally
        {
            _isSynchronizingQuoteAssetSelection = false;
        }
    }

    private string NormalizeQuoteAssetSymbol(string? symbol)
    {
        var normalized = string.IsNullOrWhiteSpace(symbol) ? NativeQuoteSymbol : symbol.Trim().ToUpperInvariant();
        return string.Equals(normalized, "NATIVE", StringComparison.OrdinalIgnoreCase)
            ? NativeQuoteSymbol
            : normalized;
    }

    private bool IsNativeQuoteMode(string? symbol) =>
        string.IsNullOrWhiteSpace(symbol) ||
        string.Equals(symbol, "NATIVE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(symbol, NativeQuoteSymbol, StringComparison.OrdinalIgnoreCase);

    private void ApplyGlobalDexSizingIfReady()
    {
        if (!_pendingGlobalSizingApply || IsNativeQuoteMode(SelectedQuoteAssetSymbol) || AvailableSpendableQuoteBalance <= 0)
        {
            return;
        }

        BuyAmountBnb = RoundBuyAmount(AvailableSpendableQuoteBalance * (_walletWorkspace.GlobalPositionSizingPercent / 100m));
        _pendingGlobalSizingApply = false;
    }

    private void QueueBuyQuote()
    {
        _quoteDebounceTimer.Stop();
        _quoteDebounceTimer.Start();
    }

    private async Task RefreshBuyQuoteAsync()
    {
        var gateway = _walletWorkspace.ActiveDexGateway;
        var selectedToken = await RunOnUiAsync(() => SelectedToken);
        var buyAmount = await RunOnUiAsync(() => BuyAmountBnb);
        var quoteSymbol = await RunOnUiAsync(() => SelectedQuoteAssetSymbol);

        if (gateway is null || selectedToken is null || buyAmount <= 0m)
        {
            await RunOnUiAsync(() =>
            {
                BuyQuoteLabel = string.Empty;
                BuyQuoteBrush = "#8FA3B8";
            });
            return;
        }

        try
        {
            if (IsNativeQuoteMode(quoteSymbol))
            {
                var tokensOut = await gateway.GetTokenPriceInNativeAsync(
                    selectedToken.TokenAddress, buyAmount, selectedToken.TokenInfo.DexId);

                if (tokensOut > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        BuyQuoteLabel = $"≈ {tokensOut:0.########} {selectedToken.TokenInfo.Symbol}";
                        BuyQuoteBrush = "#21E6C1";
                    });
                }
                else
                {
                    await RunOnUiAsync(() =>
                    {
                        BuyQuoteLabel = "Router quote unavailable for this pair.";
                        BuyQuoteBrush = "#8FA3B8";
                    });
                }
            }
            else
            {
                var priceUsd = selectedToken.TokenInfo.PriceUsd;
                if (priceUsd > 0)
                {
                    var tokensEst = buyAmount / priceUsd;
                    await RunOnUiAsync(() =>
                    {
                        BuyQuoteLabel = $"≈ {tokensEst:0.########} {selectedToken.TokenInfo.Symbol} (price est.)";
                        BuyQuoteBrush = "#F4B860";
                    });
                }
                else
                {
                    await RunOnUiAsync(() =>
                    {
                        BuyQuoteLabel = "Price estimate unavailable.";
                        BuyQuoteBrush = "#8FA3B8";
                    });
                }
            }
        }
        catch
        {
            await RunOnUiAsync(() =>
            {
                BuyQuoteLabel = "Could not fetch quote.";
                BuyQuoteBrush = "#FF8A65";
            });
        }
    }

    private async Task RefreshTokenBalanceAsync()
    {
        var gateway = _walletWorkspace.ActiveDexGateway;
        var selectedToken = await RunOnUiAsync(() => SelectedToken);

        if (gateway is null || selectedToken is null || !_walletWorkspace.CanUseDexTradingOnSelectedNetwork)
        {
            await RunOnUiAsync(() =>
            {
                IsTokenBalanceLoading = false;
                TokenBalance = 0m;
                TokenBalanceLabel = string.Empty;
            });
            return;
        }

        try
        {
            await RunOnUiAsync(() => IsTokenBalanceLoading = true);
            var balance = await gateway.GetTokenBalanceAsync(selectedToken.TokenAddress);
            await RunOnUiAsync(() =>
            {
                TokenBalance = balance;
                TokenBalanceLabel = $"{balance:0.########} {selectedToken.TokenInfo.Symbol}";
                IsTokenBalanceLoading = false;
            });
        }
        catch
        {
            await RunOnUiAsync(() =>
            {
                TokenBalance = 0m;
                TokenBalanceLabel = "Balance unavailable";
                IsTokenBalanceLoading = false;
            });
        }
    }

    private void ApplySellBalancePreset(string? preset)
    {
        var ratio = preset?.Trim() switch
        {
            "25"  => 0.25m,
            "50"  => 0.50m,
            "75"  => 0.75m,
            "100" => 1.00m,
            _     => 0m
        };

        if (ratio <= 0m)
        {
            return;
        }

        if (TokenBalance <= 0m)
        {
            StatusMessage = "No token balance loaded. Click ↻ to refresh token balance.";
            return;
        }

        SellAmountTokens = Math.Round(TokenBalance * ratio, 8, MidpointRounding.AwayFromZero);
        StatusMessage = $"Sell amount set to {preset}% of token balance: {SellAmountTokens:0.########}.";
    }

    private sealed record LocalChartRequest(TimeSpan BucketSize, TimeSpan Lookback, int MaxCandles);
    private sealed record GeckoChartRequest(string Timeframe, int Aggregate, int Limit);
}

public sealed class DexTradeRecordViewModel : ReactiveObject
{
    public string Side      { get; }
    public string Symbol    { get; }
    public decimal Amount   { get; }
    public decimal Price    { get; }
    public string TxHash    { get; }
    public bool   Success   { get; }
    public DateTime TimeUtc { get; }

    public string SideLabel    => Side;
    public string SideBrush    => Side == "BUY" ? "#3DDC84" : "#FF6B6B";
    public string AmountLabel  => $"{Amount:0.########}";
    public string PriceLabel   => Price > 0 ? $"$ {Price:N6}" : "--";
    public string TxHashShort  => TxHash.Length > 10 ? TxHash[..10] + "..." : (TxHash.Length > 0 ? TxHash : "FAILED");
    public string TimeLabel    => TimeUtc.ToLocalTime().ToString("HH:mm:ss");
    public string StatusLabel  => Success ? "FILLED" : "FAILED";
    public string StatusBrush  => Success ? "#21E6C1" : "#FF6B6B";

    public DexTradeRecordViewModel(string side, string symbol, decimal amount, decimal price, string txHash, bool success, DateTime timeUtc)
    {
        Side = side; Symbol = symbol; Amount = amount; Price = price;
        TxHash = txHash; Success = success; TimeUtc = timeUtc;
    }
}
