using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public partial class SniperViewModel : ReactiveObject, IDisposable
{
    private static readonly JsonSerializerOptions StorageJsonOptions = new() { WriteIndented = true };
    private static readonly IReadOnlyDictionary<string, SniperChainProfile> ChainProfiles =
        new Dictionary<string, SniperChainProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["bsc"]       = new("BSC",          true,  ["BNB",  "WBNB"]),
            ["ethereum"]  = new("Ethereum",      true,  ["ETH",  "WETH"]),
            ["base"]      = new("Base",          true,  ["ETH",  "WETH"]),
            ["solana"]    = new("Solana",        true,  ["SOL",  "WSOL"]),
            ["tron"]      = new("Tron",          true,  ["TRX",  "WTRX"]),
            ["polygon"]   = new("Polygon",       true,  ["POL",  "WMATIC", "MATIC"]),
            ["arbitrum"]  = new("Arbitrum",      true,  ["ETH",  "WETH"]),
            ["cex-spot"]     = new("Binance Spot",          false, ["USDT"]),
            ["cex-futures"]  = new("Binance USD-M Futures", false, ["USDT"])
        };
    private static readonly HashSet<string> TrustedDexIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "pancakeswap",
        "uniswap",
        "sushiswap",
        "aerodrome",
        "raydium",
        "pumpswap",
        "jupiter",
        "sunswap",
        "quickswap",
        "quickswap v3",
        "camelot",
        "orca",
        "meteora",
        "lifinity"
    };
    private static readonly IReadOnlyDictionary<string, int> DexQualityScores =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["uniswap"]      = 92,
            ["jupiter"]      = 91,
            ["orca"]         = 88,
            ["pancakeswap"]  = 88,
            ["raydium"]      = 86,
            ["quickswap v3"] = 85,
            ["meteora"]      = 84,
            ["aerodrome"]    = 84,
            ["quickswap"]    = 82,
            ["sunswap"]      = 78,
            ["camelot"]      = 78,
            ["sushiswap"]    = 74,
            ["lifinity"]     = 70,
            ["pumpswap"]     = 56
        };

    private readonly WalletWorkspaceViewModel _walletWorkspace;
    private readonly BinanceGateway? _spotGateway;
    private readonly CryptoAITerminal.Core.Interfaces.IExchangeGateway? _futuresGateway;
    private readonly DexScreenerClient _dexClient;
    private readonly DispatcherTimer _scanTimer;
    private readonly DispatcherTimer _positionPulseTimer;
    private readonly string _settingsFilePath;
    private readonly string _paperHistoryFilePath;
    private readonly string _liveHistoryFilePath;
    private readonly string _auditTrailFilePath;
    private readonly string _liveOpenPositionsFilePath;
    private readonly SniperRiskPolicyService _riskPolicyService;
    private readonly SniperTradeHistoryService _tradeHistoryService;
    private readonly SniperLiveExecutionService _liveExecutionService;
    private readonly SniperSignalStreamService _signalStreamService;
    private readonly SniperExecutionAuditService _auditService;
    private readonly SniperOpenPositionStateService _openPositionStateService;
    private readonly SniperLiveReadinessService _liveReadinessService;
    private readonly IDisposable? _spotMarketSubscription;
    private readonly IDisposable? _futuresMarketSubscription;
    private readonly List<IDisposable> _commandErrorSubscriptions = [];
    private readonly object _marketDataCacheLock = new();
    private readonly SemaphoreSlim _cexSnapshotLock = new(1, 1);
    private readonly IReadOnlyList<string> _cexSymbols;
    private readonly Dictionary<string, MarketData> _spotMarketBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarketData> _futuresMarketBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DexTokenInfo> _cachedCexSnapshot = [];
    private bool _cachedCexSnapshotIsFutures;
    private DateTime _cachedCexSnapshotUtc = DateTime.MinValue;
    private readonly HashSet<string> _executedBuys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paperExecutedBuys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _firstSeenByTokenKey = new(StringComparer.OrdinalIgnoreCase);

    private bool _isArmed;
    private bool _autoBuyEnabled;
    private bool _isScanning;
    private bool _isHelpOpen;
    private decimal _buyAmountBnb = 0.01m;
    private decimal _minLiquidityUsd = 25000m;
    private decimal _maxLiquidityUsd = 500000m;
    private decimal _minVolume24hUsd = 100000m;
    private decimal _minMomentum5m = 3m;
    private decimal _maxMarketCapUsd = 5000000m;
    private int _maxRiskScore = 45;
    private decimal _minPairAgeMinutes;
    private decimal _maxPairAgeMinutes = 240m;
    private decimal _launchMaxPairAgeMinutes = 12m;
    private decimal _warmPairMinAgeMinutes = 20m;
    private string _selectedStrategyMode = "Mixed";
    private decimal _maxVolumeToLiquidityRatio = 18m;
    private decimal _maxMarketCapToLiquidityRatio = 30m;
    private bool _enableExecutionGuard = true;
    private bool _blockSuspectedHoneypots = true;
    private bool _paperTradingEnabled = true;
    // CEX live execution для снайпера — заблокирован по умолчанию.
    // Оба флага должны быть включены чтобы открыть live путь:
    //   CexLiveExecutionEnabled  — мастер-свитч из Settings.
    //   CexLiveAcknowledged      — пользователь подтвердил риск.
    private bool _cexLiveExecutionEnabled;
    private bool _cexLiveAcknowledged;
    private bool _autoTakeProfitEnabled = true;
    private decimal _takeProfitPercent = 20m;
    private bool _autoStopLossEnabled = true;
    private decimal _stopLossPercent = 12m;
    private bool _autoTrailingStopEnabled = true;
    private decimal _trailingStopPercent = 8m;
    private bool _partialTakeProfitEnabled = true;
    private decimal _partialTakeProfitTriggerPercent = 12m;
    private decimal _partialTakeProfitSellPercent = 35m;
    private bool _breakEvenEnabled = true;
    private decimal _breakEvenTriggerPercent = 10m;
    private decimal _maxSimulatedBuyTaxPercent = 12m;
    private decimal _maxSimulatedSellTaxPercent = 15m;
    private int _cooldownSeconds = 90;
    private int _maxSimultaneousPositions = 2;
    private int _maxBuysPerSession = 3;
    private decimal _maxDailyLiveLossNative = 0.05m;
    private decimal _maxExposurePerChainNative = 0.02m;
    private decimal _maxExposurePerWalletNative = 0.03m;
    private int _maxConsecutiveLiveLosses = 3;
    private decimal _hardCapTotalLiveExposureNative = 0.04m;
    private decimal _tinyDryRunCapNative = 0.02m;
    private int _sessionBuyCount;
    private bool _requireBnbQuote = true;
    private bool _preferStableQuote = false;
    private decimal _stableQuoteBalance;
    private bool _isStableQuoteBalanceLoading;
    private bool _pendingGlobalSizingApply = true;
    private string _enabledChainsText = "bsc,ethereum,base,solana,tron";
    private string _whitelistText = string.Empty;
    private string _watchlistText = string.Empty;
    private string _blacklistText = "inu,elon,ai16z,test,wrapped";
    private string _selectedPresetName = "Balanced";
    private string _statusMessage = "Sniper is idle. Arm the scanner to start tracking fresh multi-chain pairs.";
    private string _latestRiskVerdictTitle = "No pair evaluated yet";
    private string _latestRiskNarrative = "Arm the sniper and let the feed inspect fresh pairs.";
    private string _latestRiskFlags = "No heuristic flags yet";
    private string _latestRiskAccentHex = "#8FA3B8";
    private string _latestRiskScoreLabel = "Risk --/100";
    private string _latestStructureVerdict = "Structure guard idle";
    private string _latestStructureNarrative = "No pair evaluated for structural integrity yet.";
    private string _latestStructureAccentHex = "#8FA3B8";
    private string _latestExecutionVerdict = "Execution guard idle";
    private string _latestExecutionReason = "No pair evaluated for execution yet.";
    private string _latestExecutionAccentHex = "#8FA3B8";
    private DateTime _lastScanLocal = DateTime.MinValue;
    private string _lastSignalMessageKind = "Idle";
    private string _lastSignalNarrative = "No signal message has been processed yet.";
    private string _lastSnapshotSource = "No snapshot source yet.";
    private DateTime _lastFeedMessageLocal = DateTime.MinValue;
    private DateTime _lastOpenPositionPulseLocal = DateTime.MinValue;
    private int _lastSnapshotTokenCount;
    private int _lastDiscoveredTokenCount;
    private int _lastAcceptedTokenCount;
    private int _lastRejectedTokenCount;
    private int _lastObservedUpdateCount;
    private int _lastTrackedPositionUpdateCount;
    private int _lastOpenPositionPulseCount;
    private bool _isRefreshingTrackedPositions;
    private string _openPositionTrackerStatus = "No open-position market tracker activity yet.";
    private string _selectedScanVenue = "DEX";
    private string _selectedTradingProfile = "Balanced";
    private string _selectedScalpPreset = "Standard";
    private int _futuresLeverage = 5;
    private string _selectedFuturesBias = "Long & Short";
    private bool _futuresRegionFallbackToSpotActive;
    private DateTime _lastVenueAutoScanUtc = DateTime.MinValue;
    private DateTime? _lastBuyUtc;
    private CancellationTokenSource? _signalStreamCts;
    private Task? _signalStreamTask;
    private readonly TokenSecurityService _tokenSecurityService = new();
    private readonly TokenSecurityAiService _tokenAiService = new();
    private bool _enableAiTokenVerdict = true;
    private bool _enableExternalSecurityScan;
    private decimal _slippagePercent = 3m;

    // ── Manual Snipe ──────────────────────────────────────────────────────────
    private string _manualSnipeAddress = string.Empty;
    private string _manualSnipeInfoLabel = string.Empty;
    private string _manualSnipeInfoBrush = "#8FA3B8";
    private string _manualSnipeQuoteLabel = string.Empty;
    private string _manualSnipeQuoteBrush = "#8FA3B8";
    private decimal _manualSnipeTokenBalance;
    private string _manualSnipeTokenBalanceLabel = string.Empty;
    private decimal _manualSnipeSellAmount;
    private bool _isManualSnipeLoading;
    private string _manualSnipeStatusMessage = string.Empty;
    private DexTokenInfo? _resolvedManualSnipeToken;
    private readonly DispatcherTimer _manualSnipeQuoteTimer;

    public bool EnableExternalSecurityScan
    {
        get => _enableExternalSecurityScan;
        set => this.RaiseAndSetIfChanged(ref _enableExternalSecurityScan, value);
    }

    /// <summary>
    /// When enabled, each accepted candidate gets an AI risk verdict (Claude if a
    /// key is configured, otherwise an offline heuristic). Visible on the card.
    /// </summary>
    public bool EnableAiTokenVerdict
    {
        get => _enableAiTokenVerdict;
        set => this.RaiseAndSetIfChanged(ref _enableAiTokenVerdict, value);
    }

    public bool AiVerdictUsesLiveModel => _tokenAiService.UsesLiveModel;

    /// <summary>Wire the Claude API key/model (e.g. shared from the AI Bot settings).</summary>
    public void ConfigureAiVerdict(string? apiKey, string? model = null)
    {
        if (apiKey is not null) _tokenAiService.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) _tokenAiService.Model = model;
        this.RaisePropertyChanged(nameof(AiVerdictUsesLiveModel));
    }

    public decimal SlippagePercent
    {
        get => _slippagePercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _slippagePercent, Math.Clamp(value, 0.1m, 50m));
            this.RaisePropertyChanged(nameof(SlippageSummary));
            PersistSettings();
        }
    }
    public string SlippageSummary => $"Slippage: {_slippagePercent:0.##}% — all buys and sells will use this tolerance.";

    // ── Manual Snipe properties ───────────────────────────────────────────────

    public string ManualSnipeAddress
    {
        get => _manualSnipeAddress;
        set
        {
            this.RaiseAndSetIfChanged(ref _manualSnipeAddress, value?.Trim() ?? string.Empty);
            _resolvedManualSnipeToken = null;
            ManualSnipeInfoLabel = string.Empty;
            ManualSnipeQuoteLabel = string.Empty;
            ManualSnipeTokenBalanceLabel = string.Empty;
            ManualSnipeTokenBalance = 0m;
            QueueManualSnipeLookup();
        }
    }
    public string ManualSnipeInfoLabel
    {
        get => _manualSnipeInfoLabel;
        private set => this.RaiseAndSetIfChanged(ref _manualSnipeInfoLabel, value);
    }
    public string ManualSnipeInfoBrush
    {
        get => _manualSnipeInfoBrush;
        private set => this.RaiseAndSetIfChanged(ref _manualSnipeInfoBrush, value);
    }
    public string ManualSnipeQuoteLabel
    {
        get => _manualSnipeQuoteLabel;
        private set => this.RaiseAndSetIfChanged(ref _manualSnipeQuoteLabel, value);
    }
    public string ManualSnipeQuoteBrush
    {
        get => _manualSnipeQuoteBrush;
        private set => this.RaiseAndSetIfChanged(ref _manualSnipeQuoteBrush, value);
    }
    public decimal ManualSnipeTokenBalance
    {
        get => _manualSnipeTokenBalance;
        private set => this.RaiseAndSetIfChanged(ref _manualSnipeTokenBalance, value);
    }
    public string ManualSnipeTokenBalanceLabel
    {
        get => _manualSnipeTokenBalanceLabel;
        private set => this.RaiseAndSetIfChanged(ref _manualSnipeTokenBalanceLabel, value);
    }
    public decimal ManualSnipeSellAmount
    {
        get => _manualSnipeSellAmount;
        set => this.RaiseAndSetIfChanged(ref _manualSnipeSellAmount, Math.Max(0m, value));
    }
    public bool IsManualSnipeLoading
    {
        get => _isManualSnipeLoading;
        private set => this.RaiseAndSetIfChanged(ref _isManualSnipeLoading, value);
    }
    public string ManualSnipeStatusMessage
    {
        get => _manualSnipeStatusMessage;
        private set => this.RaiseAndSetIfChanged(ref _manualSnipeStatusMessage, value);
    }

    public ReactiveCommand<Unit, Unit> ManualSnipeBuyCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ManualSnipeSellCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ManualSnipeProbeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ManualSnipeRefreshBalanceCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> ApplyManualSnipeSellPresetCommand { get; private set; } = null!;

    public SniperViewModel(
        WalletWorkspaceViewModel walletWorkspace,
        BinanceGateway? spotGateway = null,
        CryptoAITerminal.Core.Interfaces.IExchangeGateway? futuresGateway = null,
        IEnumerable<string>? cexSymbols = null)
    {
        _walletWorkspace = walletWorkspace;
        _spotGateway = spotGateway;
        _futuresGateway = futuresGateway;
        _dexClient = new DexScreenerClient();
        _riskPolicyService = new SniperRiskPolicyService();
        _tradeHistoryService = new SniperTradeHistoryService();
        _liveExecutionService = new SniperLiveExecutionService();
        _signalStreamService = new SniperSignalStreamService(_dexClient);
        _auditService = new SniperExecutionAuditService();
        _openPositionStateService = new SniperOpenPositionStateService();
        _liveReadinessService = new SniperLiveReadinessService();
        _cexSymbols = (cexSymbols ?? spotGateway?.Symbols ?? ["BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT"])
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(static symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _walletWorkspace.PropertyChanged += OnWalletWorkspacePropertyChanged;
        _settingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal",
            "sniper-settings.json");
        _paperHistoryFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal",
            "sniper-paper-history.json");
        _liveHistoryFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal",
            "sniper-live-history.json");
        _auditTrailFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal",
            "sniper-live-audit.json");
        _liveOpenPositionsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal",
            "sniper-live-open-positions.json");

        FreshPairs = new ObservableCollection<SniperCandidateViewModel>();
        AcceptedPairs = new ObservableCollection<SniperCandidateViewModel>();
        OpenPositions = new ObservableCollection<SniperCandidateViewModel>();
        PaperPositions = new ObservableCollection<SniperCandidateViewModel>();
        PaperTradeHistory = new ObservableCollection<PaperTradeRecordViewModel>();
        LiveTradeHistory = new ObservableCollection<PaperTradeRecordViewModel>();
        ExecutionAuditTrail = new ObservableCollection<SniperExecutionAuditRecordViewModel>();
        LiveReadyStatuses = new ObservableCollection<SniperLiveReadyStatusViewModel>();
        PerformanceCurvePoints = new ObservableCollection<Point>();
        DecisionLog = new ObservableCollection<SniperLogEntryViewModel>();

        ArmCommand = ReactiveCommand.CreateFromTask(ArmAsync, outputScheduler: App.UiScheduler);
        StopCommand = ReactiveCommand.Create(Stop, outputScheduler: App.UiScheduler);
        ScanNowCommand = ReactiveCommand.CreateFromTask(ScanAsync, outputScheduler: App.UiScheduler);
        BuyCandidateCommand = ReactiveCommand.CreateFromTask<SniperCandidateViewModel>(BuyCandidateAsync, outputScheduler: App.UiScheduler);
        ClearPositionCommand = ReactiveCommand.Create<SniperCandidateViewModel>(ClearPosition, outputScheduler: App.UiScheduler);
        EmergencyClosePositionCommand = ReactiveCommand.CreateFromTask<SniperCandidateViewModel>(EmergencyClosePositionAsync, outputScheduler: App.UiScheduler);
        ResetSafetyCommand = ReactiveCommand.Create(ResetSafetyState, outputScheduler: App.UiScheduler);
        ApplySafePresetCommand = ReactiveCommand.Create(ApplySafePreset, outputScheduler: App.UiScheduler);
        ApplyBalancedPresetCommand = ReactiveCommand.Create(ApplyBalancedPreset, outputScheduler: App.UiScheduler);
        ApplyAggressivePresetCommand = ReactiveCommand.Create(ApplyAggressivePreset, outputScheduler: App.UiScheduler);
        ApplyScalpPresetCommand = ReactiveCommand.Create<string>(ApplyScalpPreset, outputScheduler: App.UiScheduler);
        ApplyBuyBalancePresetCommand = ReactiveCommand.Create<string>(ApplyBuyBalancePreset, outputScheduler: App.UiScheduler);
        UseMaxBuyAmountCommand = ReactiveCommand.Create(UseMaxBuyAmount, outputScheduler: App.UiScheduler);
        ApplyAllChainsCommand = ReactiveCommand.Create(ApplyAllChainsPreset, outputScheduler: App.UiScheduler);
        ApplyEvmChainsCommand = ReactiveCommand.Create(ApplyEvmChainsPreset, outputScheduler: App.UiScheduler);
        OpenHelpCommand = ReactiveCommand.Create(() =>
        {
            IsHelpOpen = true;
        }, outputScheduler: App.UiScheduler);
        CloseHelpCommand = ReactiveCommand.Create(() =>
        {
            IsHelpOpen = false;
        }, outputScheduler: App.UiScheduler);

        ManualSnipeBuyCommand          = ReactiveCommand.CreateFromTask(ManualSnipeBuyAsync, outputScheduler: App.UiScheduler);
        ManualSnipeSellCommand         = ReactiveCommand.CreateFromTask(ManualSnipeSellAsync, outputScheduler: App.UiScheduler);
        ManualSnipeProbeCommand        = ReactiveCommand.CreateFromTask(ManualSnipeProbeAsync, outputScheduler: App.UiScheduler);
        ManualSnipeRefreshBalanceCommand = ReactiveCommand.CreateFromTask(ManualSnipeRefreshBalanceAsync, outputScheduler: App.UiScheduler);
        ApplyManualSnipeSellPresetCommand = ReactiveCommand.Create<string>(ApplyManualSnipeSellPreset, outputScheduler: App.UiScheduler);

        _manualSnipeQuoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _manualSnipeQuoteTimer.Tick += async (_, _) =>
        {
            _manualSnipeQuoteTimer.Stop();
            await ManualSnipeLookupAndQuoteAsync();
        };

        _scanTimer = new DispatcherTimer
        {
            Interval = SniperSignalStreamService.GetUiHeartbeatInterval()
        };
        _scanTimer.Tick += (_, _) =>
        {
            RaiseSafetyProperties();
            if (IsArmed &&
                IsCexVenue &&
                DateTime.UtcNow - _lastVenueAutoScanUtc >= GetVenueAutoScanInterval())
            {
                _lastVenueAutoScanUtc = DateTime.UtcNow;
                RunLoggedAsync(ScanAsync, "Sniper auto scan");
            }
        };
        _positionPulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _positionPulseTimer.Tick += (_, _) => RunLoggedAsync(RefreshOpenPositionMarketDataAsync, "Sniper position pulse");
        _spotMarketSubscription = _spotGateway?.MarketDataStream.Subscribe(OnSpotMarketDataReceived);
        _futuresMarketSubscription = _futuresGateway?.MarketDataStream.Subscribe(OnFuturesMarketDataReceived);
        ObserveCommandErrors();

        LoadSettings();
        LoadPaperTradeHistory();
        LoadLiveTradeHistory();
        LoadExecutionAuditTrail();
        RestoreOpenLivePositions();
        ApplyEmergencyRiskStopIfNeeded(false);
        RebuildLiveReadinessStatuses();
        UpdatePositionPulseTimerState();
        ApplyWalletGlobalQuoteModeToSniper();
        ApplyGlobalSniperSizingIfReady();
        RunLoggedAsync(RefreshStableQuoteBalanceAsync, "Stable quote balance refresh");
    }

    public ObservableCollection<SniperCandidateViewModel> FreshPairs { get; }
    public ObservableCollection<SniperCandidateViewModel> AcceptedPairs { get; }
    public ObservableCollection<SniperCandidateViewModel> OpenPositions { get; }
    public ObservableCollection<SniperCandidateViewModel> PaperPositions { get; }
    public ObservableCollection<PaperTradeRecordViewModel> PaperTradeHistory { get; }
    public ObservableCollection<PaperTradeRecordViewModel> LiveTradeHistory { get; }
    public ObservableCollection<SniperExecutionAuditRecordViewModel> ExecutionAuditTrail { get; }
    public ObservableCollection<SniperLiveReadyStatusViewModel> LiveReadyStatuses { get; }
    public ObservableCollection<Point> PerformanceCurvePoints { get; }
    public ObservableCollection<SniperLogEntryViewModel> DecisionLog { get; }

    public ReactiveCommand<Unit, Unit> ArmCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> ScanNowCommand { get; }
    public ReactiveCommand<SniperCandidateViewModel, Unit> BuyCandidateCommand { get; }
    public ReactiveCommand<SniperCandidateViewModel, Unit> ClearPositionCommand { get; }
    public ReactiveCommand<SniperCandidateViewModel, Unit> EmergencyClosePositionCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetSafetyCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplySafePresetCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyBalancedPresetCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyAggressivePresetCommand { get; }
    public ReactiveCommand<string, Unit> ApplyScalpPresetCommand { get; }
    public ReactiveCommand<string, Unit> ApplyBuyBalancePresetCommand { get; }
    public ReactiveCommand<Unit, Unit> UseMaxBuyAmountCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyAllChainsCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyEvmChainsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenHelpCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseHelpCommand { get; }

    private void RunLoggedAsync(Func<Task> operation, string label)
    {
        _ = ExecuteAsync();

        async Task ExecuteAsync()
        {
            try
            {
                await operation();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PushLog($"{label} failed: {ex.Message}", false);
            }
        }
    }

    private void ObserveCommandErrors()
    {
        SubscribeCommandErrors(ArmCommand, "Arm sniper");
        SubscribeCommandErrors(StopCommand, "Stop sniper");
        SubscribeCommandErrors(ScanNowCommand, "Manual sniper scan");
        SubscribeCommandErrors(BuyCandidateCommand, "Sniper buy");
        SubscribeCommandErrors(ClearPositionCommand, "Clear sniper position");
        SubscribeCommandErrors(EmergencyClosePositionCommand, "Emergency close");
        SubscribeCommandErrors(ResetSafetyCommand, "Reset sniper safety");
        SubscribeCommandErrors(ApplySafePresetCommand, "Apply safe preset");
        SubscribeCommandErrors(ApplyBalancedPresetCommand, "Apply balanced preset");
        SubscribeCommandErrors(ApplyAggressivePresetCommand, "Apply aggressive preset");
        SubscribeCommandErrors(ApplyScalpPresetCommand, "Apply scalp preset");
        SubscribeCommandErrors(ApplyBuyBalancePresetCommand, "Apply buy balance preset");
        SubscribeCommandErrors(UseMaxBuyAmountCommand, "Use max buy amount");
        SubscribeCommandErrors(ApplyAllChainsCommand, "Apply all chains");
        SubscribeCommandErrors(ApplyEvmChainsCommand, "Apply EVM chains");
        SubscribeCommandErrors(OpenHelpCommand, "Open sniper help");
        SubscribeCommandErrors(CloseHelpCommand, "Close sniper help");
    }

    private void SubscribeCommandErrors<TParam, TResult>(ReactiveCommand<TParam, TResult> command, string label)
    {
        _commandErrorSubscriptions.Add(command.ThrownExceptions.Subscribe(ex => PushLog($"{label} failed: {ex.Message}", false)));
    }

    public bool IsArmed
    {
        get => _isArmed;
        set => this.RaiseAndSetIfChanged(ref _isArmed, value);
    }

    public bool AutoBuyEnabled
    {
        get => _autoBuyEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoBuyEnabled, value);
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set => this.RaiseAndSetIfChanged(ref _isScanning, value);
    }

    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        set => this.RaiseAndSetIfChanged(ref _isHelpOpen, value);
    }

    public IReadOnlyList<string> SniperVenueOptions { get; } = ["DEX", "CEX Spot", "CEX Futures"];
    public IReadOnlyList<string> TradingProfileOptions { get; } = ["Balanced", "Scalp"];
    public IReadOnlyList<string> FuturesBiasOptions { get; } = ["Long Only", "Short Only", "Long & Short"];

    public string SelectedScanVenue
    {
        get => _selectedScanVenue;
        set
        {
            var normalized = NormalizeVenue(value);
            if (string.Equals(_selectedScanVenue, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedScanVenue, normalized);
            _lastVenueAutoScanUtc = DateTime.MinValue;
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public string SelectedTradingProfile
    {
        get => _selectedTradingProfile;
        set
        {
            var normalized = NormalizeTradingProfile(value);
            if (string.Equals(_selectedTradingProfile, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedTradingProfile, normalized);
            if (IsScalpProfile)
            {
                ApplyScalpPreset(_selectedScalpPreset);
            }

            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public string SelectedScalpPreset
    {
        get => _selectedScalpPreset;
        set
        {
            var normalized = NormalizeScalpPreset(value);
            if (string.Equals(_selectedScalpPreset, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedScalpPreset, normalized);
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public int FuturesLeverage
    {
        get => _futuresLeverage;
        set
        {
            this.RaiseAndSetIfChanged(ref _futuresLeverage, Math.Clamp(value, 1, 50));
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public string SelectedFuturesBias
    {
        get => _selectedFuturesBias;
        set
        {
            var normalized = NormalizeFuturesBias(value);
            if (string.Equals(_selectedFuturesBias, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedFuturesBias, normalized);
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public bool IsDexVenue => string.Equals(SelectedScanVenue, "DEX", StringComparison.Ordinal);
    public bool IsCexSpotVenue => string.Equals(SelectedScanVenue, "CEX Spot", StringComparison.Ordinal);
    public bool IsFuturesVenue => string.Equals(SelectedScanVenue, "CEX Futures", StringComparison.Ordinal);
    public bool IsCexVenue => IsCexSpotVenue || IsFuturesVenue;
    public bool IsScalpProfile => string.Equals(SelectedTradingProfile, "Scalp", StringComparison.Ordinal);

    public decimal BuyAmountBnb
    {
        get => _buyAmountBnb;
        set
        {
            this.RaiseAndSetIfChanged(ref _buyAmountBnb, value);
            this.RaisePropertyChanged(nameof(HasEnoughStableQuoteBalanceForConfiguredBuy));
            this.RaisePropertyChanged(nameof(StableQuoteAvailabilityMessage));
            this.RaisePropertyChanged(nameof(StableQuoteAvailabilityBrush));
            PersistSettings();
        }
    }

    public decimal MinLiquidityUsd
    {
        get => _minLiquidityUsd;
        set
        {
            this.RaiseAndSetIfChanged(ref _minLiquidityUsd, value);
            PersistSettings();
        }
    }

    public decimal MaxLiquidityUsd
    {
        get => _maxLiquidityUsd;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxLiquidityUsd, value);
            PersistSettings();
        }
    }

    public decimal MinVolume24hUsd
    {
        get => _minVolume24hUsd;
        set
        {
            this.RaiseAndSetIfChanged(ref _minVolume24hUsd, value);
            PersistSettings();
        }
    }

    public decimal MinMomentum5m
    {
        get => _minMomentum5m;
        set
        {
            this.RaiseAndSetIfChanged(ref _minMomentum5m, value);
            PersistSettings();
        }
    }

    public decimal MaxMarketCapUsd
    {
        get => _maxMarketCapUsd;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxMarketCapUsd, value);
            PersistSettings();
        }
    }

    public int MaxRiskScore
    {
        get => _maxRiskScore;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxRiskScore, Math.Max(0, value));
            PersistSettings();
        }
    }

    public decimal MinPairAgeMinutes
    {
        get => _minPairAgeMinutes;
        set
        {
            this.RaiseAndSetIfChanged(ref _minPairAgeMinutes, Math.Max(0m, value));
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public decimal MaxPairAgeMinutes
    {
        get => _maxPairAgeMinutes;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxPairAgeMinutes, Math.Max(0m, value));
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public decimal LaunchMaxPairAgeMinutes
    {
        get => _launchMaxPairAgeMinutes;
        set
        {
            this.RaiseAndSetIfChanged(ref _launchMaxPairAgeMinutes, Math.Max(1m, value));
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public decimal WarmPairMinAgeMinutes
    {
        get => _warmPairMinAgeMinutes;
        set
        {
            this.RaiseAndSetIfChanged(ref _warmPairMinAgeMinutes, Math.Max(0m, value));
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public IReadOnlyList<string> StrategyModeOptions { get; } =
        ["Mixed", "Launch Sniper", "Momentum Continuation", "Reversal / Reclaim"];

    public string SelectedStrategyMode
    {
        get => _selectedStrategyMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Mixed" : value.Trim();
            this.RaiseAndSetIfChanged(ref _selectedStrategyMode, normalized);
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public decimal MaxVolumeToLiquidityRatio
    {
        get => _maxVolumeToLiquidityRatio;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxVolumeToLiquidityRatio, Math.Max(0m, value));
            PersistSettings();
        }
    }

    public decimal MaxMarketCapToLiquidityRatio
    {
        get => _maxMarketCapToLiquidityRatio;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxMarketCapToLiquidityRatio, Math.Max(0m, value));
            PersistSettings();
        }
    }

    public bool EnableExecutionGuard
    {
        get => _enableExecutionGuard;
        set
        {
            this.RaiseAndSetIfChanged(ref _enableExecutionGuard, value);
            PersistSettings();
        }
    }

    public bool BlockSuspectedHoneypots
    {
        get => _blockSuspectedHoneypots;
        set
        {
            this.RaiseAndSetIfChanged(ref _blockSuspectedHoneypots, value);
            PersistSettings();
        }
    }

    public bool PaperTradingEnabled
    {
        get => _paperTradingEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _paperTradingEnabled, value);
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public bool AutoTakeProfitEnabled
    {
        get => _autoTakeProfitEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoTakeProfitEnabled, value);
            this.RaisePropertyChanged(nameof(TakeProfitSummary));
            PersistSettings();
        }
    }

    public decimal TakeProfitPercent
    {
        get => _takeProfitPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _takeProfitPercent, Math.Max(0m, value));
            this.RaisePropertyChanged(nameof(TakeProfitSummary));
            PersistSettings();
        }
    }

    public bool AutoStopLossEnabled
    {
        get => _autoStopLossEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoStopLossEnabled, value);
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public decimal StopLossPercent
    {
        get => _stopLossPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _stopLossPercent, Math.Max(0m, value));
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public bool AutoTrailingStopEnabled
    {
        get => _autoTrailingStopEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoTrailingStopEnabled, value);
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public decimal TrailingStopPercent
    {
        get => _trailingStopPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _trailingStopPercent, Math.Max(0m, value));
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public bool PartialTakeProfitEnabled
    {
        get => _partialTakeProfitEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _partialTakeProfitEnabled, value);
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public decimal PartialTakeProfitTriggerPercent
    {
        get => _partialTakeProfitTriggerPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _partialTakeProfitTriggerPercent, Math.Max(0m, value));
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public decimal PartialTakeProfitSellPercent
    {
        get => _partialTakeProfitSellPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _partialTakeProfitSellPercent, Math.Clamp(value, 1m, 99m));
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public bool BreakEvenEnabled
    {
        get => _breakEvenEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _breakEvenEnabled, value);
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public decimal BreakEvenTriggerPercent
    {
        get => _breakEvenTriggerPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _breakEvenTriggerPercent, Math.Max(0m, value));
            this.RaisePropertyChanged(nameof(ExitEngineSummary));
            PersistSettings();
        }
    }

    public decimal MaxSimulatedBuyTaxPercent
    {
        get => _maxSimulatedBuyTaxPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxSimulatedBuyTaxPercent, Math.Max(0m, value));
            PersistSettings();
        }
    }

    public decimal MaxSimulatedSellTaxPercent
    {
        get => _maxSimulatedSellTaxPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxSimulatedSellTaxPercent, Math.Max(0m, value));
            PersistSettings();
        }
    }

    public int CooldownSeconds
    {
        get => _cooldownSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _cooldownSeconds, Math.Max(0, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public int MaxSimultaneousPositions
    {
        get => _maxSimultaneousPositions;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxSimultaneousPositions, Math.Max(1, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public int MaxBuysPerSession
    {
        get => _maxBuysPerSession;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxBuysPerSession, Math.Max(1, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public decimal MaxDailyLiveLossNative
    {
        get => _maxDailyLiveLossNative;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxDailyLiveLossNative, Math.Max(0m, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public decimal MaxExposurePerChainNative
    {
        get => _maxExposurePerChainNative;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxExposurePerChainNative, Math.Max(0m, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public decimal MaxExposurePerWalletNative
    {
        get => _maxExposurePerWalletNative;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxExposurePerWalletNative, Math.Max(0m, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public int MaxConsecutiveLiveLosses
    {
        get => _maxConsecutiveLiveLosses;
        set
        {
            this.RaiseAndSetIfChanged(ref _maxConsecutiveLiveLosses, Math.Max(0, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public decimal HardCapTotalLiveExposureNative
    {
        get => _hardCapTotalLiveExposureNative;
        set
        {
            this.RaiseAndSetIfChanged(ref _hardCapTotalLiveExposureNative, Math.Max(0m, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public decimal TinyDryRunCapNative
    {
        get => _tinyDryRunCapNative;
        set
        {
            this.RaiseAndSetIfChanged(ref _tinyDryRunCapNative, Math.Max(0m, value));
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public bool RequireBnbQuote
    {
        get => _requireBnbQuote;
        set
        {
            this.RaiseAndSetIfChanged(ref _requireBnbQuote, value);
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public bool PreferStableQuote
    {
        get => _preferStableQuote;
        set
        {
            if (_preferStableQuote == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _preferStableQuote, value);
            _walletWorkspace.GlobalQuoteAssetSymbol = value
                ? string.Equals(_walletWorkspace.GlobalQuoteAssetSymbol, "NATIVE", StringComparison.OrdinalIgnoreCase)
                    ? "USDT"
                    : _walletWorkspace.GlobalQuoteAssetSymbol
                : "NATIVE";
            _pendingGlobalSizingApply = true;
            this.RaisePropertyChanged(nameof(BuyAmountLabel));
            this.RaisePropertyChanged(nameof(StableQuoteModeLabel));
            this.RaisePropertyChanged(nameof(StableQuoteModeBrush));
            this.RaisePropertyChanged(nameof(StableQuoteBalanceLabel));
            this.RaisePropertyChanged(nameof(StableQuoteSpendableLabel));
            this.RaisePropertyChanged(nameof(HasEnoughStableQuoteBalanceForConfiguredBuy));
            this.RaisePropertyChanged(nameof(StableQuoteAvailabilityMessage));
            this.RaisePropertyChanged(nameof(StableQuoteAvailabilityBrush));
            RunLoggedAsync(RefreshStableQuoteBalanceAsync, "Stable quote balance refresh");
            ApplyGlobalSniperSizingIfReady();
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public string EnabledChainsText
    {
        get => _enabledChainsText;
        set
        {
            this.RaiseAndSetIfChanged(ref _enabledChainsText, value);
            PersistSettings();
            RaiseSafetyProperties();
        }
    }

    public string WhitelistText
    {
        get => _whitelistText;
        set
        {
            this.RaiseAndSetIfChanged(ref _whitelistText, value);
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public string WatchlistText
    {
        get => _watchlistText;
        set
        {
            this.RaiseAndSetIfChanged(ref _watchlistText, value);
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public string BlacklistText
    {
        get => _blacklistText;
        set
        {
            this.RaiseAndSetIfChanged(ref _blacklistText, value);
            RaiseSafetyProperties();
            PersistSettings();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string LatestRiskVerdictTitle
    {
        get => _latestRiskVerdictTitle;
        set => this.RaiseAndSetIfChanged(ref _latestRiskVerdictTitle, value);
    }

    public string LatestRiskNarrative
    {
        get => _latestRiskNarrative;
        set => this.RaiseAndSetIfChanged(ref _latestRiskNarrative, value);
    }

    public string LatestRiskFlags
    {
        get => _latestRiskFlags;
        set => this.RaiseAndSetIfChanged(ref _latestRiskFlags, value);
    }

    public string LatestRiskAccentHex
    {
        get => _latestRiskAccentHex;
        set => this.RaiseAndSetIfChanged(ref _latestRiskAccentHex, value);
    }

    public string LatestRiskScoreLabel
    {
        get => _latestRiskScoreLabel;
        set => this.RaiseAndSetIfChanged(ref _latestRiskScoreLabel, value);
    }

    public string LatestStructureVerdict
    {
        get => _latestStructureVerdict;
        set => this.RaiseAndSetIfChanged(ref _latestStructureVerdict, value);
    }

    public string LatestStructureNarrative
    {
        get => _latestStructureNarrative;
        set => this.RaiseAndSetIfChanged(ref _latestStructureNarrative, value);
    }

    public string LatestStructureAccentHex
    {
        get => _latestStructureAccentHex;
        set => this.RaiseAndSetIfChanged(ref _latestStructureAccentHex, value);
    }

    public string LatestExecutionVerdict
    {
        get => _latestExecutionVerdict;
        set => this.RaiseAndSetIfChanged(ref _latestExecutionVerdict, value);
    }

    public string LatestExecutionReason
    {
        get => _latestExecutionReason;
        set => this.RaiseAndSetIfChanged(ref _latestExecutionReason, value);
    }

    public string LatestExecutionAccentHex
    {
        get => _latestExecutionAccentHex;
        set => this.RaiseAndSetIfChanged(ref _latestExecutionAccentHex, value);
    }

    public DateTime LastScanLocal
    {
        get => _lastScanLocal;
        set => this.RaiseAndSetIfChanged(ref _lastScanLocal, value);
    }

    public string LastSignalMessageKind
    {
        get => _lastSignalMessageKind;
        set => this.RaiseAndSetIfChanged(ref _lastSignalMessageKind, value);
    }

    public string LastSignalNarrative
    {
        get => _lastSignalNarrative;
        set => this.RaiseAndSetIfChanged(ref _lastSignalNarrative, value);
    }

    public string LastSnapshotSource
    {
        get => _lastSnapshotSource;
        set => this.RaiseAndSetIfChanged(ref _lastSnapshotSource, value);
    }

    public DateTime LastFeedMessageLocal
    {
        get => _lastFeedMessageLocal;
        set => this.RaiseAndSetIfChanged(ref _lastFeedMessageLocal, value);
    }

    public int LastSnapshotTokenCount
    {
        get => _lastSnapshotTokenCount;
        set => this.RaiseAndSetIfChanged(ref _lastSnapshotTokenCount, value);
    }

    public int LastDiscoveredTokenCount
    {
        get => _lastDiscoveredTokenCount;
        set => this.RaiseAndSetIfChanged(ref _lastDiscoveredTokenCount, value);
    }

    public int LastAcceptedTokenCount
    {
        get => _lastAcceptedTokenCount;
        set => this.RaiseAndSetIfChanged(ref _lastAcceptedTokenCount, value);
    }

    public int LastRejectedTokenCount
    {
        get => _lastRejectedTokenCount;
        set => this.RaiseAndSetIfChanged(ref _lastRejectedTokenCount, value);
    }

    public int LastObservedUpdateCount
    {
        get => _lastObservedUpdateCount;
        set => this.RaiseAndSetIfChanged(ref _lastObservedUpdateCount, value);
    }

    public int LastTrackedPositionUpdateCount
    {
        get => _lastTrackedPositionUpdateCount;
        set => this.RaiseAndSetIfChanged(ref _lastTrackedPositionUpdateCount, value);
    }

    public DateTime LastOpenPositionPulseLocal
    {
        get => _lastOpenPositionPulseLocal;
        set => this.RaiseAndSetIfChanged(ref _lastOpenPositionPulseLocal, value);
    }

    public int LastOpenPositionPulseCount
    {
        get => _lastOpenPositionPulseCount;
        set => this.RaiseAndSetIfChanged(ref _lastOpenPositionPulseCount, value);
    }

    public string OpenPositionTrackerStatus
    {
        get => _openPositionTrackerStatus;
        set => this.RaiseAndSetIfChanged(ref _openPositionTrackerStatus, value);
    }

    public string AutoModeLabel => AutoBuyEnabled ? "Full Auto" : "Semi Auto";
    public string ExecutionModeLabel => PaperTradingEnabled
        ? $"{SelectedScanVenue} paper sniper"
        : SupportsSelectedVenueLiveExecution
            ? $"{SelectedScanVenue} live sniper"
            : $"{SelectedScanVenue} live blocked";
    public string GlobalExecutionModeLabel => _walletWorkspace.GlobalExecutionModeLabel;
    public string GlobalExecutionSummary => _walletWorkspace.GlobalExecutionSummary;
    public string GlobalPositionSizingLabel => _walletWorkspace.GlobalPositionSizingLabel;
    public string GlobalPositionSizingSummary => _walletWorkspace.GlobalPositionSizingSummary;
    public string VenueSummary => IsDexVenue
        ? $"DEX launch scanner across {EnabledChainsSummary}."
        : IsFuturesVenue
            ? $"Binance USD-M futures scanner across {string.Join(", ", _cexSymbols)} at x{FuturesLeverage} with {SelectedFuturesBias} bias."
            : $"Binance spot scanner across {string.Join(", ", _cexSymbols)}.";
    public string TradingProfileSummary => IsScalpProfile
        ? $"Scalp profile active | {SelectedScalpPreset} | TP {TakeProfitPercent:0.##}% / SL {StopLossPercent:0.##}% / cooldown {CooldownSeconds}s."
        : $"Balanced profile active | {SelectedStrategyMode} | momentum floor {MinMomentum5m:0.##}% / risk cap {MaxRiskScore}.";
    public string ScalpPresetSummary => GetScalpPresetSummary(SelectedScalpPreset);
    public string FuturesExecutionSummary => IsFuturesVenue
        ? $"Futures bias {SelectedFuturesBias} | leverage x{FuturesLeverage} | {(PaperTradingEnabled ? "paper execution" : "live execution is guarded in sniper mode")}{(_futuresRegionFallbackToSpotActive ? " | region fallback: spot market data" : string.Empty)}"
        : "Futures parameters are hidden until CEX Futures mode is selected.";
    public string BuyAmountLabel => PreferStableQuote ? $"Buy ({PreferredStableQuoteSymbol}-first shared quote)" : "Buy (native asset)";
    public string PreferredStableQuoteSymbol => _walletWorkspace.EffectiveGlobalQuoteAssetSymbol;
    public string StableQuoteModeLabel => PreferStableQuote ? $"{PreferredStableQuoteSymbol} ACTIVE" : "NATIVE MODE";
    public string StableQuoteModeBrush => PreferStableQuote ? "#14E0C1" : "#8FA3B8";
    public decimal StableQuoteBalance
    {
        get => _stableQuoteBalance;
        private set
        {
            this.RaiseAndSetIfChanged(ref _stableQuoteBalance, value);
            this.RaisePropertyChanged(nameof(StableQuoteBalanceLabel));
            this.RaisePropertyChanged(nameof(StableQuoteSpendableLabel));
            this.RaisePropertyChanged(nameof(HasEnoughStableQuoteBalanceForConfiguredBuy));
            this.RaisePropertyChanged(nameof(StableQuoteAvailabilityMessage));
            this.RaisePropertyChanged(nameof(StableQuoteAvailabilityBrush));
        }
    }
    public bool IsStableQuoteBalanceLoading
    {
        get => _isStableQuoteBalanceLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isStableQuoteBalanceLoading, value);
            this.RaisePropertyChanged(nameof(StableQuoteBalanceLabel));
            this.RaisePropertyChanged(nameof(StableQuoteSpendableLabel));
        }
    }
    public string StableQuoteBalanceLabel => !PreferStableQuote
        ? "Stable quote mode is off."
        : IsStableQuoteBalanceLoading
            ? "Loading stable balance..."
            : $"{StableQuoteBalance:0.######} {PreferredStableQuoteSymbol}";
    public string StableQuoteSpendableLabel => !PreferStableQuote
        ? "Switch stable mode on to size buys from quote balance."
        : IsStableQuoteBalanceLoading
            ? "Calculating spendable stable balance..."
            : $"Available to spend: {StableQuoteBalance:0.######} {PreferredStableQuoteSymbol}";
    public bool HasEnoughStableQuoteBalanceForConfiguredBuy => !PreferStableQuote || BuyAmountBnb <= 0m || BuyAmountBnb <= StableQuoteBalance;
    public string StableQuoteAvailabilityMessage => !PreferStableQuote
        ? "Native sniper mode uses the connected wallet native balance."
        : HasEnoughStableQuoteBalanceForConfiguredBuy
            ? $"Configured buy amount fits the available {PreferredStableQuoteSymbol} balance."
            : $"Not enough {PreferredStableQuoteSymbol}. Need {BuyAmountBnb:0.######}, available {StableQuoteBalance:0.######}.";
    public string StableQuoteAvailabilityBrush => HasEnoughStableQuoteBalanceForConfiguredBuy ? "#14E0C1" : "#FF6B6B";
    public string TakeProfitSummary =>
        AutoTakeProfitEnabled
            ? $"Auto-sell armed at +{TakeProfitPercent:0.##}%"
            : "Auto-sell disabled";
    public string ExitEngineSummary =>
        $"{TakeProfitSummary} | {(PartialTakeProfitEnabled ? $"Partial {PartialTakeProfitSellPercent:0.#}% at +{PartialTakeProfitTriggerPercent:0.##}%" : "Partial off")} | {(BreakEvenEnabled ? $"Break-even at +{BreakEvenTriggerPercent:0.##}%" : "Break-even off")} | {(AutoStopLossEnabled ? $"Stop-loss -{StopLossPercent:0.##}%" : "Stop-loss off")} | {(AutoTrailingStopEnabled ? $"Trailing {TrailingStopPercent:0.##}%" : "Trailing off")}";
    public string PresetSummary => $"{_selectedPresetName} preset | {SelectedTradingProfile}";
    public string EnabledChainsSummary => IsDexVenue
        ? string.Join(", ", GetEnabledChainProfiles().Select(static profile => profile.DisplayName))
        : string.Join(", ", _cexSymbols);
    public string StrategyModeSummary =>
        IsDexVenue
            ? $"{SelectedStrategyMode} | Pair age {MinPairAgeMinutes:0.#}-{(MaxPairAgeMinutes <= 0m ? "inf" : MaxPairAgeMinutes.ToString("0.#"))}m | Launch<={LaunchMaxPairAgeMinutes:0.#}m | Warm>={WarmPairMinAgeMinutes:0.#}m"
            : $"{SelectedStrategyMode} | 5m >= {MinMomentum5m:0.##}% | 24h volume >= ${MinVolume24hUsd:N0} | universe {string.Join(", ", _cexSymbols)}";
    public string QuoteRoutingSummary
    {
        get
        {
            if (IsCexVenue)
            {
                return IsFuturesVenue
                    ? "CEX futures routing uses Binance USD-M perpetual contracts quoted in USDT."
                    : "CEX spot routing uses Binance spot USDT pairs.";
            }

            if (PreferStableQuote)
            {
                var stableRoutes = GetSupportedStableQuoteSymbolsForChain(GetChainIdForNetwork(_walletWorkspace.SelectedNetwork));
                var stableRouteLabel = stableRoutes.Count == 0
                    ? "no stable routes configured"
                    : string.Join(", ", stableRoutes);
                return $"Stable-first mode keeps supported stable pools eligible and prefers {PreferredStableQuoteSymbol} on {_walletWorkspace.SelectedNetwork}. Active wallet routes: {stableRouteLabel}.";
            }

            return RequireBnbQuote
                ? "Native-only filter is active. The sniper favors native and wrapped-native quote pools for faster routing; disable it to admit stable-quoted pairs too."
                : "Flexible quote coverage is active. Native, wrapped-native, and configured stable quote pools can all enter the queue.";
        }
    }
    public string SupportedQuoteRoutesSummary
    {
        get
        {
            if (IsCexVenue)
            {
                return $"Tracked Binance symbols: {string.Join(", ", _cexSymbols)}.";
            }

            var options = DexQuoteAssetCatalog.GetOptions(_walletWorkspace.SelectedNetwork);
            return options.Count == 0
                ? $"No quote routes are configured for {_walletWorkspace.SelectedNetwork}."
                : $"{_walletWorkspace.SelectedNetwork} live routes: {string.Join(", ", options.Select(static option => option.Symbol))}.";
        }
    }
    public string ChainCoverageSummary => IsDexVenue
        ? $"{EnabledChainsSummary} enabled. Live execution works on BSC, Ethereum, Base, Solana, and Tron when the wallet is armed on the same chain."
        : $"{SelectedScanVenue} coverage includes {EnabledChainsSummary}. Scanner ranks centralized exchange movers instead of fresh on-chain launches.";
    public string TokenFilterSummary =>
        $"Whitelist {CountFragments(WhitelistText)} | Watchlist {CountFragments(WatchlistText)} | Blacklist {CountFragments(BlacklistText)} fragment(s).";
    public string NetworkExecutionMatrix =>
        "BSC, Ethereum, Base: live sniper execution available when the wallet is armed on the same network. Solana: live sniper execution is available through the Solana signing core and Jupiter swap path. Tron: live sniper execution is available through the TronGrid signing core and SunSwap router.";
    public string SignalFeedSummary => IsDexVenue
        ? "Hybrid signal feed: EVM factory events -> address enrichment -> safety checks, plus Solana program activity -> transaction parsing -> direct mint enrichment. Indexed snapshot fallback stays enabled as a recovery layer."
        : IsFuturesVenue
            ? "Binance futures feed: live ticker cache + periodic candle momentum snapshot + futures scalp heuristics."
            : "Binance spot feed: live ticker cache + periodic candle momentum snapshot + centralized exchange mover heuristics.";
    public string FeedTelemetrySummary =>
        $"{LastSignalMessageKind} | {LastFeedMessageLabel} | Last scan {(LastScanLocal == DateTime.MinValue ? "--" : LastScanLocal.ToString("HH:mm:ss"))}";
    public string SnapshotTelemetrySummary =>
        $"{LastSnapshotSource} | Snapshot {LastSnapshotTokenCount} | Queue updates {LastObservedUpdateCount} | Position refreshes {LastTrackedPositionUpdateCount}";
    public string QueueTelemetrySummary =>
        $"New {LastDiscoveredTokenCount} | Accepted {LastAcceptedTokenCount} | Filtered {LastRejectedTokenCount} | Fresh {FreshPairs.Count} | Queue {AcceptedPairs.Count}";
    public string SignalNarrativeSummary => LastSignalNarrative;
    public string WalletExecutionSummary =>
        $"{WalletStatus} | {(CanExecuteAutoBuy ? "Execution ready" : "Execution blocked")} | {(IsArmed ? "Scanner armed" : "Scanner idle")} | {SelectedScanVenue}";
    public string OpenPositionPulseSummary =>
        LastOpenPositionPulseLocal == DateTime.MinValue
            ? "No position market ticks yet."
            : $"Last market tick {LastOpenPositionPulseLocal:HH:mm:ss} | Updated {LastOpenPositionPulseCount} tracked position quote(s)";
    public string FreshPairsSummary =>
        FreshPairs.Count == 0
            ? "No fresh pairs visible yet."
            : $"{FreshPairs.Count} tracked | Latest: {FreshPairs[0].DisplayName} | {FreshPairs[0].SignalConfirmationLabel} | {FreshPairs[0].Status}";
    public string AcceptedQueueSummary =>
        AcceptedPairs.Count == 0
            ? "No accepted pairs yet."
            : $"{AcceptedPairs.Count} ready | Next: {AcceptedPairs[0].DisplayName} | {AcceptedPairs[0].SignalConfirmationLabel} | {AcceptedPairs[0].Status}";
    public string OpenPositionsSummary =>
        OpenPositions.Count == 0
            ? "No live positions open."
            : $"{OpenPositions.Count} live | {OpenPositionPulseSummary} | Latest: {OpenPositions[0].DisplayName} | {OpenPositions[0].Status}";
    public string LatestAuditSummary =>
        ExecutionAuditTrail.Count == 0
            ? "No execution audit events recorded yet."
            : $"{ExecutionAuditTrail[0].AuditTitle} | {ExecutionAuditTrail[0].MetaLine} | {ExecutionAuditTrail[0].TxLine}";
    public string LatestDecisionSummary =>
        DecisionLog.Count == 0
            ? "No decisions recorded yet."
            : $"{DecisionLog[0].LocalTime:HH:mm:ss} | {DecisionLog[0].Message}";
    public string LastFeedMessageLabel =>
        LastFeedMessageLocal == DateTime.MinValue ? "No feed message yet" : $"Feed {LastFeedMessageLocal:HH:mm:ss}";
    public bool CexLiveExecutionEnabled
    {
        get => _cexLiveExecutionEnabled;
        set => this.RaiseAndSetIfChanged(ref _cexLiveExecutionEnabled, value);
    }

    public bool CexLiveAcknowledged
    {
        get => _cexLiveAcknowledged;
        set => this.RaiseAndSetIfChanged(ref _cexLiveAcknowledged, value);
    }

    public bool HasCexGateway => IsFuturesVenue ? _futuresGateway is not null : _spotGateway is not null;

    public string WalletStatus => IsDexVenue
        ? _walletWorkspace.WalletCapabilityText
        : PaperTradingEnabled
            ? "CEX sniper is armed in paper mode and reads Binance market data."
            : _cexLiveExecutionEnabled && _cexLiveAcknowledged && HasCexGateway
                ? "CEX sniper live execution is armed. Orders will be placed on the exchange."
                : "CEX sniper: enable CexLiveExecutionEnabled + acknowledge risk to unlock live execution.";
    public bool SupportsSelectedVenueLiveExecution =>
        (IsDexVenue && _walletWorkspace.CanUseDexTradingOnSelectedNetwork && _walletWorkspace.GlobalLiveExecutionEnabled)
        || (IsCexVenue && _cexLiveExecutionEnabled && _cexLiveAcknowledged && HasCexGateway);
    public bool CanExecuteAutoBuy => AutoBuyEnabled &&
        (PaperTradingEnabled || SupportsSelectedVenueLiveExecution);
    public bool CanEmergencyCloseLivePositions => string.IsNullOrWhiteSpace(EmergencyCloseBlockedReason);
    public string EmergencyCloseBlockedReason => GetEmergencyCloseBlockedReason();
    public string SniperGuardStatusLabel => CanExecuteAutoBuy
        ? "AUTO PATH READY"
        : _walletWorkspace.GlobalPaperOnlyMode
            ? "PAPER LOCK"
            : _walletWorkspace.IsReadOnly
                ? "WATCH MODE"
                : "AUTO BLOCKED";
    public string SniperGuardStatusBrush => SniperGuardStatusLabel switch
    {
        "AUTO PATH READY" => "#21E6C1",
        "PAPER LOCK" => "#F4B860",
        "WATCH MODE" => "#5BC0EB",
        _ => "#FF8A65"
    };
    public string SniperGuardSummary => CanExecuteAutoBuy
        ? $"Sniper can execute on {SelectedScanVenue} with {PreferredStableQuoteSymbol} routing preferences."
        : AutoBuyEnabled
            ? WalletExecutionSummary
            : "Auto-buy is disabled, so sniper stays in observation mode until you re-arm execution.";
    public string SniperGuardDetail => $"{_walletWorkspace.RouteReadinessSummary} | {LiveRiskSummary}";
    public int OpenPositionCount => OpenPositions.Count + PaperPositions.Count;
    public int RemainingPositionSlots => Math.Max(0, MaxSimultaneousPositions - OpenPositionCount);
    public int SessionBuyCount => _sessionBuyCount;
    public int RemainingSessionBuys => Math.Max(0, MaxBuysPerSession - _sessionBuyCount);
    public int PaperTradeCount => PaperTradeHistory.Count;
    public int LiveTradeCount => LiveTradeHistory.Count;
    public int WinningPaperTradeCount => PaperTradeHistory.Count(static trade => trade.PnlPercent > 0m);
    public int WinningLiveTradeCount => LiveTradeHistory.Count(static trade => trade.PnlPercent > 0m);
    public decimal WinRatePercent =>
        PaperTradeHistory.Count == 0
            ? 0m
            : (decimal)WinningPaperTradeCount / PaperTradeHistory.Count * 100m;
    public decimal AveragePaperPnlPercent =>
        PaperTradeHistory.Count == 0
            ? 0m
            : PaperTradeHistory.Average(static trade => trade.PnlPercent);
    public string WinRateLabel => PaperTradeHistory.Count == 0 ? "--" : $"{WinRatePercent:0.#}%";
    public string AveragePaperPnlLabel => PaperTradeHistory.Count == 0 ? "--" : $"{AveragePaperPnlPercent:+0.##;-0.##;0}%";
    public decimal LiveWinRatePercent =>
        LiveTradeHistory.Count == 0
            ? 0m
            : (decimal)WinningLiveTradeCount / LiveTradeHistory.Count * 100m;
    public string LiveWinRateLabel => LiveTradeHistory.Count == 0 ? "--" : $"{LiveWinRatePercent:0.#}%";
    public decimal AverageLivePnlPercent =>
        LiveTradeHistory.Count == 0
            ? 0m
            : LiveTradeHistory.Average(static trade => trade.PnlPercent);
    public string AverageLivePnlLabel => LiveTradeHistory.Count == 0 ? "--" : $"{AverageLivePnlPercent:+0.##;-0.##;0}%";
    public string BestPaperTradeLabel =>
        PaperTradeHistory.Count == 0
            ? "--"
            : FormatPaperTradeStat(PaperTradeHistory.MaxBy(static trade => trade.PnlPercent));
    public string WorstPaperTradeLabel =>
        PaperTradeHistory.Count == 0
            ? "--"
            : FormatPaperTradeStat(PaperTradeHistory.MinBy(static trade => trade.PnlPercent));
    public string BestLiveTradeLabel =>
        LiveTradeHistory.Count == 0
            ? "--"
            : FormatPaperTradeStat(LiveTradeHistory.MaxBy(static trade => trade.PnlPercent));
    public string WorstLiveTradeLabel =>
        LiveTradeHistory.Count == 0
            ? "--"
            : FormatPaperTradeStat(LiveTradeHistory.MinBy(static trade => trade.PnlPercent));
    public int CombinedTradeCount => PaperTradeHistory.Count + LiveTradeHistory.Count;
    public int CombinedWinningTradeCount => WinningPaperTradeCount + WinningLiveTradeCount;
    public decimal CombinedWinRatePercent =>
        CombinedTradeCount == 0
            ? 0m
            : (decimal)CombinedWinningTradeCount / CombinedTradeCount * 100m;

    private string GetEmergencyCloseBlockedReason()
    {
        if (OpenPositions.Count == 0)
        {
            return "No live open positions are available for emergency close.";
        }

        var executionReason = _walletWorkspace.GetExecutionGuardBlockReason("Sniper emergency close");
        if (!string.IsNullOrWhiteSpace(executionReason))
        {
            return executionReason;
        }

        // CEX positions close via exchange gateway — DEX wallet is not required.
        bool hasDexPositions = OpenPositions.Any(p => !IsCexToken(p.TokenInfo));
        if (hasDexPositions && _walletWorkspace.ActiveDexGateway is null)
        {
            return "The active wallet session is not attached to a live DEX connector.";
        }

        return string.Empty;
    }
    public string CombinedWinRateLabel => CombinedTradeCount == 0 ? "--" : $"{CombinedWinRatePercent:0.#}%";
    public decimal NetClosedPnlPercent => PaperTradeHistory.Sum(static trade => trade.PnlPercent) + LiveTradeHistory.Sum(static trade => trade.PnlPercent);
    public string NetClosedPnlLabel => CombinedTradeCount == 0 ? "--" : $"{NetClosedPnlPercent:+0.##;-0.##;0}%";
    public decimal CombinedAveragePnlPercent =>
        CombinedTradeCount == 0
            ? 0m
            : NetClosedPnlPercent / CombinedTradeCount;
    public string CombinedAveragePnlLabel => CombinedTradeCount == 0 ? "--" : $"{CombinedAveragePnlPercent:+0.##;-0.##;0}%";
    public string TradeMixLabel => $"Paper {PaperTradeCount} | Live {LiveTradeCount}";
    public bool IsPerformancePositive => NetClosedPnlPercent >= 0m;
    public decimal OpenPaperPnlAveragePercent =>
        PaperPositions.Count == 0
            ? 0m
            : PaperPositions.Average(static position => position.PaperPnlPercent);
    public string OpenPaperPnlLabel => PaperPositions.Count == 0 ? "--" : $"{OpenPaperPnlAveragePercent:+0.##;-0.##;0}%";
    public int OpenRunnerCount => PaperPositions.Count(static position => position.PartialTakeProfitExecuted) + OpenPositions.Count(static position => position.PartialTakeProfitExecuted);
    public bool IsCooldownActive => CooldownRemainingSeconds > 0;
    public int CooldownRemainingSeconds
    {
        get
        {
            if (_lastBuyUtc is null || CooldownSeconds <= 0)
            {
                return 0;
            }

            var elapsed = DateTime.UtcNow - _lastBuyUtc.Value;
            var remaining = CooldownSeconds - (int)Math.Floor(elapsed.TotalSeconds);
            return Math.Max(0, remaining);
        }
    }

    public string CooldownStatus =>
        IsCooldownActive
            ? $"Cooling down - {CooldownRemainingSeconds}s remaining"
            : "Ready for the next entry";

    public decimal DailyLiveLossNative => BuildRiskSnapshot().DailyLiveLossNative;
    public decimal TotalLiveExposureNative => BuildRiskSnapshot().TotalLiveExposureNative;
    public int ConsecutiveLiveLossCount => BuildRiskSnapshot().ConsecutiveLiveLosses;
    public bool IsEmergencyRiskStopActive => _riskPolicyService.IsEmergencyStopActive(BuildRiskSnapshot(), BuildRiskLimits());
    public string LiveReadySummary =>
        LiveReadyStatuses.Count == 0
            ? "No live-readiness evidence yet."
            : $"{LiveReadyStatuses.Count(static status => status.IsReady)}/{LiveReadyStatuses.Count} network paths have complete live evidence.";
    public string LiveRiskSummary =>
        $"Loss today {FormatRiskAmount(DailyLiveLossNative)}/{FormatRiskLimit(MaxDailyLiveLossNative)} | " +
        $"Wallet exposure {FormatRiskAmount(TotalLiveExposureNative)}/{FormatRiskLimit(MaxExposurePerWalletNative)} | " +
        $"Hard cap {FormatRiskLimit(HardCapTotalLiveExposureNative)} | " +
        $"Loss streak {ConsecutiveLiveLossCount}/{FormatRiskCountLimit(MaxConsecutiveLiveLosses)}" +
        (IsEmergencyRiskStopActive ? " | Emergency stop ACTIVE" : string.Empty);

    public string SafetySummary =>
        $"Open {OpenPositionCount}/{MaxSimultaneousPositions} | Session buys {SessionBuyCount}/{MaxBuysPerSession} | Cooldown {CooldownStatus}" +
        (PaperTradingEnabled ? string.Empty : $" | {(IsEmergencyRiskStopActive ? "Emergency stop ACTIVE" : "Live risk gates armed")}") +
        $" | {SelectedScanVenue}";

    public void Dispose()
    {
        _scanTimer.Stop();
        _positionPulseTimer.Stop();
        _manualSnipeQuoteTimer.Stop();
        StopSignalStream();
        _spotMarketSubscription?.Dispose();
        _futuresMarketSubscription?.Dispose();
        foreach (var subscription in _commandErrorSubscriptions)
        {
            subscription.Dispose();
        }
        foreach (var mgr in _cexTpSlManagers.Values)
        {
            _ = mgr.DetachAsync();
            mgr.Dispose();
        }
        _cexTpSlManagers.Clear();
        _cexSnapshotLock.Dispose();
        _walletWorkspace.PropertyChanged -= OnWalletWorkspacePropertyChanged;
    }

    private void OnSpotMarketDataReceived(MarketData data)
    {
        if (string.IsNullOrWhiteSpace(data.Symbol))
        {
            return;
        }

        lock (_marketDataCacheLock)
        {
            _spotMarketBySymbol[data.Symbol] = data;
        }
    }

    private void OnFuturesMarketDataReceived(MarketData data)
    {
        if (string.IsNullOrWhiteSpace(data.Symbol))
        {
            return;
        }

        lock (_marketDataCacheLock)
        {
            _futuresMarketBySymbol[data.Symbol] = data;
        }
    }

    private static string NormalizeVenue(string? venue) => venue?.Trim() switch
    {
        "CEX Spot" => "CEX Spot",
        "CEX Futures" => "CEX Futures",
        _ => "DEX"
    };

    private static string NormalizeTradingProfile(string? profile) => profile?.Trim() switch
    {
        "Scalp" => "Scalp",
        _ => "Balanced"
    };

    private static string NormalizeScalpPreset(string? preset) => preset?.Trim() switch
    {
        "Tight" => "Tight",
        "Aggro" => "Aggro",
        _ => "Standard"
    };

    private static string NormalizeFuturesBias(string? bias) => bias?.Trim() switch
    {
        "Long Only" => "Long Only",
        "Short Only" => "Short Only",
        _ => "Long & Short"
    };

    private static string GetScalpPresetSummary(string preset) => NormalizeScalpPreset(preset) switch
    {
        "Tight" => "Fast reclaim setup | smaller stop | tighter take-profit.",
        "Aggro" => "Impulse chasing setup | wider stop | higher take-profit.",
        _ => "Balanced scalp setup | controlled stop | clean continuation target."
    };

    private TimeSpan GetVenueAutoScanInterval() => IsScalpProfile
        ? TimeSpan.FromSeconds(6)
        : TimeSpan.FromSeconds(12);

    private void ApplyWalletGlobalQuoteModeToSniper()
    {
        var shouldUseStableQuote = !string.Equals(_walletWorkspace.GlobalQuoteAssetSymbol, "NATIVE", StringComparison.OrdinalIgnoreCase);
        if (_preferStableQuote == shouldUseStableQuote)
        {
            return;
        }

        this.RaiseAndSetIfChanged(ref _preferStableQuote, shouldUseStableQuote);
        this.RaisePropertyChanged(nameof(BuyAmountLabel));
        this.RaisePropertyChanged(nameof(StableQuoteModeLabel));
        this.RaisePropertyChanged(nameof(StableQuoteModeBrush));
        this.RaisePropertyChanged(nameof(StableQuoteBalanceLabel));
        this.RaisePropertyChanged(nameof(StableQuoteSpendableLabel));
        this.RaisePropertyChanged(nameof(HasEnoughStableQuoteBalanceForConfiguredBuy));
        this.RaisePropertyChanged(nameof(StableQuoteAvailabilityMessage));
        this.RaisePropertyChanged(nameof(StableQuoteAvailabilityBrush));
    }

    private void ApplyGlobalSniperSizingIfReady()
    {
        if (!_pendingGlobalSizingApply)
        {
            return;
        }

        decimal baseBalance;
        if (PreferStableQuote)
        {
            baseBalance = StableQuoteBalance;
            if (baseBalance <= 0)
            {
                return;
            }
        }
        else
        {
            baseBalance = _walletWorkspace.NativeBalance;
            if (baseBalance <= 0)
            {
                return;
            }
        }

        BuyAmountBnb = RoundSniperBuyAmount(baseBalance * (_walletWorkspace.GlobalPositionSizingPercent / 100m));
        _pendingGlobalSizingApply = false;
    }

    private void OnWalletWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletWorkspaceViewModel.SelectedNetwork) or
            nameof(WalletWorkspaceViewModel.ActiveDexGateway) or
            nameof(WalletWorkspaceViewModel.NativeBalance) or
            nameof(WalletWorkspaceViewModel.CanUseDexTradingOnSelectedNetwork) or
            nameof(WalletWorkspaceViewModel.GlobalQuoteAssetSymbol) or
            nameof(WalletWorkspaceViewModel.EffectiveGlobalQuoteAssetSymbol) or
            nameof(WalletWorkspaceViewModel.GlobalPaperOnlyMode) or
            nameof(WalletWorkspaceViewModel.GlobalExecutionModeLabel) or
            nameof(WalletWorkspaceViewModel.GlobalExecutionSummary) or
            nameof(WalletWorkspaceViewModel.GlobalPositionSizingPercent) or
            nameof(WalletWorkspaceViewModel.GlobalPositionSizingLabel) or
            nameof(WalletWorkspaceViewModel.GlobalPositionSizingSummary))
        {
            if (e.PropertyName is nameof(WalletWorkspaceViewModel.GlobalPositionSizingPercent))
            {
                _pendingGlobalSizingApply = true;
            }

            ApplyWalletGlobalQuoteModeToSniper();
            this.RaisePropertyChanged(nameof(PreferredStableQuoteSymbol));
            this.RaisePropertyChanged(nameof(StableQuoteModeLabel));
            this.RaisePropertyChanged(nameof(StableQuoteModeBrush));
            this.RaisePropertyChanged(nameof(GlobalExecutionModeLabel));
            this.RaisePropertyChanged(nameof(GlobalExecutionSummary));
            this.RaisePropertyChanged(nameof(ExecutionModeLabel));
            this.RaisePropertyChanged(nameof(CanExecuteAutoBuy));
            this.RaisePropertyChanged(nameof(GlobalPositionSizingLabel));
            this.RaisePropertyChanged(nameof(GlobalPositionSizingSummary));
            this.RaisePropertyChanged(nameof(StableQuoteBalanceLabel));
            this.RaisePropertyChanged(nameof(StableQuoteSpendableLabel));
            this.RaisePropertyChanged(nameof(HasEnoughStableQuoteBalanceForConfiguredBuy));
            this.RaisePropertyChanged(nameof(StableQuoteAvailabilityMessage));
            this.RaisePropertyChanged(nameof(StableQuoteAvailabilityBrush));
            RunLoggedAsync(RefreshStableQuoteBalanceAsync, "Stable quote balance refresh");
            ApplyGlobalSniperSizingIfReady();
        }
    }

    private async Task RefreshStableQuoteBalanceAsync()
    {
        if (!PreferStableQuote)
        {
            IsStableQuoteBalanceLoading = false;
            StableQuoteBalance = 0m;
            return;
        }

        var gateway = _walletWorkspace.ActiveDexGateway;
        var quoteAsset = DexQuoteAssetCatalog.Find(_walletWorkspace.SelectedNetwork, PreferredStableQuoteSymbol);
        if (gateway is null || quoteAsset?.ContractAddress is null || !_walletWorkspace.CanUseDexTradingOnSelectedNetwork)
        {
            IsStableQuoteBalanceLoading = false;
            StableQuoteBalance = 0m;
            return;
        }

        try
        {
            IsStableQuoteBalanceLoading = true;
            StableQuoteBalance = await gateway.GetTokenBalanceAsync(quoteAsset.ContractAddress);
            ApplyGlobalSniperSizingIfReady();
        }
        catch (Exception ex)
        {
            StableQuoteBalance = 0m;
            PushLog($"Stable quote balance refresh failed for {PreferredStableQuoteSymbol}: {ex.Message}", false);
        }
        finally
        {
            IsStableQuoteBalanceLoading = false;
        }
    }

    private async Task ArmAsync()
    {
        IsArmed = true;
        StatusMessage = IsDexVenue
            ? $"Sniper armed. The stream indexer is priming a warm cache for {EnabledChainsSummary} before live detection starts."
            : $"Sniper armed on {SelectedScanVenue}. Market snapshot priming started for {EnabledChainsSummary}.";
        PushLog(StatusMessage, true);
        if (!_scanTimer.IsEnabled)
        {
            _scanTimer.Start();
        }

        UpdatePositionPulseTimerState();
        StartSignalStream();
        await ScanAsync();
    }

    private void Stop()
    {
        IsArmed = false;
        _scanTimer.Stop();
        UpdatePositionPulseTimerState();
        StopSignalStream();
        StatusMessage = $"Sniper stopped. {SelectedScanVenue} detection is paused.";
        PushLog(StatusMessage, false);
    }

    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        try
        {
            IsScanning = true;
            if (IsDexVenue)
            {
                var snapshot = await _dexClient.GetLatestTokensAsync(GetEnabledChainIds(), 140);
                await ProcessSnapshotAsync(snapshot, "Manual snapshot refreshed from the signal source.", allowDiscovery: true);
            }
            else
            {
                var snapshot = await BuildCexSnapshotAsync(IsFuturesVenue, forceRefresh: true);
                await ProcessSnapshotAsync(snapshot, IsFuturesVenue ? "Binance futures snapshot refreshed." : "Binance spot snapshot refreshed.", allowDiscovery: true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Manual signal refresh failed: {ex.Message}";
            PushLog(StatusMessage, false);
        }
        finally
        {
            IsScanning = false;
            RaiseSafetyProperties();
        }
    }

    private void StartSignalStream()
    {
        if (!IsDexVenue)
        {
            StopSignalStream();
            return;
        }

        if (_signalStreamTask is { IsCompleted: false })
        {
            return;
        }

        StopSignalStream();
        _signalStreamCts = new CancellationTokenSource();
        var reader = _signalStreamService.Start(GetEnabledChainIds(), _signalStreamCts.Token);
        _signalStreamTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in reader.ReadAllAsync(_signalStreamCts.Token))
                {
                    await Dispatcher.UIThread.InvokeAsync(async () => await HandleSignalMessageAsync(message));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PushLog($"Sniper signal stream failed: {ex.Message}", false);
            }
        });
    }

    private void StopSignalStream()
    {
        try
        {
            _signalStreamCts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            _signalStreamCts?.Dispose();
            _signalStreamCts = null;
            _signalStreamTask = null;
        }
    }

    private void UpdatePositionPulseTimerState()
    {
        var shouldTrackPositions = IsArmed || OpenPositions.Count > 0 || PaperPositions.Count > 0;
        if (shouldTrackPositions)
        {
            if (!_positionPulseTimer.IsEnabled)
            {
                _positionPulseTimer.Start();
            }
        }
        else if (_positionPulseTimer.IsEnabled)
        {
            _positionPulseTimer.Stop();
        }
    }

    private async Task RefreshOpenPositionMarketDataAsync()
    {
        if (_isRefreshingTrackedPositions)
        {
            return;
        }

        if (IsCexVenue)
        {
            await RefreshCexPositionMarketDataAsync();
            return;
        }

        var trackedPositions = OpenPositions
            .Concat(PaperPositions)
            .Where(position => !string.IsNullOrWhiteSpace(position.TokenInfo.ChainId) &&
                               !string.IsNullOrWhiteSpace(position.TokenInfo.TokenAddress))
            .ToList();

        if (trackedPositions.Count == 0)
        {
            OpenPositionTrackerStatus = IsArmed
                ? "Position tracker is armed and waiting for an open position."
                : "Position tracker is idle because there are no open positions.";
            LastOpenPositionPulseCount = 0;
            RaiseSafetyProperties();
            return;
        }

        _isRefreshingTrackedPositions = true;
        try
        {
            var refreshedTokens = new List<DexTokenInfo>();
            foreach (var chainGroup in trackedPositions
                         .GroupBy(position => position.TokenInfo.ChainId, StringComparer.OrdinalIgnoreCase))
            {
                var tokens = await _dexClient.GetTokensByAddressesAsync(
                    chainGroup.Key,
                    chainGroup.Select(position => position.TokenInfo.TokenAddress));

                refreshedTokens.AddRange(tokens);
            }

            LastTrackedPositionUpdateCount = await RefreshTrackedPositionsAsync(refreshedTokens);
            LastOpenPositionPulseLocal = DateTime.Now;
            LastOpenPositionPulseCount = LastTrackedPositionUpdateCount;
            OpenPositionTrackerStatus = LastTrackedPositionUpdateCount > 0
                ? $"Tracked {LastTrackedPositionUpdateCount} open-position quote(s) at {LastOpenPositionPulseLocal:HH:mm:ss}."
                : $"Open-position quote poll ran at {DateTime.Now:HH:mm:ss}, but no market quotes were returned for the tracked addresses.";
        }
        catch (Exception ex)
        {
            OpenPositionTrackerStatus = $"Open-position tracker failed: {ex.Message}";
        }
        finally
        {
            _isRefreshingTrackedPositions = false;
            RaiseSafetyProperties();
        }
    }

    private async Task RefreshCexPositionMarketDataAsync()
    {
        var trackedPositions = OpenPositions
            .Concat(PaperPositions)
            .Where(static position => !string.IsNullOrWhiteSpace(position.TokenInfo.TokenAddress))
            .ToList();

        if (trackedPositions.Count == 0)
        {
            OpenPositionTrackerStatus = IsArmed
                ? "Position tracker is armed and waiting for an open position."
                : "Position tracker is idle because there are no open positions.";
            LastOpenPositionPulseCount = 0;
            RaiseSafetyProperties();
            return;
        }

        _isRefreshingTrackedPositions = true;
        try
        {
            var refreshedTokens = await BuildCexSnapshotAsync(IsFuturesVenue);
            LastTrackedPositionUpdateCount = await RefreshTrackedPositionsAsync(refreshedTokens);
            LastOpenPositionPulseLocal = DateTime.Now;
            LastOpenPositionPulseCount = LastTrackedPositionUpdateCount;
            OpenPositionTrackerStatus = LastTrackedPositionUpdateCount > 0
                ? $"Tracked {LastTrackedPositionUpdateCount} open-position quote(s) at {LastOpenPositionPulseLocal:HH:mm:ss}."
                : $"Open-position quote poll ran at {DateTime.Now:HH:mm:ss}, but no market quotes were returned for the tracked symbols.";
        }
        catch (Exception ex)
        {
            OpenPositionTrackerStatus = $"Open-position tracker failed: {ex.Message}";
        }
        finally
        {
            _isRefreshingTrackedPositions = false;
            RaiseSafetyProperties();
        }
    }

    private async Task<IReadOnlyList<DexTokenInfo>> BuildCexSnapshotAsync(bool futures, bool allowSpotFallback = true, bool forceRefresh = false)
    {
        if (!forceRefresh && TryGetCachedCexSnapshot(futures, GetCexSnapshotCacheTtl(), out var cachedSnapshot))
        {
            return cachedSnapshot;
        }

        CryptoAITerminal.Core.Interfaces.IExchangeGateway? gateway = futures ? _futuresGateway : _spotGateway;
        if (gateway is null)
        {
            return [];
        }

        var lockTimeout = forceRefresh ? TimeSpan.FromSeconds(8) : TimeSpan.Zero;
        if (!await _cexSnapshotLock.WaitAsync(lockTimeout))
        {
            return TryGetCachedCexSnapshot(futures, TimeSpan.FromMinutes(5), out cachedSnapshot)
                ? cachedSnapshot
                : [];
        }

        try
        {
            if (!forceRefresh && TryGetCachedCexSnapshot(futures, GetCexSnapshotCacheTtl(), out cachedSnapshot))
            {
                return cachedSnapshot;
            }

            if (futures && allowSpotFallback && _futuresRegionFallbackToSpotActive)
            {
                var fallbackSnapshot = await BuildCexSnapshotWithSpotMarketDataAsync();
                CacheCexSnapshot(futures, fallbackSnapshot);
                return fallbackSnapshot;
            }

            List<DexTokenInfo> tokens;
            try
            {
                var tasks = _cexSymbols.Select(symbol => BuildCexMarketTokenSafeAsync(symbol, futures)).ToArray();
                tokens = (await Task.WhenAll(tasks))
                    .Where(static token => token is not null)
                    .Select(static token => token!)
                    .ToList();
                _futuresRegionFallbackToSpotActive = false;
            }
            catch (Exception ex) when (futures && allowSpotFallback && IsBinanceFuturesRegionRestriction(ex))
            {
                var wasAlreadyFallback = _futuresRegionFallbackToSpotActive;
                _futuresRegionFallbackToSpotActive = true;
                StatusMessage = "Binance futures candles are region-restricted for this connection. Sniper is using spot market data as a fallback for CEX Futures analytics.";
                if (!wasAlreadyFallback)
                {
                    PushLog(StatusMessage, false);
                }

                RaiseSafetyProperties();
                var fallbackSnapshot = await BuildCexSnapshotWithSpotMarketDataAsync();
                CacheCexSnapshot(futures, fallbackSnapshot);
                return fallbackSnapshot;
            }

            ApplyObservationMetadata(tokens);
            var rankedTokens = RankSnapshotTokens(tokens);
            CacheCexSnapshot(futures, rankedTokens);
            return rankedTokens;
        }
        finally
        {
            _cexSnapshotLock.Release();
        }
    }

    private TimeSpan GetCexSnapshotCacheTtl() =>
        IsScalpProfile ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(8);

    private bool TryGetCachedCexSnapshot(bool futures, TimeSpan maxAge, out IReadOnlyList<DexTokenInfo> snapshot)
    {
        if (_cachedCexSnapshot.Count > 0 &&
            _cachedCexSnapshotIsFutures == futures &&
            DateTime.UtcNow - _cachedCexSnapshotUtc <= maxAge)
        {
            snapshot = _cachedCexSnapshot;
            return true;
        }

        snapshot = [];
        return false;
    }

    private void CacheCexSnapshot(bool futures, IReadOnlyList<DexTokenInfo> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        _cachedCexSnapshot = snapshot;
        _cachedCexSnapshotIsFutures = futures;
        _cachedCexSnapshotUtc = DateTime.UtcNow;
    }

    private async Task<IReadOnlyList<DexTokenInfo>> BuildCexSnapshotWithSpotMarketDataAsync()
    {
        if (_spotGateway is null)
        {
            return [];
        }

        var tasks = _cexSymbols.Select(symbol => BuildCexMarketTokenSafeAsync(symbol, futures: true, useSpotData: true)).ToArray();
        var tokens = (await Task.WhenAll(tasks))
            .Where(static token => token is not null)
            .Select(static token => token!)
            .ToList();

        ApplyObservationMetadata(tokens);
        return RankSnapshotTokens(tokens);
    }

    private async Task<DexTokenInfo?> BuildCexMarketTokenSafeAsync(string symbol, bool futures, bool useSpotData = false)
    {
        try
        {
            return await BuildCexMarketTokenAsync(symbol, futures, useSpotData);
        }
        catch (Exception ex) when (futures && !useSpotData && IsBinanceFuturesRegionRestriction(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            PushLog($"CEX snapshot skipped {symbol}: {ex.Message}", false);
            return null;
        }
    }

    private async Task<DexTokenInfo?> BuildCexMarketTokenAsync(string symbol, bool futures, bool useSpotData = false)
    {
        CryptoAITerminal.Core.Interfaces.IExchangeGateway? gatewayRef = useSpotData ? _spotGateway : futures ? _futuresGateway : _spotGateway;
        if (gatewayRef is null)
        {
            return null;
        }

        IReadOnlyList<DexOhlcvPoint> minuteCandles;
        IReadOnlyList<DexOhlcvPoint> hourCandles;

        if (futures && !useSpotData)
        {
            minuteCandles = await _futuresGateway!.GetCandlesAsync(symbol, "1M", 12);
            hourCandles = await _futuresGateway.GetCandlesAsync(symbol, "1H", 26);
        }
        else
        {
            minuteCandles = await _spotGateway!.GetCandlesAsync(symbol, "1M", 12);
            hourCandles = await _spotGateway.GetCandlesAsync(symbol, "1H", 26);
        }

        var marketData = GetLatestMarketData(symbol, futures && !useSpotData);
        var currentPrice = marketData?.LastPrice
            ?? minuteCandles.LastOrDefault()?.Close
            ?? hourCandles.LastOrDefault()?.Close
            ?? 0m;
        if (currentPrice <= 0m)
        {
            return null;
        }

        var baseAsset = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? symbol[..^4]
            : symbol;
        var minuteCloses = minuteCandles.Select(static candle => candle.Close).Where(static close => close > 0m).ToList();
        var hourCloses = hourCandles.Select(static candle => candle.Close).Where(static close => close > 0m).ToList();
        var price5mAgo = minuteCloses.Count >= 6 ? minuteCloses[^6] : minuteCloses.FirstOrDefault();
        var price1hAgo = hourCloses.Count >= 2 ? hourCloses[^2] : hourCloses.FirstOrDefault();
        var price24hAgo = hourCloses.Count >= 25 ? hourCloses[^25] : hourCloses.FirstOrDefault();
        var quoteVolume24h = hourCandles.TakeLast(Math.Min(24, hourCandles.Count)).Sum(static candle => candle.Close * candle.Volume);
        var liquidityProxy = Math.Max(quoteVolume24h * (futures ? 0.14m : 0.08m), futures ? 250000m : 100000m);

        return new DexTokenInfo
        {
            ChainId = futures ? "cex-futures" : "cex-spot",
            DexId = futures ? "binance-futures" : "binance-spot",
            PairAddress = symbol,
            TokenAddress = symbol,
            Symbol = baseAsset,
            Name = baseAsset,
            QuoteSymbol = "USDT",
            Url = $"https://www.binance.com/en/trade/{baseAsset}_USDT",
            PriceUsd = currentPrice,
            PriceNative = currentPrice,
            PriceChange5m = CalculatePercentChange(currentPrice, price5mAgo),
            PriceChange1h = CalculatePercentChange(currentPrice, price1hAgo),
            PriceChange24h = CalculatePercentChange(currentPrice, price24hAgo),
            Volume24h = quoteVolume24h,
            LiquidityUsd = liquidityProxy,
            MarketCap = 0m,
            LastUpdatedUtc = marketData?.Timestamp ?? DateTime.UtcNow,
            SignalSourceKind = futures ? "binance-futures" : "binance-spot",
            SignalSourceLabel = futures
                ? useSpotData ? "Binance spot fallback for USD-M futures scanner" : "Binance USD-M futures scanner"
                : "Binance spot scanner",
            SignalConfirmationLabel = futures
                ? useSpotData ? "CEX futures momentum via spot fallback" : "CEX futures momentum"
                : "CEX spot momentum",
            SignalSourceCount = 1,
            OwnershipSignalStatus = "CEX venue: on-chain ownership and honeypot checks are not applicable."
        };
    }

    private MarketData? GetLatestMarketData(string symbol, bool futures)
    {
        var cache = futures ? _futuresMarketBySymbol : _spotMarketBySymbol;
        lock (_marketDataCacheLock)
        {
            return cache.TryGetValue(symbol, out var marketData) ? marketData : null;
        }
    }

    private static bool IsBinanceFuturesRegionRestriction(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("restricted location", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("service unavailable from a restricted location", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("eligibility", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal CalculatePercentChange(decimal currentPrice, decimal referencePrice)
    {
        if (currentPrice <= 0m || referencePrice <= 0m)
        {
            return 0m;
        }

        return ((currentPrice - referencePrice) / referencePrice) * 100m;
    }

    private async Task HandleSignalMessageAsync(SniperSignalMessage message)
    {
        if (!IsArmed)
        {
            return;
        }

        LastFeedMessageLocal = DateTime.Now;
        LastSignalMessageKind = message.Kind.ToString();
        LastSignalNarrative = message.Message;

        switch (message.Kind)
        {
            case SniperSignalMessageKind.WarmSnapshot:
                await ProcessSnapshotAsync(message.Tokens, message.SourceLabel, allowDiscovery: ShouldDiscoverFromWarmSnapshot());
                StatusMessage = $"Warm start complete. Cached {message.Tokens.Count} pairs across {EnabledChainsSummary}. Stream detection is now active.";
                PushLog(StatusMessage, true);
                break;

            case SniperSignalMessageKind.SnapshotRefresh:
                await ProcessSnapshotAsync(message.Tokens, message.SourceLabel, allowDiscovery: false);
                break;

            case SniperSignalMessageKind.FreshBatch:
                await ProcessSnapshotAsync(message.Tokens, message.SourceLabel, allowDiscovery: true);
                break;

            case SniperSignalMessageKind.SourceStatus:
                StatusMessage = $"{message.Message} Waiting for enrichment and safety checks.";
                PushLog(StatusMessage, true);
                break;

            case SniperSignalMessageKind.Fault:
                var retryHint = message.RetryDelay is { } retryDelay
                    ? $" Retrying in {retryDelay.TotalSeconds:0.#}s."
                    : string.Empty;
                var isNonBlockingOnChainFallback =
                    message.Message.Contains("On-chain", StringComparison.OrdinalIgnoreCase) &&
                    message.Message.Contains("detector failed", StringComparison.OrdinalIgnoreCase);
                StatusMessage = isNonBlockingOnChainFallback
                    ? $"Signal stream fallback active: {message.Message}. Market-data tracking is still running.{retryHint}"
                    : $"Signal stream degraded: {message.Message}.{retryHint}";
                PushLog(StatusMessage, !isNonBlockingOnChainFallback);
                break;
        }

        RaiseSafetyProperties();
    }

    private enum DiscoveryOutcome
    {
        AlreadyTracked,
        Accepted,
        Rejected
    }

    private async Task ProcessSnapshotAsync(IReadOnlyList<DexTokenInfo> tokens, string sourceLabel, bool allowDiscovery)
    {
        ApplyObservationMetadata(tokens);
        var rankedTokens = RankSnapshotTokens(tokens);
        LastSnapshotSource = sourceLabel;
        LastSnapshotTokenCount = rankedTokens.Count;

        if (rankedTokens.Count == 0)
        {
            LastScanLocal = DateTime.Now;
            LastDiscoveredTokenCount = 0;
            LastAcceptedTokenCount = 0;
            LastRejectedTokenCount = 0;
            LastObservedUpdateCount = 0;
            LastTrackedPositionUpdateCount = 0;
            StatusMessage = $"Signal source returned no fresh pairs at {LastScanLocal:HH:mm:ss}.";
            PushLog($"Snapshot returned 0 pairs from {sourceLabel}.", false);
            return;
        }

        LastTrackedPositionUpdateCount = await RefreshTrackedPositionsAsync(rankedTokens);
        LastObservedUpdateCount = RefreshObservedCandidateQueues(rankedTokens);
        LastScanLocal = DateTime.Now;

        if (!allowDiscovery)
        {
            LastDiscoveredTokenCount = 0;
            LastAcceptedTokenCount = 0;
            LastRejectedTokenCount = 0;
            StatusMessage = $"{sourceLabel} Last snapshot {LastScanLocal:HH:mm:ss}.";
            PushLog($"Snapshot heartbeat from {sourceLabel}: {rankedTokens.Count} pairs, {LastObservedUpdateCount} queue updates, {LastTrackedPositionUpdateCount} position refreshes.", true);
            return;
        }

        var discoveredCount = 0;
        var acceptedCount = 0;
        var rejectedCount = 0;
        foreach (var token in rankedTokens)
        {
            switch (await HandleFreshPairAsync(token))
            {
                case DiscoveryOutcome.Accepted:
                    discoveredCount++;
                    acceptedCount++;
                    break;
                case DiscoveryOutcome.Rejected:
                    discoveredCount++;
                    rejectedCount++;
                    break;
            }
        }

        LastDiscoveredTokenCount = discoveredCount;
        LastAcceptedTokenCount = acceptedCount;
        LastRejectedTokenCount = rejectedCount;

        StatusMessage = discoveredCount > 0
            ? $"Detected {discoveredCount} new pair(s) across {EnabledChainsSummary} at {LastScanLocal:HH:mm:ss}. Accepted {acceptedCount}, filtered {rejectedCount}, snapshot size {rankedTokens.Count}."
            : $"Snapshot refreshed across {EnabledChainsSummary} at {LastScanLocal:HH:mm:ss}. Watching {FreshPairs.Count} fresh pair(s); no new addresses in this pass.";
    }

    private async Task<DiscoveryOutcome> HandleFreshPairAsync(DexTokenInfo token)
    {
        if (IsTokenAlreadyTracked(token))
        {
            return DiscoveryOutcome.AlreadyTracked;
        }

        var (passed, reason, risk) = EvaluateToken(token);
        var candidate = new SniperCandidateViewModel(token, reason, passed)
        {
            Status = passed ? "Accepted" : "Filtered"
        };
        candidate.RiskScore = risk.Score;
        candidate.RiskBand = risk.Band;
        candidate.RiskSummary = risk.Summary;
        candidate.RiskFlags = risk.Flags;
        candidate.StrategyLabel = risk.StrategyLabel;
        candidate.StrategySummary = risk.StrategySummary;
        candidate.DexQualityLabel = risk.DexQualityLabel;
        candidate.OwnershipSignalLabel = risk.OwnershipSignalLabel;
        candidate.WatchlistPriorityLabel = risk.WatchlistLabel;
        candidate.PairAgeLabel = risk.ObservedAgeLabel;
        candidate.SignalSourceLabel = token.SignalSourceLabel;
        candidate.SignalConfirmationLabel = token.SignalConfirmationLabel;
        candidate.SignalPriorityScore = risk.PriorityScore;
        UpdateLatestStructure(risk);
        ApplyExecutionGuard(candidate, risk);
        UpdateLatestRisk(candidate);
        UpdateLatestExecution(candidate);

        ApplyRankScore(candidate);

        FreshPairs.Insert(0, candidate);
            TrimCollection(FreshPairs, 200);

        if (passed)
        {
            AcceptedPairs.Insert(0, candidate);
            SortAcceptedPairsByRank();
            TrimCollection(AcceptedPairs, 120);
            PushLog($"Accepted {candidate.DisplayName}: {reason} | Rank {candidate.RankScoreLabel}", true);

            StartAiVerdict(candidate);

            if (CanExecuteAutoBuy)
            {
                await BuyCandidateAsync(candidate);
            }

            return DiscoveryOutcome.Accepted;
        }

        PushLog($"Rejected {candidate.DisplayName}: {reason}", false);
        return DiscoveryOutcome.Rejected;
    }

    private static void ApplyRankScore(SniperCandidateViewModel candidate)
    {
        var rank = SniperRankingModel.Compute(candidate.TokenInfo);
        candidate.RankScore = rank.Total;
        candidate.RankScoreBand = rank.Band;
        candidate.RankScoreLabel = rank.Label;
    }

    private void StartAiVerdict(SniperCandidateViewModel candidate)
    {
        if (!EnableAiTokenVerdict) return;
        if (candidate.HasAiVerdict || candidate.AiAssessmentRunning) return;

        candidate.AiAssessmentRunning = true;
        RunLoggedAsync(() => AssessCandidateAiAsync(candidate), $"AI verdict for {candidate.DisplayName}");
    }

    private async Task AssessCandidateAiAsync(SniperCandidateViewModel candidate)
    {
        try
        {
            var summary = candidate.SecurityScanComplete ? candidate.SecurityChecksSummary : null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var verdict = await _tokenAiService
                .AssessAsync(candidate.TokenInfo, summary, cts.Token)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                candidate.AiVerdict = verdict;
                candidate.AiAssessmentRunning = false;
            });

            PushLog(
                $"AI verdict for {candidate.DisplayName}: {verdict.Verdict} (risk {verdict.RiskScore}/100) — {verdict.Source}",
                verdict.Verdict is not ("AVOID" or "RISKY"));
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => candidate.AiAssessmentRunning = false);
            throw; // RunLoggedAsync logs it
        }
    }

    private void SortAcceptedPairsByRank()
    {
        if (AcceptedPairs.Count < 2) return;

        var ordered = AcceptedPairs
            .OrderByDescending(static c => c.RankScore)
            .ThenBy(static c => c.RiskScore)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var target = ordered[i];
            var current = AcceptedPairs[i];
            if (!ReferenceEquals(target, current))
            {
                var oldIndex = AcceptedPairs.IndexOf(target);
                if (oldIndex >= 0 && oldIndex != i)
                {
                    AcceptedPairs.Move(oldIndex, i);
                }
            }
        }
    }

    private bool ShouldDiscoverFromWarmSnapshot()
    {
        var enabledChains = GetEnabledChainIds();
        return SelectedStrategyMode.Equals("Launch Sniper", StringComparison.OrdinalIgnoreCase) ||
               (enabledChains.Count == 1 && enabledChains.Contains("bsc", StringComparer.OrdinalIgnoreCase));
    }

    private bool IsTokenAlreadyTracked(DexTokenInfo token)
    {
        static bool Matches(SniperCandidateViewModel candidate, DexTokenInfo tokenInfo)
        {
            return string.Equals(candidate.TokenInfo.ChainId, tokenInfo.ChainId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(candidate.TokenInfo.TokenAddress, tokenInfo.TokenAddress, StringComparison.OrdinalIgnoreCase);
        }

        return FreshPairs.Any(candidate => Matches(candidate, token)) ||
               AcceptedPairs.Any(candidate => Matches(candidate, token)) ||
               OpenPositions.Any(candidate => Matches(candidate, token)) ||
               PaperPositions.Any(candidate => Matches(candidate, token));
    }

    private int RefreshObservedCandidateQueues(IReadOnlyList<DexTokenInfo> tokens)
    {
        var byKey = tokens.ToDictionary(
            token => BuildTokenKey(token.ChainId, token.TokenAddress),
            StringComparer.OrdinalIgnoreCase);

        var freshUpdates = RefreshObservedCollection(FreshPairs, byKey, preserveAcceptedState: false);
        var acceptedUpdates = RefreshObservedCollection(AcceptedPairs, byKey, preserveAcceptedState: true);
        return freshUpdates + acceptedUpdates;
    }

    private int RefreshObservedCollection(
        ObservableCollection<SniperCandidateViewModel> candidates,
        IReadOnlyDictionary<string, DexTokenInfo> byKey,
        bool preserveAcceptedState)
    {
        var updatedCount = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var tokenKey = BuildTokenKey(candidate.TokenInfo.ChainId, candidate.TokenInfo.TokenAddress);
            if (!byKey.TryGetValue(tokenKey, out var latestToken))
            {
                continue;
            }

            candidate.UpdateFromToken(latestToken);

            var (passed, reason, risk) = EvaluateToken(latestToken);
            candidate.RiskScore = risk.Score;
            candidate.RiskBand = risk.Band;
            candidate.RiskSummary = risk.Summary;
            candidate.RiskFlags = risk.Flags;
            candidate.StrategyLabel = risk.StrategyLabel;
            candidate.StrategySummary = risk.StrategySummary;
            candidate.DexQualityLabel = risk.DexQualityLabel;
            candidate.OwnershipSignalLabel = risk.OwnershipSignalLabel;
            candidate.WatchlistPriorityLabel = risk.WatchlistLabel;
            candidate.PairAgeLabel = risk.ObservedAgeLabel;
            candidate.SignalSourceLabel = latestToken.SignalSourceLabel;
            candidate.SignalConfirmationLabel = latestToken.SignalConfirmationLabel;
            candidate.SignalPriorityScore = risk.PriorityScore;
            UpdateLatestStructure(risk);
            ApplyExecutionGuard(candidate, risk);

            if (!candidate.WasBought)
            {
                candidate.Status = preserveAcceptedState
                    ? (passed ? $"Accepted | updated {DateTime.Now:HH:mm:ss}" : $"Needs review | {DateTime.Now:HH:mm:ss}")
                    : (passed ? $"Observed | candidate {DateTime.Now:HH:mm:ss}" : $"Observed | filtered {DateTime.Now:HH:mm:ss}");
            }

            updatedCount++;
        }

        return updatedCount;
    }

    private void ApplyObservationMetadata(IReadOnlyList<DexTokenInfo> tokens)
    {
        var watchFragments = ParseFragments(WatchlistText);
        foreach (var token in tokens)
        {
            var key = BuildTokenKey(token.ChainId, token.TokenAddress);
            if (!_firstSeenByTokenKey.TryGetValue(key, out var firstSeenUtc))
            {
                firstSeenUtc = DateTime.UtcNow;
                _firstSeenByTokenKey[key] = firstSeenUtc;
            }

            token.ObservedFirstSeenUtc = firstSeenUtc;
            token.DexQualityScore = GetDexQualityScore(token.DexId);
            token.DexQualityLabel = BuildDexQualityLabel(token.DexQualityScore, token.DexId);
            token.SignalSourceKind = string.IsNullOrWhiteSpace(token.SignalSourceKind) ? "indexed" : token.SignalSourceKind;
            token.SignalSourceLabel = string.IsNullOrWhiteSpace(token.SignalSourceLabel) ? "Indexed snapshot" : token.SignalSourceLabel;
            token.SignalSourceCount = token.SignalSourceCount <= 0 ? 1 : token.SignalSourceCount;
            token.SignalConfirmationLabel = string.IsNullOrWhiteSpace(token.SignalConfirmationLabel)
                ? $"Single-source via {token.SignalSourceLabel}"
                : token.SignalConfirmationLabel;

            var matchedWatch = GetMatchingFragment(token, watchFragments);
            token.WatchlistMatched = !string.IsNullOrWhiteSpace(matchedWatch);
            token.WatchlistMatchText = token.WatchlistMatched
                ? $"Watchlist priority: matched '{matchedWatch}'."
                : "Watchlist priority: no fragment match.";
            token.OwnershipSignalStatus = "Ownership signals unavailable from current feed; lock/burn/renounce needs a dedicated on-chain source.";
        }
    }

    private List<DexTokenInfo> RankSnapshotTokens(IReadOnlyList<DexTokenInfo> tokens)
    {
        return tokens
            .OrderByDescending(token => IsVeryFresh(token))
            .ThenByDescending(token => token.WatchlistMatched)
            .ThenByDescending(token => StrategyPriority(token))
            .ThenBy(token => GetObservedAgeMinutes(token))
            .ThenByDescending(token => token.DexQualityScore)
            .ThenByDescending(token => token.LiquidityUsd)
            .ThenByDescending(token => token.Volume24h)
            .ToList();
    }

    private static string BuildTokenKey(string? chainId, string? tokenAddress)
    {
        return $"{chainId?.Trim().ToLowerInvariant()}::{tokenAddress?.Trim().ToLowerInvariant()}";
    }

    private static bool IsCexToken(DexTokenInfo token)
    {
        return token.ChainId.Equals("cex-spot", StringComparison.OrdinalIgnoreCase) ||
               token.ChainId.Equals("cex-futures", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetNetworkNameForChain(string? chainId)
    {
        return chainId?.Trim().ToLowerInvariant() switch
        {
            "bsc" => "BSC",
            "ethereum" => "Ethereum",
            "base" => "Base",
            "solana" => "Solana",
            "tron" => "Tron",
            _ => "BSC"
        };
    }

    private static string GetChainIdForNetwork(string? networkName)
    {
        return networkName?.Trim() switch
        {
            "BSC" => "bsc",
            "Ethereum" => "ethereum",
            "Base" => "base",
            "Solana" => "solana",
            "Tron" => "tron",
            _ => "bsc"
        };
    }

    private IReadOnlyList<string> GetSupportedStableQuoteSymbolsForChain(string? chainId)
    {
        return DexQuoteAssetCatalog.GetOptions(GetNetworkNameForChain(chainId))
            .Where(static option => !option.IsNative && IsStableQuoteSymbol(option.Symbol))
            .Select(static option => option.Symbol.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string? GetPreferredStableQuoteSymbolForChain(string? chainId)
    {
        var supported = GetSupportedStableQuoteSymbolsForChain(chainId);
        if (supported.Count == 0)
        {
            return null;
        }

        return supported.Contains(PreferredStableQuoteSymbol, StringComparer.OrdinalIgnoreCase)
            ? supported.First(symbol => string.Equals(symbol, PreferredStableQuoteSymbol, StringComparison.OrdinalIgnoreCase))
            : supported[0];
    }

    private static int CountFragments(string? source)
    {
        return string.IsNullOrWhiteSpace(source)
            ? 0
            : source
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length;
    }

    private (bool Passed, string Reason, RiskEvaluation Risk) EvaluateToken(DexTokenInfo token)
    {
        var risk = EvaluateRisk(token);
        var observedAgeMinutes = GetObservedAgeMinutes(token);
        var isCexToken = IsCexToken(token);

        var enabledChains = GetEnabledChainIds();
        if (!isCexToken && !enabledChains.Contains(token.ChainId))
        {
            return (false, $"Chain {token.ChainId} is outside the enabled sniper scope.", risk);
        }

        if (token.LiquidityUsd < MinLiquidityUsd)
        {
            return (false, $"Liquidity ${token.LiquidityUsd:N0} is below minimum ${MinLiquidityUsd:N0}.", risk);
        }

        if (MaxLiquidityUsd > 0 && token.LiquidityUsd > MaxLiquidityUsd)
        {
            return (false, $"Liquidity ${token.LiquidityUsd:N0} is above cap ${MaxLiquidityUsd:N0}.", risk);
        }

        if (token.Volume24h < MinVolume24hUsd)
        {
            return (false, $"24h volume ${token.Volume24h:N0} is below minimum ${MinVolume24hUsd:N0}.", risk);
        }

        if (MinPairAgeMinutes > 0m && observedAgeMinutes < MinPairAgeMinutes)
        {
            return (false, $"Pair age {observedAgeMinutes:0.#}m is below minimum {MinPairAgeMinutes:0.#}m.", risk);
        }

        if (MaxPairAgeMinutes > 0m && observedAgeMinutes > MaxPairAgeMinutes)
        {
            return (false, $"Pair age {observedAgeMinutes:0.#}m is above cap {MaxPairAgeMinutes:0.#}m.", risk);
        }

        if (!PassesMomentumGate(token, out var momentumReason))
        {
            return (false, momentumReason, risk);
        }

        if (MaxMarketCapUsd > 0 && token.MarketCap > 0 && token.MarketCap > MaxMarketCapUsd)
        {
            return (false, $"Market cap ${token.MarketCap:N0} exceeds cap ${MaxMarketCapUsd:N0}.", risk);
        }

        var haystack = $"{token.Symbol} {token.Name}".ToLowerInvariant();
        var blockedWord = BlacklistText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(word => haystack.Contains(word.ToLowerInvariant(), StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(blockedWord))
        {
            return (false, $"Matched blacklist token fragment '{blockedWord}'.", risk);
        }

        var whitelist = ParseFragments(WhitelistText);
        if (whitelist.Count > 0)
        {
            var matchedWhitelist = whitelist.FirstOrDefault(word => haystack.Contains(word, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(matchedWhitelist))
            {
                return (false, "Whitelist is active and this pair does not match any approved fragment.", risk);
            }
        }

        if (!isCexToken)
        {
            var chainProfile = GetChainProfile(token.ChainId);
            if (RequireBnbQuote &&
                !chainProfile.AllowedQuoteSymbols.Contains(token.QuoteSymbol, StringComparer.OrdinalIgnoreCase))
            {
                return (false, $"Quote asset {token.QuoteSymbol} is not allowed while native quote filter is active for {chainProfile.DisplayName}.", risk);
            }

            if (PreferStableQuote &&
                !MatchesPreferredStableQuote(token.ChainId, token.QuoteSymbol))
            {
                var supportedStableRoutes = GetSupportedStableQuoteSymbolsForChain(token.ChainId);
                var stableRouteLabel = supportedStableRoutes.Count == 0
                    ? "no supported stable routes"
                    : string.Join(", ", supportedStableRoutes);
                return (false, $"Quote asset {token.QuoteSymbol} is outside the stable-first route set for {GetChainProfile(token.ChainId).DisplayName}. Supported stable routes: {stableRouteLabel}.", risk);
            }
        }

        var strategyDecision = EvaluateStrategy(token);
        if (!strategyDecision.Passed)
        {
            return (false, strategyDecision.Reason, risk);
        }

        if (MaxRiskScore > 0 && risk.Score > MaxRiskScore)
        {
            return (false, $"Risk score {risk.Score}/100 exceeds cap {MaxRiskScore}. {risk.Summary}", risk);
        }

        return (true, $"Liquidity ${token.LiquidityUsd:N0}, volume ${token.Volume24h:N0}, 5m momentum {token.PriceChange5m:N2}%, market cap ${token.MarketCap:N0}. {strategyDecision.Summary} {risk.Summary}", risk);
    }

    private sealed class SniperSettingsSnapshot
    {
        public bool AutoBuyEnabled { get; set; }
        public decimal BuyAmountBnb { get; set; }
        public decimal MinLiquidityUsd { get; set; }
        public decimal MaxLiquidityUsd { get; set; }
        public decimal MinVolume24hUsd { get; set; }
        public decimal MinMomentum5m { get; set; }
        public decimal MaxMarketCapUsd { get; set; }
        public int MaxRiskScore { get; set; }
        public decimal MinPairAgeMinutes { get; set; }
        public decimal MaxPairAgeMinutes { get; set; }
        public decimal LaunchMaxPairAgeMinutes { get; set; }
        public decimal WarmPairMinAgeMinutes { get; set; }
        public string? SelectedStrategyMode { get; set; }
        public decimal MaxVolumeToLiquidityRatio { get; set; }
        public decimal MaxMarketCapToLiquidityRatio { get; set; }
        public bool EnableExecutionGuard { get; set; }
        public bool BlockSuspectedHoneypots { get; set; }
        public bool PaperTradingEnabled { get; set; }
        public bool AutoTakeProfitEnabled { get; set; }
        public decimal TakeProfitPercent { get; set; }
        public bool AutoStopLossEnabled { get; set; }
        public decimal StopLossPercent { get; set; }
        public bool AutoTrailingStopEnabled { get; set; }
        public decimal TrailingStopPercent { get; set; }
        public bool PartialTakeProfitEnabled { get; set; }
        public decimal PartialTakeProfitTriggerPercent { get; set; }
        public decimal PartialTakeProfitSellPercent { get; set; }
        public bool BreakEvenEnabled { get; set; }
        public decimal BreakEvenTriggerPercent { get; set; }
        public decimal MaxSimulatedBuyTaxPercent { get; set; }
        public decimal MaxSimulatedSellTaxPercent { get; set; }
        public int CooldownSeconds { get; set; }
        public int MaxSimultaneousPositions { get; set; }
        public int MaxBuysPerSession { get; set; }
        public decimal MaxDailyLiveLossNative { get; set; }
        public decimal MaxExposurePerChainNative { get; set; }
        public decimal MaxExposurePerWalletNative { get; set; }
        public int MaxConsecutiveLiveLosses { get; set; }
        public decimal HardCapTotalLiveExposureNative { get; set; }
        public decimal TinyDryRunCapNative { get; set; }
        public string? SelectedScanVenue { get; set; }
        public string? SelectedTradingProfile { get; set; }
        public string? SelectedScalpPreset { get; set; }
        public int FuturesLeverage { get; set; }
        public string? SelectedFuturesBias { get; set; }
        public bool RequireBnbQuote { get; set; }
        public bool PreferStableQuote { get; set; }
        public string? EnabledChainsText { get; set; }
        public string? WhitelistText { get; set; }
        public string? WatchlistText { get; set; }
        public string? BlacklistText { get; set; }
        public string? SelectedPresetName { get; set; }
        public decimal SlippagePercent { get; set; }
    }

    private sealed record SniperChainProfile(
        string DisplayName,
        bool SupportsLiveExecution,
        IReadOnlyList<string> AllowedQuoteSymbols);

}

