using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.Gateway.Bybit;
using CryptoAITerminal.Gateway.KuCoin;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Gateway.OKX;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.WhaleTracker;
using CryptoAITerminal.OrderRouter;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class MainWindowViewModel : ReactiveObject, IDisposable
{
    private static readonly string[] DefaultSymbols = ["BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT", "ADAUSDT", "DOGEUSDT", "AVAXUSDT", "LINKUSDT", "TRXUSDT", "LTCUSDT"];
    private static readonly string[] KnownQuoteAssets = ["USDT", "USDC", "FDUSD", "TUSD", "BUSD", "BTC", "ETH", "BNB"];
    private static readonly string CustomMarketsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "custom-markets.json");

    private readonly List<string> _customMarketSymbols;
    private string _newMarketSymbol = "";
    private string _marketsStatus = "";

    private readonly BinanceGateway _gateway;
    private readonly BinanceFuturesGateway _futuresGateway;
    private readonly MarketOrderRouter _router;
    private readonly RiskManager.RiskManager _riskManager;
    private decimal _riskLimitPositionInput = 1000m;
    private decimal _riskLimitDailyLossInput = 500m;
    // Last budget level shown, so a toast fires only when the level worsens (not every refresh).
    private RiskManager.RiskBudgetLevel _lastRiskBudgetLevel = RiskManager.RiskBudgetLevel.Ok;
    // Stored as fields so the Portfolio Rebalancer and Funding Arb can query all gateways
    private readonly Core.Interfaces.IExchangeGateway _bybitSpotGateway     = null!;
    private readonly Core.Interfaces.IExchangeGateway _okxSpotGateway       = null!;
    private readonly Core.Interfaces.IExchangeGateway _bybitFuturesGateway  = null!;
    private readonly Core.Interfaces.IExchangeGateway _okxFuturesGateway    = null!;
    private readonly Core.Interfaces.IExchangeGateway _kucoinSpotGateway    = null!;
    private readonly Core.Interfaces.IExchangeGateway _kucoinFuturesGateway = null!;
    private readonly DispatcherTimer _orderBookTimer;
    private readonly SemaphoreSlim _orderBookRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _candleRefreshLock = new(1, 1);
    private readonly IDisposable _marketDataSubscription;
    private readonly IDisposable _futuresMarketDataSubscription;
    private readonly IDisposable _futuresAccountStateSubscription;
    private readonly IDisposable _futuresOrderUpdateSubscription;
    private readonly IDisposable _futuresTradeUpdateSubscription;
    private readonly List<IDisposable> _commandErrorSubscriptions = [];
    private IDisposable? _marketDataSubscription2;   // composite rule engine feed

    private int _selectedTabIndex;
    private MarketData? _currentMarketData;
    private CexMarketItemViewModel? _selectedMarket;
    private decimal _tradeQuantity = 0.001m;
    private decimal _limitPrice;
    private decimal _takeProfitPrice;
    private decimal _stopLossPrice;
    private string _selectedTimeInForce = "DAY";
    private string _logMessages = string.Empty;
    private bool _isMarketLoading = true;
    private decimal _availableBalanceUsdt = 10000m;
    private decimal _positionQuantity;
    private decimal _averageEntryPrice;
    private decimal _realizedPnl;
    private string _selectedTradeTimeframe = "1M";
    private string _tradeIdeaTitle = "Waiting for live market context";
    private string _tradeIdeaSummary = "Connect to a symbol to see a suggested entry zone, stop and target.";
    private string _suggestedEntryLabel = "--";
    private string _suggestedStopLabel = "--";
    private string _suggestedTargetLabel = "--";
    private string _preferredBookSide = "BUY";
    private bool _isLadderCenterLocked = true;
    private int _ladderManualOffsetTicks;
    private TradingVenueMode _selectedTradingVenue = TradingVenueMode.Cex;
    private string _selectedChartTool = "Cursor";
    private ChartToolPhase _chartToolPhase = ChartToolPhase.None;
    private bool _focusLatestCandlesOnNextRefresh = true;
    private int _selectedTradingBottomTabIndex;
    private int _chartClearDrawingsVersion;
    private int _chartResetViewVersion;
    private bool _showChartVwap          = true;
    private bool _showChartVolumeProfile = true;
    private decimal _selectedLadderPrice;
    private decimal _priceStep = 0.50m;
    private bool _pendingGlobalCexSizingApply = true;
    private string _selectedShellSection = "dashboard";
    private string _selectedOrderSide = "BUY";
    private string _selectedOrderType = "Limit";
    private decimal _slippageTolerancePercent = 0.50m;
    private string _marketsSearchText = string.Empty;
    private string _selectedMarketSortMode = "Momentum";
    private bool _showFavoriteMarketsOnly;
    private bool _isRefreshingMarketExplorerCollections;
    private string _selectedCexMarketMode = "Spot";
    private string _selectedFuturesExchange = "Binance";
    private IReadOnlyDictionary<string, IExchangeGateway>? _futuresGatewaysMap;
    private int _manualFuturesLeverage = 3;
    private string _manualFuturesMarginMode = "Cross";
    private string _selectedTradingProfile = "Balanced";
    private string _selectedScalpPreset = "Standard";
    private decimal _currentFuturesLiquidationPrice;
    private decimal _currentFuturesMarkPrice;
    private decimal _currentFuturesExchangeUnrealizedPnl;
    private CancellationTokenSource? _manualModeRefreshCts;
    private bool _isManualModeSyncScheduled;
    private readonly object _futuresUiUpdateLock = new();
    private MarketData? _pendingFuturesUiMarketData;
    private bool _isFuturesUiUpdateScheduled;
    private DateTime _lastFuturesUiMarketDataUiUpdateUtc = DateTime.MinValue;
    private readonly object _workingOrderEvaluationLock = new();
    private bool _isWorkingOrderEvaluationScheduled;
    private bool _isWorkingOrderEvaluationRunning;
    private QuickBacktestSnapshot _quickBacktestSnapshot = QuickBacktestSnapshot.Empty;
    private readonly UiLocalizationService _localization = UiLocalizationService.Instance;
    private readonly Services.BookWallSettingsStore _wallSettingsStore = new();
    private string _aiAssistantDraftPrompt = string.Empty;
    private string _aiAssistantStatusLabel = "LOCAL CRYPTO COPILOT";
    private string _aiAssistantStatusBrush = "#21E6C1";
    private AiVisualCardViewModel? _selectedAiVisual;
    private AiContextVenueMode _selectedAiContextVenueMode = AiContextVenueMode.Auto;
    private bool _aiIncludeMarketContext = true;
    private bool _aiIncludeRiskContext = true;
    private bool _aiIncludeDexContext = true;
    private bool _aiIncludeSniperContext = true;
    private bool _aiIncludeVisualContext = true;
    private string _aiPromptPresetAsset = string.Empty;
    private string _aiCustomPresetName = string.Empty;
    private string _selectedAiPromptTradeStyle = string.Empty;
    private string _selectedAiPromptHorizon = string.Empty;
    private string _selectedAiPromptRiskProfile = string.Empty;
    private string _selectedAiPromptFocus = string.Empty;
    private static readonly Dictionary<string, Bitmap> AiStudioBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions AiPresetJsonOptions = new() { WriteIndented = true };

    public MainWindowViewModel()
    {
        _customMarketSymbols = LoadCustomMarketSymbols();
        foreach (var symbol in DefaultSymbols.Concat(_customMarketSymbols))
        {
            var market = new CexMarketItemViewModel(symbol);
            ConfigureWallSettings(market);
            market.PropertyChanged += OnMarketItemPropertyChanged;
            Markets.Add(market);
        }

        RefreshMarketExplorerCollections();

        // ── Load API credentials (file takes second priority, env vars override) ──
        var credResult = CredentialsService.Load();
        var creds      = credResult.Credentials;

        _binanceCredSource = credResult.BinanceSource;
        _bybitCredSource  = credResult.BybitSource;
        _okxCredSource    = credResult.OkxSource;
        _kucoinCredSource = credResult.KucoinSource;
        _loadedBinanceKey = creds.BinanceKey;
        _loadedBybitKey   = creds.BybitKey;
        _loadedOkxKey     = creds.OkxKey;
        _loadedKucoinKey  = creds.KucoinKey;

        // Pre-fill input fields only with file-stored values (never echo env vars into inputs)
        if (credResult.BinanceSource == CredentialsService.CredentialSource.File)
        {
            _binanceKeyInput    = creds.BinanceKey;
            _binanceSecretInput = creds.BinanceSecret;
        }
        if (credResult.BybitSource == CredentialsService.CredentialSource.File)
        {
            _bybitKeyInput    = creds.BybitKey;
            _bybitSecretInput = creds.BybitSecret;
        }
        if (credResult.OkxSource == CredentialsService.CredentialSource.File)
        {
            _okxKeyInput        = creds.OkxKey;
            _okxSecretInput     = creds.OkxSecret;
            _okxPassphraseInput = creds.OkxPassphrase;
        }
        if (credResult.KucoinSource == CredentialsService.CredentialSource.File)
        {
            _kucoinKeyInput        = creds.KucoinKey;
            _kucoinSecretInput     = creds.KucoinSecret;
            _kucoinPassphraseInput = creds.KucoinPassphrase;
        }

        var binanceApiKey    = creds.BinanceKey;
        var binanceApiSecret = creds.BinanceSecret;
        var bybitApiKey      = creds.BybitKey;
        var bybitApiSecret   = creds.BybitSecret;
        var okxApiKey        = creds.OkxKey;
        var okxApiSecret     = creds.OkxSecret;
        var okxApiPassphrase = creds.OkxPassphrase;

        _gateway = new BinanceGateway(DefaultSymbols.Concat(_customMarketSymbols));
        _futuresGateway = new BinanceFuturesGateway(DefaultSymbols, binanceApiKey, binanceApiSecret);
        _router = new MarketOrderRouter(_gateway);
        _riskManager = new RiskManager.RiskManager(maxPositionSizeUsd: 1000, maxDailyLossUsd: 500);
        ApplyRiskLimitsCommand = ReactiveCommand.Create(ApplyRiskLimits);
        ResetDailyRiskCommand = ReactiveCommand.Create(ResetDailyRisk);

        var bybitSpot = new BybitGateway(DefaultSymbols, bybitApiKey, bybitApiSecret);
        var bybitFutures = new BybitFuturesGateway(DefaultSymbols, bybitApiKey, bybitApiSecret);
        var okxSpot = new OKXGateway(DefaultSymbols, okxApiKey, okxApiSecret, okxApiPassphrase);
        var okxFutures = new OKXFuturesGateway(DefaultSymbols, okxApiKey, okxApiSecret, okxApiPassphrase);

        var kucoinApiKey        = creds.KucoinKey;
        var kucoinApiSecret     = creds.KucoinSecret;
        var kucoinApiPassphrase = creds.KucoinPassphrase;
        var kucoinSpot    = new KucoinGateway(DefaultSymbols, kucoinApiKey, kucoinApiSecret, kucoinApiPassphrase);
        var kucoinFutures = new KucoinFuturesGateway(DefaultSymbols, kucoinApiKey, kucoinApiSecret, kucoinApiPassphrase);

        // Store gateways for Portfolio Rebalancer and Funding Arb Bot
        _bybitSpotGateway     = bybitSpot;
        _okxSpotGateway       = okxSpot;
        _bybitFuturesGateway  = bybitFutures;
        _okxFuturesGateway    = okxFutures;
        _kucoinSpotGateway    = kucoinSpot;
        _kucoinFuturesGateway = kucoinFutures;

        // Multi-exchange futures gateway map — used by ActiveFuturesGateway property.
        _futuresGatewaysMap = new Dictionary<string, IExchangeGateway>(StringComparer.OrdinalIgnoreCase)
        {
            ["Binance"] = _futuresGateway,
            ["Bybit"]   = bybitFutures,
            ["OKX"]     = okxFutures,
            ["KuCoin"]  = kucoinFutures,
        };

        WalletVM = new WalletWorkspaceViewModel(AddLog);
        AIBotVM = new AIBotViewModel(_gateway, _futuresGateway, bybitSpot, bybitFutures, okxSpot, okxFutures, kucoinSpot, kucoinFutures);
        AiTraderVM = new AiTraderViewModel(
            new Dictionary<string, IExchangeGateway>(StringComparer.OrdinalIgnoreCase)
            {
                ["Binance"] = _gateway,
                ["Bybit"]   = bybitSpot,
                ["OKX"]     = okxSpot,
                ["KuCoin"]  = kucoinSpot,
            },
            new Dictionary<string, IExchangeGateway>(StringComparer.OrdinalIgnoreCase)
            {
                ["Binance"] = _futuresGateway,
                ["Bybit"]   = bybitFutures,
                ["OKX"]     = okxFutures,
                ["KuCoin"]  = kucoinFutures,
            },
            dexGatewayAccessor: () => WalletVM.ActiveDexGateway,
            dexLiveAllowed: () => WalletVM.GlobalLiveExecutionEnabled);
        GridBotVM = new GridBotViewModel(_gateway, _futuresGateway);
        DcaBotVM = new DcaBotViewModel(_gateway, _bybitSpotGateway, _okxSpotGateway, _kucoinSpotGateway);
        DexTradingVM = new DexTradingViewModel(WalletVM);
        SniperVM = new SniperViewModel(WalletVM, _gateway, _futuresGateway, DefaultSymbols);
        _telegram = new TelegramNotificationService();
        _discord  = new DiscordWebhookNotificationService();
        _ntfy     = new NtfyNotificationService();
        _email    = new EmailNotificationService();
        var alertService = new AlertService(_telegram, _discord, _ntfy, _email);
        alertService.SubscribeToStream(_gateway.MarketDataStream);
        AlertsVM = new AlertsViewModel(alertService, _telegram, _discord, _ntfy, _email);
        AlertsVM.ToastRequested += ShowToast;

        // ── API Credentials commands ──────────────────────────────────────────
        SaveBinanceCredentialsCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                CredentialsService.SaveBinance(_binanceKeyInput.Trim(), _binanceSecretInput.Trim());
                BinanceSaveStatus = "✓ Saved — restart to apply";
            }
            catch (Exception ex) { BinanceSaveStatus = $"Error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        SaveBybitCredentialsCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                CredentialsService.SaveBybit(_bybitKeyInput.Trim(), _bybitSecretInput.Trim());
                BybitSaveStatus = "✓ Saved — restart to apply";
            }
            catch (Exception ex) { BybitSaveStatus = $"Error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        SaveOkxCredentialsCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                CredentialsService.SaveOkx(
                    _okxKeyInput.Trim(), _okxSecretInput.Trim(), _okxPassphraseInput.Trim());
                OkxSaveStatus = "✓ Saved — restart to apply";
            }
            catch (Exception ex) { OkxSaveStatus = $"Error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        ToggleBinanceHelpCommand = ReactiveCommand.Create(
            () => { IsBinanceHelpVisible = !_isBinanceHelpVisible; },
            outputScheduler: App.UiScheduler);

        ToggleBybitHelpCommand = ReactiveCommand.Create(
            () => { IsBybitHelpVisible = !_isBybitHelpVisible; },
            outputScheduler: App.UiScheduler);

        ToggleOkxHelpCommand = ReactiveCommand.Create(
            () => { IsOkxHelpVisible = !_isOkxHelpVisible; },
            outputScheduler: App.UiScheduler);

        ToggleBinanceKeyVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingBinanceKey = !_isShowingBinanceKey; },
            outputScheduler: App.UiScheduler);

        ToggleBinanceSecretVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingBinanceSecret = !_isShowingBinanceSecret; },
            outputScheduler: App.UiScheduler);

        ToggleBybitKeyVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingBybitKey = !_isShowingBybitKey; },
            outputScheduler: App.UiScheduler);

        ToggleBybitSecretVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingBybitSecret = !_isShowingBybitSecret; },
            outputScheduler: App.UiScheduler);

        ToggleOkxKeyVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingOkxKey = !_isShowingOkxKey; },
            outputScheduler: App.UiScheduler);

        ToggleOkxSecretVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingOkxSecret = !_isShowingOkxSecret; },
            outputScheduler: App.UiScheduler);

        ToggleOkxPassphraseVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingOkxPassphrase = !_isShowingOkxPassphrase; },
            outputScheduler: App.UiScheduler);

        SaveKucoinCredentialsCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                CredentialsService.SaveKucoin(
                    _kucoinKeyInput.Trim(), _kucoinSecretInput.Trim(), _kucoinPassphraseInput.Trim());
                KucoinSaveStatus = "✓ Saved — restart to apply";
            }
            catch (Exception ex) { KucoinSaveStatus = $"Error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        ToggleKucoinHelpCommand = ReactiveCommand.Create(
            () => { IsKucoinHelpVisible = !_isKucoinHelpVisible; },
            outputScheduler: App.UiScheduler);

        ToggleKucoinKeyVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingKucoinKey = !_isShowingKucoinKey; },
            outputScheduler: App.UiScheduler);

        ToggleKucoinSecretVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingKucoinSecret = !_isShowingKucoinSecret; },
            outputScheduler: App.UiScheduler);

        ToggleKucoinPassphraseVisibilityCommand = ReactiveCommand.Create(
            () => { IsShowingKucoinPassphrase = !_isShowingKucoinPassphrase; },
            outputScheduler: App.UiScheduler);

        // ── Affiliate links ────────────────────────────────────────────────────
        _binanceAffiliateUrl = creds.BinanceAffiliateUrl;
        _bybitAffiliateUrl   = creds.BybitAffiliateUrl;
        _okxAffiliateUrl     = creds.OkxAffiliateUrl;
        _kucoinAffiliateUrl  = creds.KucoinAffiliateUrl;

        OpenBinanceAffiliateCommand = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrWhiteSpace(_binanceAffiliateUrl))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_binanceAffiliateUrl) { UseShellExecute = true });
        }, outputScheduler: App.UiScheduler);

        OpenBybitAffiliateCommand = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrWhiteSpace(_bybitAffiliateUrl))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_bybitAffiliateUrl) { UseShellExecute = true });
        }, outputScheduler: App.UiScheduler);

        OpenOkxAffiliateCommand = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrWhiteSpace(_okxAffiliateUrl))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_okxAffiliateUrl) { UseShellExecute = true });
        }, outputScheduler: App.UiScheduler);

        OpenKucoinAffiliateCommand = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrWhiteSpace(_kucoinAffiliateUrl))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_kucoinAffiliateUrl) { UseShellExecute = true });
        }, outputScheduler: App.UiScheduler);

        // ── Profile commands ──────────────────────────────────────────────────
        SaveProfileCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var profile = SnapshotCurrentSettings();
                ProfileService.Save(_profileName.Trim(), profile);
                this.RaisePropertyChanged(nameof(AvailableProfiles));
                ProfileStatus = $"✓ Profile '{_profileName}' saved";
            }
            catch (Exception ex) { ProfileStatus = $"Save error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        LoadProfileCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var profile = ProfileService.Load(_profileName.Trim());
                if (profile is null) { ProfileStatus = $"Profile '{_profileName}' not found."; return; }
                ApplyProfile(profile);
                ProfileStatus = $"✓ Profile '{_profileName}' loaded";
            }
            catch (Exception ex) { ProfileStatus = $"Load error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        DeleteProfileCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                ProfileService.Delete(_profileName.Trim());
                this.RaisePropertyChanged(nameof(AvailableProfiles));
                ProfileStatus = $"Profile '{_profileName}' deleted";
            }
            catch (Exception ex) { ProfileStatus = $"Delete error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        ExportProfileCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var path = ProfileService.ExportToShared(_profileName.Trim());
                ProfileStatus = $"✓ Exported to {System.IO.Path.GetFileName(path)} — share this file.";
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(ProfileService.SharedDir) { UseShellExecute = true });
                }
                catch { /* opening the folder is best-effort */ }
            }
            catch (Exception ex) { ProfileStatus = $"Export error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        ImportProfileCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var names = ProfileService.ImportFromShared();
                this.RaisePropertyChanged(nameof(AvailableProfiles));
                ProfileStatus = names.Count == 0
                    ? $"No profiles found. Drop *.caiprofile files into {ProfileService.SharedDir}"
                    : $"✓ Imported {names.Count}: {string.Join(", ", names)}";
            }
            catch (Exception ex) { ProfileStatus = $"Import error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        SaveAffiliateLinksCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var current = CredentialsService.Load().Credentials;
                current.BinanceAffiliateUrl = _binanceAffiliateUrl.Trim();
                current.BybitAffiliateUrl   = _bybitAffiliateUrl.Trim();
                current.OkxAffiliateUrl     = _okxAffiliateUrl.Trim();
                current.KucoinAffiliateUrl  = _kucoinAffiliateUrl.Trim();
                CredentialsService.SaveAll(current);
                AffiliateSaveStatus = "✓ Saved";
            }
            catch (Exception ex) { AffiliateSaveStatus = $"Error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        // ── AI provider (Claude / ChatGPT) settings ──────────────────────────────
        var aiSettings = CredentialsService.LoadAiSettings();
        _aiIsChatGpt        = aiSettings.Provider == CryptoAITerminal.AIEngine.AiVendor.OpenAi;
        _aiIsClaude         = !_aiIsChatGpt;
        _anthropicKeyInput  = aiSettings.AnthropicKey;
        _openAiKeyInput     = aiSettings.OpenAiKey;
        _anthropicModelInput = string.IsNullOrWhiteSpace(aiSettings.AnthropicModel)
            ? CryptoAITerminal.AIEngine.AiRuntime.AnthropicModel : aiSettings.AnthropicModel;
        _openAiModelInput   = string.IsNullOrWhiteSpace(aiSettings.OpenAiModel)
            ? CryptoAITerminal.AIEngine.AiRuntime.OpenAiModel : aiSettings.OpenAiModel;

        SaveAiSettingsCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var provider = _aiIsChatGpt
                    ? CryptoAITerminal.AIEngine.AiVendor.OpenAi
                    : CryptoAITerminal.AIEngine.AiVendor.Anthropic;
                CredentialsService.SaveAiSettings(new CredentialsService.AiSettings(
                    provider,
                    (_anthropicKeyInput ?? "").Trim(),
                    (_openAiKeyInput ?? "").Trim(),
                    (_anthropicModelInput ?? "").Trim(),
                    (_openAiModelInput ?? "").Trim()));

                // Push the active provider's key/model into the master AIBot field, which
                // fans out (OnAIBotPropertyChanged) to every AI sub-VM — including the
                // autonomous trader and signal bot that read the key directly. The 15
                // generic AI services also resolve it via AiRuntime, so this unifies both.
                ApplyActiveAiKeyToAgents();

                AiSettingsStatus = $"✓ Saved — AI now uses {(_aiIsChatGpt ? "ChatGPT" : "Claude")}";
            }
            catch (Exception ex) { AiSettingsStatus = $"Error: {ex.Message}"; }
        }, outputScheduler: App.UiScheduler);

        OpenAnthropicConsoleCommand = ReactiveCommand.Create(() => OpenUrl("https://console.anthropic.com/settings/keys"),
            outputScheduler: App.UiScheduler);
        OpenOpenAiConsoleCommand = ReactiveCommand.Create(() => OpenUrl("https://platform.openai.com/api-keys"),
            outputScheduler: App.UiScheduler);

        // At startup, seed the autonomous trader + signal bot from the saved provider so a
        // previously-saved key works without reopening Settings. (The generic AI services
        // already fall back to AiRuntime, so they need no seeding.)
        {
            var startupKey = CryptoAITerminal.AIEngine.AiRuntime.ActiveApiKey;
            if (!string.IsNullOrWhiteSpace(startupKey))
            {
                AIBotVM.ClaudeApiKey = startupKey;
                AIBotVM.ClaudeModel  = CryptoAITerminal.AIEngine.AiRuntime.ActiveModel;
                AiTraderVM.Configure(startupKey, CryptoAITerminal.AIEngine.AiRuntime.ActiveModel);
            }
        }

        // ── Global AI command bar (Ctrl+K) ───────────────────────────────────────
        OpenCommandPaletteCommand = ReactiveCommand.Create(() =>
        {
            CommandPaletteInput  = string.Empty;
            CommandPaletteResult = string.Empty;
            IsCommandPaletteOpen = true;
        }, outputScheduler: App.UiScheduler);

        CloseCommandPaletteCommand = ReactiveCommand.Create(
            () => { IsCommandPaletteOpen = false; }, outputScheduler: App.UiScheduler);

        RunCommandPaletteCommand = ReactiveCommand.CreateFromTask(
            RunCommandPaletteAsync, outputScheduler: App.UiScheduler);

        BacktestVM = new BacktestViewModel(_gateway);

        var etherscanKey  = Environment.GetEnvironmentVariable("ETHERSCAN_API_KEY");
        var bscscanKey    = Environment.GetEnvironmentVariable("BSCSCAN_API_KEY");
        var alchemyKey    = Environment.GetEnvironmentVariable("ALCHEMY_API_KEY");
        var whaleService  = new WhaleTrackerService(500_000m, etherscanKey, bscscanKey);
        var whaleEnricher = string.IsNullOrWhiteSpace(alchemyKey)
            ? null
            : new WhaleTokenEnricher(new AlchemyPricesClient(alchemyKey));
        WhaleTrackerVM = new WhaleTrackerViewModel(whaleService, whaleEnricher);
        WhaleTrackerVM.RequestNavigateToSymbol = symbol => NavigateToSniperSymbol(symbol);

        // Funding Rate Monitor
        FundingRateVM = new FundingRateViewModel(_futuresGateway);

        // Funding Rate Arbitrage Bot
        var fundingArbSvc = new Services.FundingArbitrageService(
            _gateway,             // Binance spot
            _futuresGateway,      // Binance futures
            _bybitSpotGateway,    // Bybit spot
            _bybitFuturesGateway, // Bybit futures
            _okxSpotGateway,      // OKX spot
            _okxFuturesGateway);  // OKX futures
        FundingArbitrageVM = new FundingArbitrageViewModel(fundingArbSvc);
        FundingArbitrageVM.ToastRequested += ShowToast;

        // CEX-CEX Spot Arbitrage Scanner
        var cexArbSvc = new Services.CrossExchangeArbitrageService(
            _gateway,          // Binance spot
            _bybitSpotGateway, // Bybit spot
            _okxSpotGateway);  // OKX spot
        CrossExchangeArbVM = new CrossExchangeArbitrageViewModel(cexArbSvc);
        CrossExchangeArbVM.ToastRequested += ShowToast;

        // Copy Trading (Leader + Follower)
        var copyLeaderSvc   = new Services.CopyTradingLeaderService();
        var copyFollowerSvc = new Services.CopyTradingFollowerService();
        CopyTradingVM = new CopyTradingViewModel(copyLeaderSvc, copyFollowerSvc);
        CopyTradingVM.SetFollowerGateway(_gateway);
        CopyTradingVM.ToastRequested += ShowToast;

        // Statistical Arbitrage (Pairs Trading)
        StatArbVM = new StatArbViewModel(_gateway);

        // Best Execution Router
        var routerSvc = new Services.BestExecutionRouterService(
            _gateway,          // Binance spot
            _bybitSpotGateway, // Bybit spot
            _okxSpotGateway);  // OKX spot
        BestExecutionVM = new BestExecutionViewModel(routerSvc);
        BestExecutionVM.ToastRequested += ShowToast;

        // Multi-Market Scanner
        var scannerSvc = new Services.MarketScannerService(
            _gateway,          // Binance spot
            _bybitSpotGateway, // Bybit spot
            _okxSpotGateway);  // OKX spot
        ScannerVM = new MarketScannerViewModel(scannerSvc);
        ScannerVM.ToastRequested += ShowToast;
        ScannerVM.OnGoToTrading = symbol =>
        {
            SelectMainTab("sniper");
        };

        // Liquidation Heatmap
        var liqSvc = new LiquidationDataService();
        LiquidationHeatmapVM = new LiquidationHeatmapViewModel(liqSvc);

        // DEX Trending Feed
        var dexTrendingSvc = new DexTrendingService();
        DexTrendingVM = new DexTrendingViewModel(dexTrendingSvc);
        DexTrendingVM.OnOpenInSniper = (addr, chain) =>
        {
            SelectMainTab("sniper");
            AddLog($"[DEX TRENDING] Navigated to Sniper for {addr} on {chain}");
        };

        // Portfolio Rebalancer (CEX: Binance + Bybit + OKX; DEX: connected wallet)
        var rebalanceSvc = new PortfolioRebalanceService(
            new Core.Interfaces.IExchangeGateway[] { _gateway, _bybitSpotGateway, _okxSpotGateway },
            WalletVM);
        PortfolioRebalanceVM = new PortfolioRebalanceViewModel(rebalanceSvc);
        PortfolioRebalanceVM.AlertFired += msg =>
            ShowToast($"⚠ Portfolio Drift\n{msg}");

        // Sentiment (Fear & Greed + Long/Short + Open Interest)
        var sentimentSvc = new SentimentService();
        SentimentVM = new SentimentViewModel(sentimentSvc);

        // P&L dashboard
        var pnlService = new PnlDashboardService();
        pnlService.Load();
        PnlDashboardVM     = new PnlDashboardViewModel(pnlService);
        TradeJournalVM     = new TradeJournalViewModel(pnlService);
        TelegramSignalVM   = new TelegramSignalViewModel(_telegram);
        MarketTapeVM       = new MarketTapeViewModel(new MarketTapeService());
        HotkeySettings     = HotkeySettings.Load();

        // Gas Monitor (Ethereum / BSC / Solana)
        var etherscanKey2 = Environment.GetEnvironmentVariable("ETHERSCAN_API_KEY");
        var bscscanKey2   = Environment.GetEnvironmentVariable("BSCSCAN_API_KEY");
        _gasMonitorService = new GasMonitorService
        {
            EtherscanApiKey = etherscanKey2,
            BscscanApiKey   = bscscanKey2
        };
        GasMonitorVM = new GasMonitorViewModel(_gasMonitorService);
        GasMonitorVM.AlertTriggered += msg =>
        {
            ShowToast(msg);
            AddLog($"[GAS ALERT] {msg}");
        };
        _gasMonitorService.Start();

        // All Positions (unified view across all futures gateways)
        AllPositionsVM = new AllPositionsViewModel(
        [
            ("Binance",  _futuresGateway),
            ("Bybit",    _bybitFuturesGateway),
            ("OKX",      _okxFuturesGateway),
            ("KuCoin",   _kucoinFuturesGateway),
        ]);
        AllPositionsVM.OnLog += AddLog;

        // News Feed (RSS: CoinTelegraph, CoinDesk, Decrypt, The Block, Bitcoin Magazine)
        _newsFeedService = new NewsFeedService();
        NewsFeedVM = new NewsFeedViewModel(_newsFeedService);
        NewsFeedVM.AlertTriggered += msg => { ShowToast(msg); AddLog($"[NEWS] {msg}"); };
        _newsFeedService.Start();

        // Dashboard overview (reads the news market pulse + AI digest)
        DashboardVM = new DashboardViewModel(AIBotVM, GridBotVM, DcaBotVM, pnlService, AllPositionsVM, NewsFeedVM);

        // AI Copilot — read-only conversational assistant. Reads real positions from the
        // unified positions view, scanner AI picks and the news pulse; can never trade.
        var copilotData = new CopilotAgentService.CopilotDataSource(
            Account: async ct => await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var positions = new List<CopilotAgentService.PositionLine>(AllPositionsVM.Rows.Count);
                foreach (var r in AllPositionsVM.Rows)
                {
                    var qty = r.Side == "Short" ? -r.Size : r.Size;
                    positions.Add(new CopilotAgentService.PositionLine(
                        r.Symbol, qty, r.EntryPrice, r.MarkPrice, r.UnrealizedPnl));
                }
                var mode = WalletVM.GlobalLiveExecutionEnabled ? "live" : "paper";
                // Cash isn't a single coherent figure across wallets here → 0 = "not tracked".
                return new CopilotAgentService.AccountSnapshot(mode, 0m, 0m, positions);
            }),
            Price: async (symbol, ct) =>
            {
                var book = await _gateway.GetOrderBookAsync(symbol, 5).ConfigureAwait(false);
                var bid = book.Bids.Count > 0 ? book.Bids[0].Price : 0m;
                var ask = book.Asks.Count > 0 ? book.Asks[0].Price : 0m;
                return bid > 0 && ask > 0 ? (bid + ask) / 2m : Math.Max(bid, ask);
            },
            TopOpportunities: async ct => await Dispatcher.UIThread.InvokeAsync(() =>
                (IReadOnlyList<string>)ScannerVM.AiPicks
                    .Select(p => $"{p.Symbol} [{p.Bias} {p.Score}] {p.Reason}").ToList()),
            MarketPulse: async ct => await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var pulse = $"{NewsFeedVM.PulseLabel} (score {NewsFeedVM.PulseScore}). {NewsFeedVM.PulseDetail}";
                if (!string.IsNullOrWhiteSpace(NewsFeedVM.AiDigest))
                    pulse += $" Digest [{NewsFeedVM.AiDigestBias}]: {NewsFeedVM.AiDigest}";
                return pulse;
            }));
        CopilotVM = new CopilotViewModel(copilotData);
        var telegramUserClient = new Services.TelegramUserClientService();
        TelegramAccountVM = new TelegramAccountViewModel(telegramUserClient);

        // Read trading signals from followed Telegram channels: every new channel/group
        // message is parsed; only clean signals (symbol + direction) surface in the queue.
        telegramUserClient.ChannelMessageReceived += OnTelegramChannelMessage;

        // Daily AI briefing — one morning read tying together the book, news pulse,
        // sentiment and scanner. RefreshCommand runs on the UI scheduler, so the
        // gather delegate may read the VM collections directly.
        BriefingVM = new DailyBriefingViewModel(() =>
        {
            decimal upnl = 0m, bestPnl = 0m, worstPnl = 0m;
            string? bestSym = null, worstSym = null;
            foreach (var r in AllPositionsVM.Rows)
            {
                upnl += r.UnrealizedPnl;
                if (bestSym is null || r.UnrealizedPnl > bestPnl)  { bestSym = r.Symbol;  bestPnl = r.UnrealizedPnl; }
                if (worstSym is null || r.UnrealizedPnl < worstPnl) { worstSym = r.Symbol; worstPnl = r.UnrealizedPnl; }
            }
            var picks = ScannerVM.AiPicks.Select(p => $"{p.Symbol} [{p.Bias} {p.Score}]").ToList();
            return new BriefingInput(
                OpenPositions: AllPositionsVM.Rows.Count,
                UnrealizedPnlUsd: upnl,
                BestSymbol: bestSym, BestPnlUsd: bestPnl,
                WorstSymbol: worstSym, WorstPnlUsd: worstPnl,
                NewsPulseScore: NewsFeedVM.PulseScore,
                NewsPulseLabel: NewsFeedVM.PulseLabel,
                FearGreed: SentimentVM.FearGreedValue,
                TopPicks: picks);
        });
        BriefingVM.BriefingReady += summary => ShowToast(summary);

        // AI pre-trade risk check — scores a proposed order against the open book.
        // Advisory only; never touches the live order path.
        RiskCheckVM = new PreTradeRiskViewModel((symbol, side, orderUsd, leverage) =>
        {
            decimal existing = 0m, same = 0m;
            foreach (var r in AllPositionsVM.Rows)
            {
                var px = r.MarkPrice > 0 ? r.MarkPrice : r.EntryPrice;
                var notional = r.Size * px;
                existing += notional;
                if (string.Equals(r.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) same += notional;
            }
            return new PreTradeRiskInput(
                Symbol: symbol, Side: side, OrderUsd: orderUsd,
                EquityUsd: 0m,                      // no single coherent equity figure → unknown
                ExistingExposureUsd: existing,
                SameSymbolExposureUsd: same,
                MaxExposureUsd: AiTraderVM.MaxTotalExposureUsd,
                Leverage: leverage,
                DailyPnlUsd: 0m,
                MaxDailyLossUsd: AiTraderVM.MaxDailyLossUsd);
        });

        // AI correlation insight — builds a matrix from recent daily closes (spot
        // gateway) and reads the book's diversification posture.
        CorrelationInsightVM = new CorrelationInsightViewModel(
            fetchCloses: async (symbol, ct) =>
            {
                var candles = await _gateway.GetCandlesAsync(symbol, "1d", 60).ConfigureAwait(false);
                return candles.Select(c => (c.Timestamp, c.Close)).ToList();
            },
            heldSymbols: () => AllPositionsVM.Rows.Select(r => r.Symbol).Distinct().ToList());

        // AI options advisor — suggests a structure from the Deribit IV/skew/PCR
        // snapshot (owned by the Sentiment tab) plus a directional view.
        OptionsStrategyVM = new OptionsStrategyViewModel((asset, direction) =>
        {
            var snap = SentimentVM.OptionsSnapshot;
            if (snap is null) return null;
            var d = string.Equals(asset, "ETH", StringComparison.OrdinalIgnoreCase) ? snap.Eth : snap.Btc;
            if (!d.IsValid) return null;
            return new OptionsStrategyInput(d.Asset, d.AtmIv, d.Skew25Delta, d.PutCallRatio, d.IndexPrice, direction);
        });

        // On-Chain Metrics (Glassnode)
        _onChainService = new OnChainMetricsService();
        OnChainVM = new OnChainMetricsViewModel(_onChainService);
        OnChainVM.AlertTriggered += msg => { ShowToast(msg); AddLog($"[ON-CHAIN] {msg}"); };
        _onChainService.Start();

        // Wire Liquidation Heatmap proximity alerts
        LiquidationHeatmapVM.ProximityAlertTriggered += msg =>
        {
            ShowToast(msg);
            AddLog($"[LIQ ALERT] {msg}");
        };

        // Import existing sniper history
        pnlService.ImportSniperTrades(SniperVM.LiveTradeHistory);
        pnlService.ImportSniperTrades(SniperVM.PaperTradeHistory);

        // Balance refresher — раз в 60 сек опрашивает GetBalanceAsync по ключевым активам
        // на всех подключённых гейтвеях. Кэш используется в snapshot.
        _balanceRefresher = new BalanceRefresher(
        [
            ("Binance", "Spot",    _gateway),
            ("Binance", "Futures", _futuresGateway),
            ("Bybit",   "Spot",    _bybitSpotGateway),
            ("Bybit",   "Futures", _bybitFuturesGateway),
            ("OKX",     "Spot",    _okxSpotGateway),
            ("OKX",     "Futures", _okxFuturesGateway),
            ("KuCoin",  "Spot",    _kucoinSpotGateway),
            ("KuCoin",  "Futures", _kucoinFuturesGateway),
        ]);

        // WebApi snapshot — пишем JSON в %APPDATA%/CryptoAITerminal/webapi/snapshot.json
        // каждые 5 секунд; CryptoAITerminal.WebApi читает его и отдаёт по HTTP.
        _webApiSnapshotWriter = new WebApiSnapshotWriter(() => BuildWebApiSnapshot(pnlService));

        // WebApi queue — подбираем market-ордера, поставленные через POST /api/orders/market,
        // и маршрутизируем их в соответствующий gateway. Результат пишется в processed/<id>.json.
        var queueGateways = new Dictionary<(string, string), Core.Interfaces.IExchangeGateway>(
            new[]
            {
                new KeyValuePair<(string, string), Core.Interfaces.IExchangeGateway>(("Binance", "Spot"),    _gateway),
                new KeyValuePair<(string, string), Core.Interfaces.IExchangeGateway>(("Binance", "Futures"), _futuresGateway),
                new KeyValuePair<(string, string), Core.Interfaces.IExchangeGateway>(("Bybit",   "Spot"),    _bybitSpotGateway),
                new KeyValuePair<(string, string), Core.Interfaces.IExchangeGateway>(("Bybit",   "Futures"), _bybitFuturesGateway),
                new KeyValuePair<(string, string), Core.Interfaces.IExchangeGateway>(("OKX",     "Spot"),    _okxSpotGateway),
                new KeyValuePair<(string, string), Core.Interfaces.IExchangeGateway>(("OKX",     "Futures"), _okxFuturesGateway),
                new KeyValuePair<(string, string), Core.Interfaces.IExchangeGateway>(("KuCoin",  "Spot"),    _kucoinSpotGateway),
                new KeyValuePair<(string, string), Core.Interfaces.IExchangeGateway>(("KuCoin",  "Futures"), _kucoinFuturesGateway),
            });
        _webApiQueueProcessor = new WebApiQueueProcessor(queueGateways, msg => AddLog(msg));

        // Feed the CEX risk guard's daily-loss budget from every realized losing trade
        // (bots, grid, manual) routed through the P&L store.
        pnlService.OnTradeRecorded = OnRealizedTradeRecorded;

        // Forward bot closed trades to P&L
        AIBotVM.OnBotTradeClosed = (sym, dir, entry, exit, qty, pnl) =>
        {
            var direction = string.Equals(dir, "Short", StringComparison.OrdinalIgnoreCase)
                ? TradeDirection.Short : TradeDirection.Long;
            pnlService.RecordTrade(new TradeRecord
            {
                Source      = TradeSource.Bot,
                BotName     = "AI Bot",
                Symbol      = sym,
                Exchange    = AIBotVM.SelectedExchange,
                Direction   = direction,
                OpenedAtUtc = DateTime.UtcNow,
                ClosedAtUtc = DateTime.UtcNow,
                EntryPrice  = entry,
                ExitPrice   = exit,
                Quantity    = qty,
                PnlUsd      = pnl,
                PnlPercent  = entry == 0 ? 0 : (exit - entry) / entry * 100m,
                ExitReason  = "Signal"
            });
            if (CopyTradingVM.IsLeader)
                copyLeaderSvc.PublishTrade(AIBotVM.SelectedExchange, "Futures", sym,
                    dir, qty, exit, "AI Bot");
        };

        // Grid Bot cycles → P&L + copy trading
        GridBotVM.OnCycleClosed = (sym, buyPrice, sellPrice, qty, profit) =>
        {
            pnlService.RecordTrade(new TradeRecord
            {
                Source      = TradeSource.Bot,
                BotName     = "Grid Bot",
                Symbol      = sym,
                Exchange    = "Binance",
                Direction   = TradeDirection.Long,
                OpenedAtUtc = DateTime.UtcNow,
                ClosedAtUtc = DateTime.UtcNow,
                EntryPrice  = buyPrice,
                ExitPrice   = sellPrice,
                Quantity    = qty,
                PnlUsd      = profit,
                PnlPercent  = buyPrice == 0 ? 0 : (sellPrice - buyPrice) / buyPrice * 100m,
                ExitReason  = "Grid Level"
            });
            if (CopyTradingVM.IsLeader)
                copyLeaderSvc.PublishTrade("Binance", "Spot", sym, "SELL", qty, sellPrice, "Grid Bot");
        };

        // DCA Bot executions → P&L (recorded as open positions; P&L realised externally)
        DcaBotVM.OnExecutionForwarded = exec =>
            pnlService.RecordTrade(new TradeRecord
            {
                Source      = TradeSource.Bot,
                BotName     = "DCA Bot",
                Symbol      = exec.Symbol,
                Exchange    = "Binance",
                Direction   = TradeDirection.Long,
                OpenedAtUtc = exec.ExecutedAt,
                ClosedAtUtc = exec.ExecutedAt,
                EntryPrice  = exec.Price,
                ExitPrice   = exec.Price,   // no exit yet — P&L = 0 for now
                Quantity    = exec.Quantity,
                PnlUsd      = 0m,
                PnlPercent  = 0m,
                ExitReason  = "DCA Buy"
            });

        // CEX Arb executions → P&L
        CrossExchangeArbVM.OnArbExecuted = (sym, buyExchange, sellExchange, profit) =>
            pnlService.RecordTrade(new TradeRecord
            {
                Source      = TradeSource.Arb,
                BotName     = "CEX Arb",
                Symbol      = sym,
                Exchange    = $"{buyExchange}→{sellExchange}",
                Direction   = TradeDirection.Long,
                OpenedAtUtc = DateTime.UtcNow,
                ClosedAtUtc = DateTime.UtcNow,
                EntryPrice  = 0m,
                ExitPrice   = 0m,
                Quantity    = 0m,
                PnlUsd      = profit,
                PnlPercent  = 0m,
                ExitReason  = "Arb Execution"
            });

        // Funding Arb position closures → P&L
        FundingArbitrageVM.OnPositionClosed = row =>
        {
            var pos = row.Model;
            pnlService.RecordTrade(new TradeRecord
            {
                Source      = TradeSource.Funding,
                BotName     = "Funding Arb",
                Symbol      = pos.Symbol,
                Exchange    = pos.Exchange,
                Direction   = TradeDirection.Long,
                OpenedAtUtc = pos.OpenedAt,
                ClosedAtUtc = DateTime.UtcNow,
                EntryPrice  = pos.SpotEntryPrice,
                ExitPrice   = pos.CurrentSpotPrice,
                Quantity    = pos.SpotQty,
                PnlUsd      = pos.TotalPnlUsd,
                PnlPercent  = pos.SpotEntryPrice == 0 ? 0m
                              : pos.TotalPnlUsd / (pos.SpotEntryPrice * pos.SpotQty) * 100m,
                ExitReason  = "Manual Close"
            });
        };

        // ── Composite Rule Bot ───────────────────────────────────────────────
        CompositeRuleVM      = new CompositeRuleViewModel();
        OrderTemplatesVM     = new OrderTemplatesViewModel();
        AdvancedTrailingVM   = new AdvancedTrailingStopViewModel();
        CompositeRuleVM.ToastRequested = ShowToast;

        // Wire action callbacks to the real bots
        CompositeRuleVM.Engine.OnStartDcaBuy = (symbol, amount) =>
        {
            AddLog($"[CompositeBot] DCA Buy triggered: {symbol} ${amount:0.##}");
            ShowToast($"Rule → DCA Buy {symbol} ${amount:0.##}");
        };
        CompositeRuleVM.Engine.OnMoveStopToBreakeven = symbol =>
        {
            AddLog($"[CompositeBot] Move stop to breakeven: {symbol}");
            ShowToast($"Rule → Move stop ({symbol}) to breakeven");
        };
        CompositeRuleVM.Engine.OnStartFundingArb = symbol =>
        {
            AddLog($"[CompositeBot] Start funding arb: {symbol}");
            ShowToast($"Rule → Funding Arb ({symbol}) initiated");
        };
        CompositeRuleVM.Engine.OnPauseGrid = _ =>
        {
            AddLog("[CompositeBot] Grid Bot paused by rule");
            ShowToast("Rule → Grid Bot paused");
        };
        CompositeRuleVM.Engine.OnResumeGrid = _ =>
        {
            AddLog("[CompositeBot] Grid Bot resumed by rule");
            ShowToast("Rule → Grid Bot resumed");
        };
        CompositeRuleVM.Engine.OnCloseAllPositions = symbol =>
        {
            AddLog($"[CompositeBot] Close all positions: {symbol}");
            ShowToast($"Rule → Close all positions ({symbol})");
        };
        CompositeRuleVM.Engine.OnNotify = msg => ShowToast(msg);

        // Feed live market data into the engine
        _marketDataSubscription2 = _gateway.MarketDataStream.Subscribe(data =>
            CompositeRuleVM.Engine.FeedMarketData(data.Symbol, data.LastPrice));

        // ── Telegram Inline Signals ───────────────────────────────────────
        TelegramSignalVM.SignalAccepted += sig =>
        {
            AddLog($"[TG] Accepted: {sig.Side} {sig.Symbol} @ {sig.Price:N2}");
            ShowToast($"✅ TG Signal executed: {sig.Side} {sig.Symbol}");
            // Populate trade form and fire market order
            SelectedOrderSide = sig.Side;
            if (sig.Quantity > 0) TradeQuantity = sig.Quantity;
            _ = sig.Side == "SELL"
                ? PlaceCexMarketOrderAsync(Core.Enums.OrderSide.Sell, sig.Quantity > 0 ? sig.Quantity : TradeQuantity, reduceOnly: IsManualFuturesMode)
                : PlaceCexMarketOrderAsync(Core.Enums.OrderSide.Buy,  sig.Quantity > 0 ? sig.Quantity : TradeQuantity);
        };
        TelegramSignalVM.SignalSkipped += sig =>
        {
            AddLog($"[TG] Skipped: {sig.Side} {sig.Symbol}");
            ShowToast($"❌ TG Signal skipped: {sig.Symbol}");
        };
        // Start polling only when bot is configured (AlertsVM holds the token/chatId)
        if (_telegram.IsConfigured) _telegram.StartPolling();

        // ── Order Templates ────────────────────────────────────────────────
        // (already initialised via property initialiser; wire apply callback)
        OrderTemplatesVM.TemplateApplied += ApplyOrderTemplate;

        FocusTradingPairCommand = ReactiveCommand.Create(() => SelectMainTab("trading"),      outputScheduler: App.UiScheduler);
        HotkeyAlloc25Command    = ReactiveCommand.Create(() => SelectTradeAllocation("25"),   outputScheduler: App.UiScheduler);
        HotkeyAlloc50Command    = ReactiveCommand.Create(() => SelectTradeAllocation("50"),   outputScheduler: App.UiScheduler);
        HotkeyAlloc100Command   = ReactiveCommand.Create(() => SelectTradeAllocation("100"),  outputScheduler: App.UiScheduler);

        ApplyTemplate1Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(1), outputScheduler: App.UiScheduler);
        ApplyTemplate2Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(2), outputScheduler: App.UiScheduler);
        ApplyTemplate3Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(3), outputScheduler: App.UiScheduler);
        ApplyTemplate4Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(4), outputScheduler: App.UiScheduler);
        ApplyTemplate5Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(5), outputScheduler: App.UiScheduler);
        ApplyTemplate6Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(6), outputScheduler: App.UiScheduler);
        ApplyTemplate7Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(7), outputScheduler: App.UiScheduler);
        ApplyTemplate8Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(8), outputScheduler: App.UiScheduler);
        ApplyTemplate9Command = ReactiveCommand.Create(() => OrderTemplatesVM.ApplySlot(9), outputScheduler: App.UiScheduler);

        // Wall mode — wires selected market's WallMode and persists via callback
        SetWallModeCommand = ReactiveCommand.Create<string>(mode =>
        {
            if (SelectedMarket is null) return;
            SelectedMarket.WallMode = string.Equals(mode, "Qty", System.StringComparison.OrdinalIgnoreCase)
                ? Core.Models.WallHighlightMode.Qty
                : Core.Models.WallHighlightMode.Usd;
        }, outputScheduler: App.UiScheduler);

        // ── Advanced Trailing Stop ─────────────────────────────────────────
        // (already initialised via property initialiser; wire events)
        AdvancedTrailingVM.ArmRequested += () =>
            AdvancedTrailingVM.Arm(StrategyEntryPrice > 0 ? StrategyEntryPrice : CurrentTradePrice);

        AdvancedTrailingVM.StopLevelChanged += newStop =>
        {
            // Keep StopLossPrice display in sync so it shows in the ticket
            if (Dispatcher.UIThread.CheckAccess())
                StopLossPrice = newStop;
            else
                Dispatcher.UIThread.Post(() => StopLossPrice = newStop);
        };

        AdvancedTrailingVM.StopTriggered += triggerPrice =>
        {
            AddLog($"[Trailing Stop] Triggered at {triggerPrice:N2} — closing position");
            ShowToast($"★ Trailing stop triggered at {triggerPrice:N2}");
            _ = ExecuteClosePosition();
        };

        _localization.LanguageChanged += OnLocalizationLanguageChanged;
        WalletVM.PropertyChanged += OnWalletWorkspacePropertyChanged;
        AIBotVM.PropertyChanged += OnAIBotPropertyChanged;
        DexTradingVM.PropertyChanged += OnDexTradingPropertyChanged;
        LoadAiCustomPromptPresetsFromDisk();

        // First-run: greet with the demo/paper explainer so buyers can explore
        // the whole terminal before touching API keys.
        IsWelcomeVisible = IsFirstRun();

        // Background: check GitHub Releases for a newer version (non-blocking).
        StartUpdateCheck();

        // License: gate live execution on a valid license; demo stays open always.
        LicenseVM.LicenseChanged += ApplyLicenseState;
        ApplyLicenseState(LicenseVM.Snapshot);
        // Trial expired → surface activation immediately (but never on the very
        // first run, where the welcome overlay greets the user instead).
        if (LicenseVM.Snapshot.IsExpired && !IsWelcomeVisible)
            LicenseVM.IsVisible = true;

        _marketDataSubscription = _gateway.MarketDataStream.Subscribe(data =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var market = Markets.FirstOrDefault(item => string.Equals(item.Symbol, data.Symbol, StringComparison.OrdinalIgnoreCase));
                market?.UpdateMarketData(data);

                if (SelectedMarket is not null &&
                    string.Equals(SelectedMarket.Symbol, data.Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsManualFuturesMode)
                    {
                        CurrentMarketData = data;
                        ScheduleWorkingOrdersEvaluation();
                    }
                    else if (ShouldUseSpotDisplayFallback(data))
                    {
                        CurrentMarketData = data;
                    }

                    // Only forward price when the symbol matches the heatmap's symbol.
                    // Forwarding a different coin's price (e.g. DOGE $0.11) with BTC levels
                    // makes all Y values overflow → everything clamps to Y=0 (bars at top).
                    if (LiquidationHeatmapVM is not null && data.LastPrice > 0 &&
                        string.Equals(data.Symbol, LiquidationHeatmapVM.Symbol,
                            StringComparison.OrdinalIgnoreCase))
                        LiquidationHeatmapVM.UpdateCurrentPrice(data.LastPrice);
                }
            });
        });

        _futuresMarketDataSubscription = _futuresGateway.MarketDataStream.Subscribe(data =>
        {
            if (!IsManualFuturesMode)
            {
                return;
            }

            var selectedSymbolSnapshot = SelectedMarket?.Symbol;
            if (string.IsNullOrWhiteSpace(selectedSymbolSnapshot) ||
                !string.Equals(selectedSymbolSnapshot, data.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            QueueFuturesUiMarketData(data);
        });

        _futuresAccountStateSubscription = _futuresGateway.AccountStateChangedStream.Subscribe(symbolOrAsset =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsManualFuturesMode)
                {
                    return;
                }

                if (!string.Equals(symbolOrAsset, "USDT", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(symbolOrAsset, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _ = RefreshManualAccountStateAsync();
            });
        });

        _futuresOrderUpdateSubscription = _futuresGateway.OrderUpdateStream.Subscribe(order =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsManualFuturesMode ||
                    !string.Equals(order.Symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _ = RefreshManualAccountStateAsync();
            });
        });

        _futuresTradeUpdateSubscription = _futuresGateway.TradeUpdateStream.Subscribe(trade =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsManualFuturesMode ||
                    !string.Equals(trade.Symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _ = SyncExchangeRecentTradesAsync(SelectedTradingSymbol);
                _ = RefreshManualAccountStateAsync();
            });
        });

        BuyMarketCommand = ReactiveCommand.CreateFromTask(ExecuteBuyMarket, outputScheduler: App.UiScheduler);
        SellMarketCommand = ReactiveCommand.CreateFromTask(ExecuteSellMarket, outputScheduler: App.UiScheduler);
        PlaceBuyLimitCommand = ReactiveCommand.Create(PlaceBuyLimit, outputScheduler: App.UiScheduler);
        PlaceSellLimitCommand = ReactiveCommand.Create(PlaceSellLimit, outputScheduler: App.UiScheduler);
        ArmTakeProfitCommand = ReactiveCommand.Create(ArmTakeProfit, outputScheduler: App.UiScheduler);
        ArmStopLossCommand = ReactiveCommand.Create(ArmStopLoss, outputScheduler: App.UiScheduler);
        CancelAllOrdersCommand = ReactiveCommand.Create(CancelAllOrders, outputScheduler: App.UiScheduler);
        ClosePositionCommand = ReactiveCommand.CreateFromTask(ExecuteClosePosition, outputScheduler: App.UiScheduler);
        ReversePositionCommand = ReactiveCommand.CreateFromTask(ExecuteReversePosition, outputScheduler: App.UiScheduler);
        OpenWalletTabCommand = ReactiveCommand.Create(() => { SelectMainTab("portfolio"); }, outputScheduler: App.UiScheduler);
        SelectMainTabCommand = ReactiveCommand.Create<string>(SelectMainTab, outputScheduler: App.UiScheduler);
        StartDemoCommand = ReactiveCommand.Create(StartDemoExploring, outputScheduler: App.UiScheduler);
        OpenApiKeysFromWelcomeCommand = ReactiveCommand.Create(OpenApiKeysFromWelcome, outputScheduler: App.UiScheduler);
        OpenUpdateCommand = ReactiveCommand.Create(OpenUpdateUrl, outputScheduler: App.UiScheduler);
        DismissUpdateCommand = ReactiveCommand.Create(DismissUpdateBanner, outputScheduler: App.UiScheduler);
        SelectOrderSideCommand = ReactiveCommand.Create<string>(SelectOrderSide, outputScheduler: App.UiScheduler);
        PlacePrimaryOrderCommand = ReactiveCommand.CreateFromTask(PlacePrimaryOrderAsync, outputScheduler: App.UiScheduler);
        SelectTradeTimeframeCommand = ReactiveCommand.Create<string>(SelectTradeTimeframe, outputScheduler: App.UiScheduler);
        SelectLimitPriceCommand = ReactiveCommand.Create<decimal>(SelectLimitPrice, outputScheduler: App.UiScheduler);
        SelectBidPriceCommand = ReactiveCommand.Create<decimal>(SelectBidPrice, outputScheduler: App.UiScheduler);
        SelectAskPriceCommand = ReactiveCommand.Create<decimal>(SelectAskPrice, outputScheduler: App.UiScheduler);
        SelectTradeAllocationCommand = ReactiveCommand.Create<string>(SelectTradeAllocation, outputScheduler: App.UiScheduler);
        IncreaseLimitPriceCommand = ReactiveCommand.Create(() => ShiftLimitPrice(_priceStep), outputScheduler: App.UiScheduler);
        DecreaseLimitPriceCommand = ReactiveCommand.Create(() => ShiftLimitPrice(-_priceStep), outputScheduler: App.UiScheduler);
        ApplyAtrPresetCommand = ReactiveCommand.Create(ApplyAtrPreset, outputScheduler: App.UiScheduler);
        ApplyRiskRewardPresetCommand = ReactiveCommand.Create(ApplyRiskRewardPreset, outputScheduler: App.UiScheduler);
        ApplyScalpPresetCommand = ReactiveCommand.Create<string>(ApplyScalpPreset, outputScheduler: App.UiScheduler);
        SelectChartToolCommand = ReactiveCommand.Create<string>(SelectChartTool, outputScheduler: App.UiScheduler);
        ClearChartDrawingsCommand = ReactiveCommand.Create(ClearChartDrawings, outputScheduler: App.UiScheduler);
        ResetChartViewCommand = ReactiveCommand.Create(ResetChartView, outputScheduler: App.UiScheduler);
        ToggleVwapCommand = ReactiveCommand.Create(ToggleVwap, outputScheduler: App.UiScheduler);
        ToggleVolumeProfileCommand = ReactiveCommand.Create(ToggleVolumeProfile, outputScheduler: App.UiScheduler);
        ToggleLadderCenterModeCommand = ReactiveCommand.Create(ToggleLadderCenterMode, outputScheduler: App.UiScheduler);
        SelectTradingVenueCommand = ReactiveCommand.Create<string>(SelectTradingVenue, outputScheduler: App.UiScheduler);
        FocusMarketCommand = ReactiveCommand.Create<CexMarketItemViewModel>(FocusMarket, outputScheduler: App.UiScheduler);
        OpenMarketInTradingCommand = ReactiveCommand.Create<CexMarketItemViewModel>(OpenMarketInTrading, outputScheduler: App.UiScheduler);
        ToggleMarketFavoriteCommand = ReactiveCommand.Create<CexMarketItemViewModel>(ToggleMarketFavorite, outputScheduler: App.UiScheduler);
        RefreshMarketsCommand = ReactiveCommand.CreateFromTask(RefreshMarketsHubAsync, outputScheduler: App.UiScheduler);
        AddCustomMarketCommand = ReactiveCommand.CreateFromTask(AddCustomMarketAsync, outputScheduler: App.UiScheduler);
        RemoveMarketCommand = ReactiveCommand.Create<CexMarketItemViewModel>(RemoveCustomMarket, outputScheduler: App.UiScheduler);
        SafeLogoutCommand = ReactiveCommand.CreateFromTask(ExecuteSafeLogoutAsync, outputScheduler: App.UiScheduler);
        SendAiAssistantPromptCommand = ReactiveCommand.Create(SendAiAssistantPrompt, outputScheduler: App.UiScheduler);
        UseAiAssistantQuickPromptCommand = ReactiveCommand.Create<AiAssistantQuickPromptViewModel>(UseAiAssistantQuickPrompt, outputScheduler: App.UiScheduler);
        SelectAiVisualCommand = ReactiveCommand.Create<AiVisualCardViewModel>(SelectAiVisual, outputScheduler: App.UiScheduler);
        ClearAiAssistantConversationCommand = ReactiveCommand.Create(ClearAiAssistantConversation, outputScheduler: App.UiScheduler);
        InjectAiMarketContextCommand = ReactiveCommand.Create(InjectAiMarketContext, outputScheduler: App.UiScheduler);
        ExplainAiKnowledgeTopicCommand = ReactiveCommand.Create<AiKnowledgeTopicViewModel>(ExplainAiKnowledgeTopic, outputScheduler: App.UiScheduler);
        AnalyzeSelectedAiVisualCommand = ReactiveCommand.Create(AnalyzeSelectedAiVisual, outputScheduler: App.UiScheduler);
        BuildAiTradePlanCommand = ReactiveCommand.Create(BuildAiTradePlan, outputScheduler: App.UiScheduler);
        SelectAiContextVenueModeCommand = ReactiveCommand.Create<string>(SelectAiContextVenueMode, outputScheduler: App.UiScheduler);
        ToggleAiContextSourceCommand = ReactiveCommand.Create<string>(ToggleAiContextSource, outputScheduler: App.UiScheduler);
        GenerateAiPresetPromptCommand = ReactiveCommand.Create(GenerateAiPresetPrompt, outputScheduler: App.UiScheduler);
        LoadAiSavedPromptPresetCommand = ReactiveCommand.Create<AiPromptPresetViewModel>(LoadAiSavedPromptPreset, outputScheduler: App.UiScheduler);
        SaveAiCustomPromptPresetCommand = ReactiveCommand.Create(SaveAiCustomPromptPreset, outputScheduler: App.UiScheduler);
        DeleteAiCustomPromptPresetCommand = ReactiveCommand.Create<AiPromptPresetViewModel>(DeleteAiCustomPromptPreset, outputScheduler: App.UiScheduler);
        BuildAiSavedPromptPresetCommand = ReactiveCommand.Create<AiPromptPresetViewModel>(BuildAiSavedPromptPreset, outputScheduler: App.UiScheduler);
        SendAiSavedPromptPresetCommand = ReactiveCommand.Create<AiPromptPresetViewModel>(SendAiSavedPromptPreset, outputScheduler: App.UiScheduler);
        ObserveCommandErrors();

        _orderBookTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _orderBookTimer.Tick += async (_, _) => await RefreshSelectedOrderBookAsync();

        SelectedMarket = Markets.FirstOrDefault();
        InitializeAiSignalStudio();
        RefreshQuickBacktestSnapshot();
        RaiseTimeframeStateChanged();
        _ = InitializeAsync();
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
            RaiseShellNavigationStateChanged();
        }
    }

    public MarketData? CurrentMarketData
    {
        get => _currentMarketData;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentMarketData, value);
            if (_pendingGlobalCexSizingApply)
            {
                ApplyGlobalCexSizingIfReady();
            }
            RaiseTradingStateChanged();
            RaisePositionStateChanged();
        }
    }

    public ObservableCollection<CexMarketItemViewModel> Markets { get; } = [];
    public ObservableCollection<CexMarketItemViewModel> VisibleMarkets { get; } = [];
    public ObservableCollection<CexMarketItemViewModel> MarketHeatmapMarkets { get; } = [];
    public ObservableCollection<TradeLadderLevelViewModel> LadderLevels { get; } = [];
    public ObservableCollection<WorkingOrderViewModel> WorkingOrders { get; } = [];
    public ObservableCollection<TradeFillViewModel> RecentFills { get; } = [];
    public ObservableCollection<PositionRowViewModel> PositionRows { get; } = [];
    public ObservableCollection<SignalRowViewModel> SignalRows { get; } = [];
    public ObservableCollection<DexOhlcvPoint> TradingCandles { get; } = [];
    public ObservableCollection<AiAssistantMessageViewModel> AiAssistantMessages { get; } = [];
    public ObservableCollection<AiAssistantQuickPromptViewModel> AiAssistantQuickPrompts { get; } = [];
    public ObservableCollection<AiContextCardViewModel> AiAssistantContextCards { get; } = [];
    public ObservableCollection<AiVisualCardViewModel> AiAssistantVisuals { get; } = [];
    public ObservableCollection<AiKnowledgeTopicViewModel> AiAssistantKnowledgeTopics { get; } = [];
    public ObservableCollection<AiPromptPresetViewModel> AiSavedPromptPresets { get; } = [];
    public ObservableCollection<AiPromptPresetViewModel> AiCustomPromptPresets { get; } = [];

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
            this.RaisePropertyChanged(nameof(SelectedTradingSymbol));
            this.RaisePropertyChanged(nameof(SelectedMarketTitle));
            _ = RefreshSelectedOrderBookAsync();

            if (resolvedMarket is not null)
            {
                resolvedMarket.ApplyTimeframe(SelectedTradeTimeframe);
                CurrentMarketData = new MarketData
                {
                    Symbol = resolvedMarket.Symbol,
                    LastPrice = resolvedMarket.LastPrice,
                    BestBid = resolvedMarket.BestBid,
                    BestAsk = resolvedMarket.BestAsk,
                    Timestamp = resolvedMarket.LastUpdated == default ? DateTime.UtcNow : resolvedMarket.LastUpdated.ToUniversalTime()
                };
            }

            _focusLatestCandlesOnNextRefresh = true;
            _ = RefreshSelectedCandlesAsync();
            if (resolvedMarket is not null && IsManualFuturesMode)
            {
                _ = RefreshManualAccountStateAsync();
            }
            if (resolvedMarket is null)
            {
                RaiseTradingStateChanged();
                RefreshQuickBacktestSnapshot();
            }
        }
    }

    public string SelectedTradingSymbol => SelectedMarket?.Symbol ?? DefaultSymbols[0];
    public string SelectedMarketTitle => SelectedMarket?.DisplaySymbol ?? "Select a coin";
    public TradingVenueMode SelectedTradingVenue
    {
        get => _selectedTradingVenue;
        set => this.RaiseAndSetIfChanged(ref _selectedTradingVenue, value);
    }
    public IReadOnlyList<string> AvailableCexMarketModes { get; } = ["Spot", "Futures"];
    public IReadOnlyList<string> AvailableFuturesMarginModes { get; } = ["Cross", "Isolated"];
    public IReadOnlyList<string> TradingProfileOptions { get; } = ["Balanced", "Scalp"];
    public string SelectedCexMarketMode
    {
        get => _selectedCexMarketMode;
        set
        {
            var normalized = string.Equals(value, "Futures", StringComparison.OrdinalIgnoreCase) ? "Futures" : "Spot";
            if (string.Equals(_selectedCexMarketMode, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedCexMarketMode, normalized);
            EnsureSelectedMarketDisplaySnapshot();
            RaiseTradingModeStateChanged();
            RaiseTradingStateChanged();
            SchedulePassiveManualModeSync();
        }
    }
    public int ManualFuturesLeverage
    {
        get => _manualFuturesLeverage;
        set
        {
            var normalized = Math.Max(1, value);
            if (_manualFuturesLeverage == normalized)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _manualFuturesLeverage, normalized);
            this.RaisePropertyChanged(nameof(CexMarketModeSummary));
        }
    }
    public string ManualFuturesMarginMode
    {
        get => _manualFuturesMarginMode;
        set
        {
            var normalized = string.Equals(value, "Isolated", StringComparison.OrdinalIgnoreCase) ? "Isolated" : "Cross";
            if (string.Equals(_manualFuturesMarginMode, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _manualFuturesMarginMode, normalized);
            this.RaisePropertyChanged(nameof(CexMarketModeSummary));
            this.RaisePropertyChanged(nameof(CurrentFuturesMarginModeLabel));
        }
    }
    public string SelectedTradingProfile
    {
        get => _selectedTradingProfile;
        set
        {
            var normalized = string.Equals(value, "Scalp", StringComparison.OrdinalIgnoreCase) ? "Scalp" : "Balanced";
            if (string.Equals(_selectedTradingProfile, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedTradingProfile, normalized);
            this.RaisePropertyChanged(nameof(IsScalpProfile));
            this.RaisePropertyChanged(nameof(TradingProfileSummary));
            this.RaisePropertyChanged(nameof(ScalpPresetTargetLabel));

            if (string.Equals(normalized, "Scalp", StringComparison.Ordinal))
            {
                ApplyScalpPreset(_selectedScalpPreset);
            }
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
            this.RaisePropertyChanged(nameof(TradingProfileSummary));
            this.RaisePropertyChanged(nameof(ScalpPresetTargetLabel));
        }
    }
    public bool IsManualFuturesMode => string.Equals(SelectedCexMarketMode, "Futures", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> FuturesExchangeOptions { get; } = ["Binance", "Bybit", "OKX", "KuCoin"];

    public string SelectedFuturesExchange
    {
        get => _selectedFuturesExchange;
        set
        {
            var normalized = (value is "Binance" or "Bybit" or "OKX" or "KuCoin") ? value : "Binance";
            if (string.Equals(_selectedFuturesExchange, normalized, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _selectedFuturesExchange, normalized);
            _manualFuturesSetupDone.Clear();
            this.RaisePropertyChanged(nameof(IsFuturesPrivateApiReady));
            this.RaisePropertyChanged(nameof(FuturesPrivateApiStatusLabel));
            this.RaisePropertyChanged(nameof(FuturesPrivateApiStatusBrush));
            this.RaisePropertyChanged(nameof(CexMarketModeSummary));
            this.RaisePropertyChanged(nameof(TradingTerminalSummary));
        }
    }

    private IExchangeGateway ActiveFuturesGateway =>
        _futuresGatewaysMap is not null && _futuresGatewaysMap.TryGetValue(_selectedFuturesExchange, out var gw)
            ? gw
            : _futuresGateway;

    public bool IsScalpProfile => string.Equals(SelectedTradingProfile, "Scalp", StringComparison.OrdinalIgnoreCase);
    public bool IsCexTradingMode => SelectedTradingVenue == TradingVenueMode.Cex;
    public bool IsDexTradingMode => SelectedTradingVenue == TradingVenueMode.Dex;
    public string CexVenueBackground => IsCexTradingMode ? "#17373B" : "#0F1721";
    public string DexVenueBackground => IsDexTradingMode ? "#17373B" : "#0F1721";
    public string CexVenueForeground => IsCexTradingMode ? "#F4F7FB" : "#8FA3B8";
    public string DexVenueForeground => IsDexTradingMode ? "#F4F7FB" : "#8FA3B8";
    public string ActiveTradingSymbol => IsDexTradingMode ? DexTradingVM.SelectedToken?.TokenInfo.Symbol ?? "DEX" : SelectedTradingSymbol;
    public string ActiveTradingTitle => IsDexTradingMode ? DexTradingVM.SelectedTokenTitle : SelectedMarketTitle;
    public string TradingTerminalSummary => IsDexTradingMode
        ? "DEX desk with shared wallet, chart and supported quote routing."
        : IsManualFuturesMode
            ? $"CEX desk for {SelectedFuturesExchange} USD-M futures with two-way manual execution."
            : "CEX desk for Binance spot USDT pairs.";
    public string CexMarketModeSummary => IsManualFuturesMode ? $"{SelectedFuturesExchange} USD-M futures x{ManualFuturesLeverage} · {ManualFuturesMarginMode}" : "Binance spot market";
    public string TradingProfileSummary => IsScalpProfile
        ? $"Scalp {SelectedScalpPreset} · {GetScalpPresetSummary(SelectedScalpPreset)}"
        : "Balanced manual trading profile";
    public FuturesMarginMode SelectedManualFuturesMarginModeEnum =>
        string.Equals(ManualFuturesMarginMode, "Isolated", StringComparison.OrdinalIgnoreCase)
            ? FuturesMarginMode.Isolated
            : FuturesMarginMode.Cross;
    public IReadOnlyList<DexOhlcvPoint> ActiveTradingCandles => IsDexTradingMode ? DexTradingVM.ChartCandles : TradingCandles;
    public int SelectedTradingBottomTabIndex
    {
        get => _selectedTradingBottomTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTradingBottomTabIndex, value);
    }

    public string SelectedOrderSide
    {
        get => _selectedOrderSide;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedOrderSide, value);
            RaiseOrderTicketStateChanged();
        }
    }

    public string SelectedOrderType
    {
        get => _selectedOrderType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedOrderType, value);
            RaiseOrderTicketStateChanged();
        }
    }

    public decimal SlippageTolerancePercent
    {
        get => _slippageTolerancePercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _slippageTolerancePercent, value);
            RaiseOrderTicketStateChanged();
        }
    }

    public string MarketsSearchText
    {
        get => _marketsSearchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _marketsSearchText, value);
            RefreshMarketExplorerCollections();
            RaiseMarketExplorerStateChanged();
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
            RaiseMarketExplorerStateChanged();
        }
    }

    public bool ShowFavoriteMarketsOnly
    {
        get => _showFavoriteMarketsOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showFavoriteMarketsOnly, value);
            RefreshMarketExplorerCollections();
            RaiseMarketExplorerStateChanged();
        }
    }

    public bool IsMarketLoading
    {
        get => _isMarketLoading;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMarketLoading, value);
            this.RaisePropertyChanged(nameof(ConnectionStateLabel));
            this.RaisePropertyChanged(nameof(FeedStatusLabel));
        }
    }

    public IReadOnlyList<string> MarketSortOptions { get; } = ["Momentum", "Spread", "Updated", "Alphabetical", "Price"];
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

    public ReactiveCommand<Unit, Unit> BuyMarketCommand { get; }
    public ReactiveCommand<Unit, Unit> SellMarketCommand { get; }
    public ReactiveCommand<Unit, Unit> PlaceBuyLimitCommand { get; }
    public ReactiveCommand<Unit, Unit> PlaceSellLimitCommand { get; }
    public ReactiveCommand<Unit, Unit> ArmTakeProfitCommand { get; }
    public ReactiveCommand<Unit, Unit> ArmStopLossCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAllOrdersCommand { get; }
    public ReactiveCommand<Unit, Unit> ClosePositionCommand { get; }
    public ReactiveCommand<Unit, Unit> ReversePositionCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenWalletTabCommand { get; }
    public ReactiveCommand<string, Unit> SelectMainTabCommand { get; }
    public ReactiveCommand<Unit, Unit> StartDemoCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenApiKeysFromWelcomeCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissUpdateCommand { get; }
    public ReactiveCommand<string, Unit> SelectOrderSideCommand { get; }
    public ReactiveCommand<Unit, Unit> PlacePrimaryOrderCommand { get; }
    public ReactiveCommand<string, Unit> SelectTradeTimeframeCommand { get; }
    public ReactiveCommand<decimal, Unit> SelectLimitPriceCommand { get; }
    public ReactiveCommand<decimal, Unit> SelectBidPriceCommand { get; }
    public ReactiveCommand<decimal, Unit> SelectAskPriceCommand { get; }
    public ReactiveCommand<string, Unit> SelectTradeAllocationCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseLimitPriceCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseLimitPriceCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyAtrPresetCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyRiskRewardPresetCommand { get; }
    public ReactiveCommand<string, Unit> ApplyScalpPresetCommand { get; }
    public ReactiveCommand<string, Unit> SelectChartToolCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearChartDrawingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetChartViewCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVwapCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVolumeProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLadderCenterModeCommand { get; }
    public ReactiveCommand<string, Unit> SelectTradingVenueCommand { get; }
    public ReactiveCommand<CexMarketItemViewModel, Unit> FocusMarketCommand { get; }
    public ReactiveCommand<CexMarketItemViewModel, Unit> OpenMarketInTradingCommand { get; }
    public ReactiveCommand<CexMarketItemViewModel, Unit> ToggleMarketFavoriteCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshMarketsCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCustomMarketCommand { get; }
    public ReactiveCommand<CexMarketItemViewModel, Unit> RemoveMarketCommand { get; }

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
    public ReactiveCommand<Unit, Unit> SafeLogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SendAiAssistantPromptCommand { get; }
    public ReactiveCommand<AiAssistantQuickPromptViewModel, Unit> UseAiAssistantQuickPromptCommand { get; }
    public ReactiveCommand<AiVisualCardViewModel, Unit> SelectAiVisualCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAiAssistantConversationCommand { get; }
    public ReactiveCommand<Unit, Unit> InjectAiMarketContextCommand { get; }
    public ReactiveCommand<AiKnowledgeTopicViewModel, Unit> ExplainAiKnowledgeTopicCommand { get; }
    public ReactiveCommand<Unit, Unit> AnalyzeSelectedAiVisualCommand { get; }
    public ReactiveCommand<Unit, Unit> BuildAiTradePlanCommand { get; }
    public ReactiveCommand<string, Unit> SelectAiContextVenueModeCommand { get; }
    public ReactiveCommand<string, Unit> ToggleAiContextSourceCommand { get; }
    public ReactiveCommand<Unit, Unit> GenerateAiPresetPromptCommand { get; }
    public ReactiveCommand<AiPromptPresetViewModel, Unit> LoadAiSavedPromptPresetCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAiCustomPromptPresetCommand { get; }
    public ReactiveCommand<AiPromptPresetViewModel, Unit> DeleteAiCustomPromptPresetCommand { get; }
    public ReactiveCommand<AiPromptPresetViewModel, Unit> BuildAiSavedPromptPresetCommand { get; }
    public ReactiveCommand<AiPromptPresetViewModel, Unit> SendAiSavedPromptPresetCommand { get; }

    // ── Trading hotkey commands (single key, no modifier) ────────────────────
    public ReactiveCommand<Unit, Unit> FocusTradingPairCommand { get; }
    public ReactiveCommand<Unit, Unit> HotkeyAlloc25Command    { get; }
    public ReactiveCommand<Unit, Unit> HotkeyAlloc50Command    { get; }
    public ReactiveCommand<Unit, Unit> HotkeyAlloc100Command   { get; }

    // ── Order Template hotkey commands (Shift+1 … Shift+9) ───────────────────
    public ReactiveCommand<Unit, Unit> ApplyTemplate1Command { get; }
    public ReactiveCommand<Unit, Unit> ApplyTemplate2Command { get; }
    public ReactiveCommand<Unit, Unit> ApplyTemplate3Command { get; }
    public ReactiveCommand<Unit, Unit> ApplyTemplate4Command { get; }
    public ReactiveCommand<Unit, Unit> ApplyTemplate5Command { get; }
    public ReactiveCommand<Unit, Unit> ApplyTemplate6Command { get; }
    public ReactiveCommand<Unit, Unit> ApplyTemplate7Command { get; }
    public ReactiveCommand<Unit, Unit> ApplyTemplate8Command { get; }
    public ReactiveCommand<Unit, Unit> ApplyTemplate9Command { get; }

    // ── Wall mode command (implemented in Task 7) ────────────────────────────
    public ReactiveCommand<string, Unit> SetWallModeCommand { get; }

    public decimal TradeQuantity
    {
        get => _tradeQuantity;
        set
        {
            this.RaiseAndSetIfChanged(ref _tradeQuantity, value);
            RaiseTradingStateChanged();
        }
    }

    public decimal LimitPrice
    {
        get => _limitPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _limitPrice, value);
            RaiseOrderTicketStateChanged();
        }
    }

    public decimal TakeProfitPrice
    {
        get => _takeProfitPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _takeProfitPrice, value);
            RaiseOrderTicketStateChanged();
        }
    }

    public decimal StopLossPrice
    {
        get => _stopLossPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _stopLossPrice, value);
            RaiseOrderTicketStateChanged();
        }
    }

    public decimal PriceStep
    {
        get => _priceStep;
        set => this.RaiseAndSetIfChanged(ref _priceStep, value);
    }

    public string SelectedTimeInForce
    {
        get => _selectedTimeInForce;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTimeInForce, value);
            RaiseOrderTicketStateChanged();
        }
    }

    public ObservableCollection<string> TimeInForceOptions { get; } = ["DAY", "GTC", "IOC"];
    public ObservableCollection<string> AvailableTradeTimeframes { get; } = ["1M", "5M", "15M", "1H", "4H", "1D", "1W", "1MN", "ALL"];
    public ObservableCollection<string> AiPromptTradeStyleOptions { get; } = [];
    public ObservableCollection<string> AiPromptHorizonOptions { get; } = [];
    public ObservableCollection<string> AiPromptRiskProfileOptions { get; } = [];
    public ObservableCollection<string> AiPromptFocusOptions { get; } = [];
    public IReadOnlyList<string> OrderTypeOptions { get; } = ["Limit", "Market"];
    public ObservableCollection<ActivityFeedRowViewModel> RecentActivityFeed { get; } = [];

    public string LogMessages
    {
        get => _logMessages;
        set => this.RaiseAndSetIfChanged(ref _logMessages, value);
    }

    public decimal AvailableBalanceUsdt
    {
        get => _availableBalanceUsdt;
        set
        {
            this.RaiseAndSetIfChanged(ref _availableBalanceUsdt, value);
            this.RaisePropertyChanged(nameof(AvailableBalanceLabel));
            this.RaisePropertyChanged(nameof(PortfolioExposureLabel));
            this.RaisePropertyChanged(nameof(AccountEquityLabel));
            if (_pendingGlobalCexSizingApply)
            {
                ApplyGlobalCexSizingIfReady();
            }
        }
    }

    public decimal PositionQuantity
    {
        get => _positionQuantity;
        set
        {
            this.RaiseAndSetIfChanged(ref _positionQuantity, value);
            RaisePositionStateChanged();
        }
    }

    public decimal AverageEntryPrice
    {
        get => _averageEntryPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _averageEntryPrice, value);
            RaisePositionStateChanged();
        }
    }

    public decimal RealizedPnl
    {
        get => _realizedPnl;
        set
        {
            this.RaiseAndSetIfChanged(ref _realizedPnl, value);
            RaiseCexActionStateChanged();
            this.RaisePropertyChanged(nameof(RealizedPnlLabel));
        }
    }

    public string SelectedTradeTimeframe
    {
        get => _selectedTradeTimeframe;
        set => this.RaiseAndSetIfChanged(ref _selectedTradeTimeframe, value);
    }

    public string SelectedChartTool
    {
        get => _selectedChartTool;
        set => this.RaiseAndSetIfChanged(ref _selectedChartTool, value);
    }

    public ChartToolPhase SelectedChartToolPhase
    {
        get => _chartToolPhase;
        set => this.RaiseAndSetIfChanged(ref _chartToolPhase, value);
    }

    public int ChartClearDrawingsVersion
    {
        get => _chartClearDrawingsVersion;
        set => this.RaiseAndSetIfChanged(ref _chartClearDrawingsVersion, value);
    }

    public int ChartResetViewVersion
    {
        get => _chartResetViewVersion;
        set => this.RaiseAndSetIfChanged(ref _chartResetViewVersion, value);
    }

    public bool ShowChartVwap
    {
        get => _showChartVwap;
        set => this.RaiseAndSetIfChanged(ref _showChartVwap, value);
    }

    public bool ShowChartVolumeProfile
    {
        get => _showChartVolumeProfile;
        set => this.RaiseAndSetIfChanged(ref _showChartVolumeProfile, value);
    }

    public string ChartVwapBackground         => _showChartVwap          ? "#17373B" : "#0F1721";
    public string ChartVwapForeground         => _showChartVwap          ? "#FBBF24" : "#8FA3B8";
    public string ChartVolumeProfileBackground => _showChartVolumeProfile ? "#21173B" : "#0F1721";
    public string ChartVolumeProfileForeground => _showChartVolumeProfile ? "#A855F7" : "#8FA3B8";


    public string AvailableBalanceLabel => $"{AvailableBalanceUsdt:N2} USDT";
    public string GlobalPositionSizingLabel => WalletVM.GlobalPositionSizingLabel;
    public string GlobalPositionSizingSummary => WalletVM.GlobalPositionSizingSummary;
    public string GlobalExecutionModeLabel => WalletVM.GlobalExecutionModeLabel;
    public string GlobalExecutionModeBrush => WalletVM.GlobalExecutionModeBrush;
    public string GlobalExecutionSummary => WalletVM.GlobalExecutionSummary;
    public string GlobalRiskCapLabel => WalletVM.GlobalRiskCapLabel;
    public string GlobalRiskSummary => WalletVM.GlobalRiskSummary;
    public string RiskModeLabel => WalletVM.GlobalPositionSizingPercent switch
    {
        <= 25m => "MODERATE",
        <= 60m => "BALANCED",
        _ => "AGGRESSIVE"
    };
    public string RiskModeBrush => WalletVM.GlobalPositionSizingPercent switch
    {
        <= 25m => "#F4B860",
        <= 60m => "#21E6C1",
        _ => "#FF6B6B"
    };
    public string RiskModeSummaryLabel => WalletVM.GlobalPaperOnlyMode ? "Execution guard on" : "Live routing enabled";
    public string ConnectionStateLabel => IsMarketLoading ? "Connecting" : "Connected";
    public string ConnectionStateDetailLabel => SelectedMarket?.DisplaySymbol ?? "Binance Stream";
    public decimal EffectivePositionMarkPrice => IsManualFuturesMode && _currentFuturesMarkPrice > 0 ? _currentFuturesMarkPrice : CurrentTradePrice;
    public decimal CurrentOpenExposureUsdt => Math.Abs(PositionQuantity) * EffectivePositionMarkPrice;
    public string CurrentOpenExposureLabel => $"{CurrentOpenExposureUsdt:0.##} USDT";
    public string CurrentDailyLossLabel => $"{Math.Max(0m, -RealizedPnl):0.##} USDT";
    public string BaseAssetSymbol => SelectedTradingSymbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
        ? SelectedTradingSymbol[..^4]
        : SelectedTradingSymbol;
    public decimal CurrentTradePrice => CurrentMarketData?.LastPrice > 0
        ? CurrentMarketData.LastPrice
        : LatestTradingCandle?.Close > 0
            ? LatestTradingCandle.Close
            : SelectedMarket?.LastPrice ?? 0m;
    public decimal TradeNotional => TradeQuantity * CurrentTradePrice;
    public string TradeNotionalLabel => TradeNotional <= 0 ? "--" : $"~ {TradeNotional:N2} USDT";
    public string PositionStatusLabel => PositionQuantity > 0
        ? $"LONG {PositionQuantity:0.0000} {BaseAssetSymbol}"
        : PositionQuantity < 0
            ? $"SHORT {Math.Abs(PositionQuantity):0.0000} {BaseAssetSymbol}"
            : "FLAT";
    public bool HasOpenManualPosition => PositionQuantity != 0;
    public string EntryPriceLabel => AverageEntryPrice > 0 ? $"{AverageEntryPrice:N2}" : "--";
    public decimal UnrealizedPnl => PositionQuantity != 0 && AverageEntryPrice > 0
        ? (EffectivePositionMarkPrice - AverageEntryPrice) * PositionQuantity
        : 0m;
    public string UnrealizedPnlLabel => $"{UnrealizedPnl:+0.00;-0.00;0.00} USDT";
    public string ExchangeUnrealizedPnlLabel => $"{_currentFuturesExchangeUnrealizedPnl:+0.00;-0.00;0.00} USDT";
    public string CurrentFuturesMarkPriceLabel => _currentFuturesMarkPrice > 0 ? $"{_currentFuturesMarkPrice:N2}" : "--";
    public string CurrentFuturesLiquidationLabel => _currentFuturesLiquidationPrice > 0 ? $"{_currentFuturesLiquidationPrice:N2}" : "--";
    public string CurrentFuturesMarginModeLabel => IsManualFuturesMode ? ManualFuturesMarginMode.ToUpperInvariant() : "--";
    public string CurrentFuturesLeverageLabel => IsManualFuturesMode ? $"{ManualFuturesLeverage}x" : "--";
    public bool IsFuturesPrivateApiReady => ActiveFuturesGateway.HasPrivateApiCredentials;
    public string FuturesPrivateApiStatusLabel => IsFuturesPrivateApiReady ? "Private API Ready" : "Private API Missing";
    public string FuturesPrivateApiStatusBrush => IsFuturesPrivateApiReady ? "#3DDC84" : "#FF6B6B";

    // ── API Credentials (editable in Settings) ───────────────────────────────

    private CredentialsService.CredentialSource _binanceCredSource;
    private CredentialsService.CredentialSource _bybitCredSource;
    private CredentialsService.CredentialSource _okxCredSource;
    private CredentialsService.CredentialSource _kucoinCredSource;
    private string _loadedBinanceKey = "";   // resolved at startup (env or file)
    private string _loadedBybitKey  = "";
    private string _loadedOkxKey    = "";
    private string _loadedKucoinKey = "";

    // Input backing fields (pre-filled from file; empty for env-var-only keys)
    private string _binanceKeyInput        = "";
    private string _binanceSecretInput     = "";
    private string _bybitKeyInput          = "";
    private string _bybitSecretInput       = "";
    private string _okxKeyInput            = "";
    private string _okxSecretInput         = "";
    private string _okxPassphraseInput     = "";
    private string _kucoinKeyInput         = "";
    private string _kucoinSecretInput      = "";
    private string _kucoinPassphraseInput  = "";

    // Show/hide toggles for each secret field
    private bool _isShowingBinanceKey         = false;
    private bool _isShowingBinanceSecret      = false;
    private bool _isShowingBybitKey           = false;
    private bool _isShowingBybitSecret        = false;
    private bool _isShowingOkxKey             = false;
    private bool _isShowingOkxSecret          = false;
    private bool _isShowingOkxPassphrase      = false;
    private bool _isShowingKucoinKey          = false;
    private bool _isShowingKucoinSecret       = false;
    private bool _isShowingKucoinPassphrase   = false;

    // Help panel visibility
    private bool   _isBinanceHelpVisible = false;
    private bool   _isBybitHelpVisible  = false;
    private bool   _isOkxHelpVisible    = false;
    private bool   _isKucoinHelpVisible = false;

    // Save status messages
    private string _binanceSaveStatus = "";
    private string _bybitSaveStatus  = "";
    private string _okxSaveStatus    = "";
    private string _kucoinSaveStatus = "";

    // Affiliate links
    private string _binanceAffiliateUrl = "";
    private string _bybitAffiliateUrl   = "";
    private string _okxAffiliateUrl     = "";
    private string _kucoinAffiliateUrl  = "";
    private string _affiliateSaveStatus = "";

    // ── Editable input properties ─────────────────────────────────────────────

    public string BinanceKeyInput
    {
        get => _binanceKeyInput;
        set => this.RaiseAndSetIfChanged(ref _binanceKeyInput, value ?? "");
    }
    public string BinanceSecretInput
    {
        get => _binanceSecretInput;
        set => this.RaiseAndSetIfChanged(ref _binanceSecretInput, value ?? "");
    }

    public string BybitKeyInput
    {
        get => _bybitKeyInput;
        set => this.RaiseAndSetIfChanged(ref _bybitKeyInput, value ?? "");
    }
    public string BybitSecretInput
    {
        get => _bybitSecretInput;
        set => this.RaiseAndSetIfChanged(ref _bybitSecretInput, value ?? "");
    }
    public string OkxKeyInput
    {
        get => _okxKeyInput;
        set => this.RaiseAndSetIfChanged(ref _okxKeyInput, value ?? "");
    }
    public string OkxSecretInput
    {
        get => _okxSecretInput;
        set => this.RaiseAndSetIfChanged(ref _okxSecretInput, value ?? "");
    }
    public string OkxPassphraseInput
    {
        get => _okxPassphraseInput;
        set => this.RaiseAndSetIfChanged(ref _okxPassphraseInput, value ?? "");
    }
    public string KucoinKeyInput
    {
        get => _kucoinKeyInput;
        set => this.RaiseAndSetIfChanged(ref _kucoinKeyInput, value ?? "");
    }
    public string KucoinSecretInput
    {
        get => _kucoinSecretInput;
        set => this.RaiseAndSetIfChanged(ref _kucoinSecretInput, value ?? "");
    }
    public string KucoinPassphraseInput
    {
        get => _kucoinPassphraseInput;
        set => this.RaiseAndSetIfChanged(ref _kucoinPassphraseInput, value ?? "");
    }

    // ── Visibility toggles ────────────────────────────────────────────────────

    public bool IsShowingBinanceKey
    {
        get => _isShowingBinanceKey;
        private set => this.RaiseAndSetIfChanged(ref _isShowingBinanceKey, value);
    }
    public bool IsShowingBinanceSecret
    {
        get => _isShowingBinanceSecret;
        private set => this.RaiseAndSetIfChanged(ref _isShowingBinanceSecret, value);
    }

    public bool IsShowingBybitKey
    {
        get => _isShowingBybitKey;
        private set => this.RaiseAndSetIfChanged(ref _isShowingBybitKey, value);
    }
    public bool IsShowingBybitSecret
    {
        get => _isShowingBybitSecret;
        private set => this.RaiseAndSetIfChanged(ref _isShowingBybitSecret, value);
    }
    public bool IsShowingOkxKey
    {
        get => _isShowingOkxKey;
        private set => this.RaiseAndSetIfChanged(ref _isShowingOkxKey, value);
    }
    public bool IsShowingOkxSecret
    {
        get => _isShowingOkxSecret;
        private set => this.RaiseAndSetIfChanged(ref _isShowingOkxSecret, value);
    }
    public bool IsShowingOkxPassphrase
    {
        get => _isShowingOkxPassphrase;
        private set => this.RaiseAndSetIfChanged(ref _isShowingOkxPassphrase, value);
    }
    public bool IsShowingKucoinKey
    {
        get => _isShowingKucoinKey;
        private set => this.RaiseAndSetIfChanged(ref _isShowingKucoinKey, value);
    }
    public bool IsShowingKucoinSecret
    {
        get => _isShowingKucoinSecret;
        private set => this.RaiseAndSetIfChanged(ref _isShowingKucoinSecret, value);
    }
    public bool IsShowingKucoinPassphrase
    {
        get => _isShowingKucoinPassphrase;
        private set => this.RaiseAndSetIfChanged(ref _isShowingKucoinPassphrase, value);
    }

    // ── Help panel ────────────────────────────────────────────────────────────

    public bool IsBinanceHelpVisible
    {
        get => _isBinanceHelpVisible;
        private set => this.RaiseAndSetIfChanged(ref _isBinanceHelpVisible, value);
    }

    public bool IsBybitHelpVisible
    {
        get => _isBybitHelpVisible;
        private set => this.RaiseAndSetIfChanged(ref _isBybitHelpVisible, value);
    }
    public bool IsOkxHelpVisible
    {
        get => _isOkxHelpVisible;
        private set => this.RaiseAndSetIfChanged(ref _isOkxHelpVisible, value);
    }
    public bool IsKucoinHelpVisible
    {
        get => _isKucoinHelpVisible;
        private set => this.RaiseAndSetIfChanged(ref _isKucoinHelpVisible, value);
    }

    // ── Save status ───────────────────────────────────────────────────────────

    public string BinanceSaveStatus
    {
        get => _binanceSaveStatus;
        private set => this.RaiseAndSetIfChanged(ref _binanceSaveStatus, value);
    }

    public string BybitSaveStatus
    {
        get => _bybitSaveStatus;
        private set => this.RaiseAndSetIfChanged(ref _bybitSaveStatus, value);
    }
    public string OkxSaveStatus
    {
        get => _okxSaveStatus;
        private set => this.RaiseAndSetIfChanged(ref _okxSaveStatus, value);
    }
    public string KucoinSaveStatus
    {
        get => _kucoinSaveStatus;
        private set => this.RaiseAndSetIfChanged(ref _kucoinSaveStatus, value);
    }

    // ── Affiliate link properties ─────────────────────────────────────────────

    public string BinanceAffiliateUrl
    {
        get => _binanceAffiliateUrl;
        set => this.RaiseAndSetIfChanged(ref _binanceAffiliateUrl, value);
    }
    public string BybitAffiliateUrl
    {
        get => _bybitAffiliateUrl;
        set => this.RaiseAndSetIfChanged(ref _bybitAffiliateUrl, value);
    }
    public string OkxAffiliateUrl
    {
        get => _okxAffiliateUrl;
        set => this.RaiseAndSetIfChanged(ref _okxAffiliateUrl, value);
    }
    public string KucoinAffiliateUrl
    {
        get => _kucoinAffiliateUrl;
        set => this.RaiseAndSetIfChanged(ref _kucoinAffiliateUrl, value);
    }
    public string AffiliateSaveStatus
    {
        get => _affiliateSaveStatus;
        private set => this.RaiseAndSetIfChanged(ref _affiliateSaveStatus, value);
    }

    // ── AI provider (Claude / ChatGPT) selection ──────────────────────────────
    private bool _aiIsClaude = true;
    private bool _aiIsChatGpt;
    private string _anthropicKeyInput = string.Empty;
    private string _openAiKeyInput = string.Empty;
    private string _anthropicModelInput = string.Empty;
    private string _openAiModelInput = string.Empty;
    private string _aiSettingsStatus = string.Empty;

    /// <summary>Claude is the active AI provider (two-way bound to the Claude radio button).</summary>
    public bool AiIsClaude
    {
        get => _aiIsClaude;
        set { this.RaiseAndSetIfChanged(ref _aiIsClaude, value); if (value && _aiIsChatGpt) AiIsChatGpt = false; }
    }

    /// <summary>ChatGPT is the active AI provider (two-way bound to the ChatGPT radio button).</summary>
    public bool AiIsChatGpt
    {
        get => _aiIsChatGpt;
        set { this.RaiseAndSetIfChanged(ref _aiIsChatGpt, value); if (value && _aiIsClaude) AiIsClaude = false; }
    }

    public string AnthropicKeyInput
    {
        get => _anthropicKeyInput;
        set => this.RaiseAndSetIfChanged(ref _anthropicKeyInput, value);
    }

    public string OpenAiKeyInput
    {
        get => _openAiKeyInput;
        set => this.RaiseAndSetIfChanged(ref _openAiKeyInput, value);
    }

    public string AnthropicModelInput
    {
        get => _anthropicModelInput;
        set => this.RaiseAndSetIfChanged(ref _anthropicModelInput, value);
    }

    public string OpenAiModelInput
    {
        get => _openAiModelInput;
        set => this.RaiseAndSetIfChanged(ref _openAiModelInput, value);
    }

    public string AiSettingsStatus
    {
        get => _aiSettingsStatus;
        private set => this.RaiseAndSetIfChanged(ref _aiSettingsStatus, value);
    }

    /// <summary>
    /// Pushes the active provider's key/model into the master AIBot field. Its
    /// PropertyChanged handler fans the value out to every AI sub-VM, so the
    /// autonomous trader, signal bot and sniper verdict all switch provider too.
    /// </summary>
    private void ApplyActiveAiKeyToAgents()
    {
        AIBotVM.ClaudeApiKey = CryptoAITerminal.AIEngine.AiRuntime.ActiveApiKey;
        AIBotVM.ClaudeModel  = CryptoAITerminal.AIEngine.AiRuntime.ActiveModel;
    }

    /// <summary>Opens a URL in the user's default browser (web login / API-key console).</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no browser available — non-fatal */ }
    }

    // ── Global AI command bar (Ctrl+K) ──────────────────────────────────────────
    private readonly AiCommandPaletteService _palette = new();
    private bool _isCommandPaletteOpen;
    private string _commandPaletteInput = string.Empty;
    private string _commandPaletteResult = string.Empty;
    private bool _commandPaletteBusy;

    public bool IsCommandPaletteOpen
    {
        get => _isCommandPaletteOpen;
        set => this.RaiseAndSetIfChanged(ref _isCommandPaletteOpen, value);
    }

    public string CommandPaletteInput
    {
        get => _commandPaletteInput;
        set => this.RaiseAndSetIfChanged(ref _commandPaletteInput, value ?? string.Empty);
    }

    public string CommandPaletteResult
    {
        get => _commandPaletteResult;
        private set => this.RaiseAndSetIfChanged(ref _commandPaletteResult, value);
    }

    public bool CommandPaletteBusy
    {
        get => _commandPaletteBusy;
        private set => this.RaiseAndSetIfChanged(ref _commandPaletteBusy, value);
    }

    /// <summary>
    /// Runs the Ctrl+K command bar. Navigation intents jump to the section instantly
    /// (deterministic, offline-safe); everything else is answered by the AI copilot
    /// inline. Per the chosen autonomy level the bar never trades — it advises and navigates.
    /// </summary>
    private async System.Threading.Tasks.Task RunCommandPaletteAsync()
    {
        var text = (CommandPaletteInput ?? string.Empty).Trim();
        if (text.Length == 0 || CommandPaletteBusy) return;

        var parsed = _palette.Parse(text);

        if (parsed.Intent == AiCommandPaletteService.Intent.Navigate && parsed.SectionKey is not null)
        {
            SelectMainTab(parsed.SectionKey);
            CommandPaletteResult = $"→ {parsed.SectionLabel}";
            IsCommandPaletteOpen = false;
            return;
        }

        // Question → AI copilot (live provider when keyed, offline assistant otherwise).
        CommandPaletteBusy = true;
        CommandPaletteResult = "Thinking…";
        try
        {
            var answer = await CopilotVM.AskInlineAsync(text).ConfigureAwait(true);
            CommandPaletteResult = answer.Text;
        }
        catch (Exception ex)
        {
            CommandPaletteResult = $"Error: {ex.Message}";
        }
        finally
        {
            CommandPaletteBusy = false;
        }
    }

    // ── Status / mask display ─────────────────────────────────────────────────

    public string BinanceApiKeyMask => MaskKey(_loadedBinanceKey);
    public string BinanceApiStatus  => _loadedBinanceKey.Length > 0 ? "Key loaded ✓" : "No key — read-only";
    public string BinanceCredentialSourceLabel => _binanceCredSource switch
    {
        CredentialsService.CredentialSource.Env  => "ENV",
        CredentialsService.CredentialSource.File => "FILE",
        _                                        => "—",
    };

    public Avalonia.Media.IBrush BinanceCredentialSourceBadgeBrush => _binanceCredSource switch
    {
        CredentialsService.CredentialSource.Env  => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#21C67B")),
        CredentialsService.CredentialSource.File => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3D8BCD")),
        _                                        => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A4F63")),
    };

    public bool IsBinanceCredSourceEnv => _binanceCredSource == CredentialsService.CredentialSource.Env;

    public string BybitApiKeyMask => MaskKey(_loadedBybitKey);
    public string BybitApiStatus  => _loadedBybitKey.Length > 0 ? "Key loaded ✓" : "No key — read-only";
    public string BybitCredentialSourceLabel => _bybitCredSource switch
    {
        CredentialsService.CredentialSource.Env  => "ENV",
        CredentialsService.CredentialSource.File => "FILE",
        _                                        => "—",
    };

    public Avalonia.Media.IBrush BybitCredentialSourceBadgeBrush => _bybitCredSource switch
    {
        CredentialsService.CredentialSource.Env  => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#21C67B")),
        CredentialsService.CredentialSource.File => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3D8BCD")),
        _                                        => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A4F63")),
    };

    public bool IsBybitCredSourceEnv => _bybitCredSource == CredentialsService.CredentialSource.Env;

    public string OkxApiKeyMask => MaskKey(_loadedOkxKey);
    public string OkxApiStatus  => _loadedOkxKey.Length > 0 ? "Key loaded ✓" : "No key — read-only";
    public string OkxPassphraseStatus => "";  // kept for XAML compat, not used in new UI
    public string OkxCredentialSourceLabel => _okxCredSource switch
    {
        CredentialsService.CredentialSource.Env  => "ENV",
        CredentialsService.CredentialSource.File => "FILE",
        _                                        => "—",
    };

    public Avalonia.Media.IBrush OkxCredentialSourceBadgeBrush => _okxCredSource switch
    {
        CredentialsService.CredentialSource.Env  => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#21C67B")),
        CredentialsService.CredentialSource.File => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3D8BCD")),
        _                                        => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A4F63")),
    };

    public bool IsOkxCredSourceEnv => _okxCredSource == CredentialsService.CredentialSource.Env;

    public string KucoinApiKeyMask => MaskKey(_loadedKucoinKey);
    public string KucoinApiStatus  => _loadedKucoinKey.Length > 0 ? "Key loaded ✓" : "No key — read-only";
    public string KucoinCredentialSourceLabel => _kucoinCredSource switch
    {
        CredentialsService.CredentialSource.Env  => "ENV",
        CredentialsService.CredentialSource.File => "FILE",
        _                                        => "—",
    };

    public Avalonia.Media.IBrush KucoinCredentialSourceBadgeBrush => _kucoinCredSource switch
    {
        CredentialsService.CredentialSource.Env  => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#21C67B")),
        CredentialsService.CredentialSource.File => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3D8BCD")),
        _                                        => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A4F63")),
    };

    public bool IsKucoinCredSourceEnv => _kucoinCredSource == CredentialsService.CredentialSource.Env;

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SaveBinanceCredentialsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveBybitCredentialsCommand  { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveOkxCredentialsCommand    { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveKucoinCredentialsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleBinanceHelpCommand      { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleBybitHelpCommand        { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleOkxHelpCommand          { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleKucoinHelpCommand       { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleBinanceKeyVisibilityCommand      { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleBinanceSecretVisibilityCommand   { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleBybitKeyVisibilityCommand        { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleBybitSecretVisibilityCommand     { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleOkxKeyVisibilityCommand          { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleOkxSecretVisibilityCommand       { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleOkxPassphraseVisibilityCommand   { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleKucoinKeyVisibilityCommand        { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleKucoinSecretVisibilityCommand     { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleKucoinPassphraseVisibilityCommand { get; private set; } = null!;

    // ── Affiliate links ────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> OpenBinanceAffiliateCommand  { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> OpenBybitAffiliateCommand    { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> OpenOkxAffiliateCommand      { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> OpenKucoinAffiliateCommand   { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveAffiliateLinksCommand    { get; private set; } = null!;

    // ── AI provider (Claude / ChatGPT) ──────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> SaveAiSettingsCommand        { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> OpenAnthropicConsoleCommand  { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> OpenOpenAiConsoleCommand     { get; private set; } = null!;

    // ── Global AI command bar (Ctrl+K) ──────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> OpenCommandPaletteCommand    { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CloseCommandPaletteCommand   { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RunCommandPaletteCommand     { get; private set; } = null!;

    // ── Configuration Profiles ────────────────────────────────────────────────
    private string _profileName = "default";
    private string _profileStatus = string.Empty;

    public string ProfileName
    {
        get => _profileName;
        set => this.RaiseAndSetIfChanged(ref _profileName, value ?? "default");
    }

    public string ProfileStatus
    {
        get => _profileStatus;
        private set => this.RaiseAndSetIfChanged(ref _profileStatus, value);
    }

    public IReadOnlyList<string> AvailableProfiles => ProfileService.ListProfiles();

    public ReactiveCommand<Unit, Unit> SaveProfileCommand  { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> LoadProfileCommand  { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExportProfileCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ImportProfileCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DeleteProfileCommand { get; private set; } = null!;

    private static string MaskKey(string key) =>
        string.IsNullOrWhiteSpace(key)
            ? "not set"
            : key[..Math.Min(4, key.Length)] + new string('●', Math.Min(12, key.Length - 4));
    public decimal CurrentFuturesEstimatedMargin => IsManualFuturesMode && ManualFuturesLeverage > 0
        ? Math.Abs(PositionQuantity * AverageEntryPrice) / ManualFuturesLeverage
        : 0m;
    public string CurrentFuturesEstimatedMarginLabel => CurrentFuturesEstimatedMargin > 0 ? $"{CurrentFuturesEstimatedMargin:N2} USDT" : "--";
    public decimal CurrentFuturesRoePercent => CurrentFuturesEstimatedMargin <= 0 ? 0m : (UnrealizedPnl / CurrentFuturesEstimatedMargin) * 100m;
    public string CurrentFuturesRoeLabel => $"{CurrentFuturesRoePercent:+0.##;-0.##;0.##}%";
    public string RealizedPnlLabel => $"{RealizedPnl:+0.00;-0.00;0.00} USDT";
    public string ScalpPresetTargetLabel => GetScalpPresetSummary(SelectedScalpPreset);
    public string PortfolioExposureLabel => TradeNotional <= 0 || AvailableBalanceUsdt <= 0
        ? "0%"
        : $"{Math.Min(100m, TradeNotional / AvailableBalanceUsdt * 100m):N1}%";
    public decimal BidDepthTotal => SelectedMarket?.BidLevels.Sum(level => level.Quantity) ?? 0m;
    public decimal AskDepthTotal => SelectedMarket?.AskLevels.Sum(level => level.Quantity) ?? 0m;
    public string BidDepthLabel => $"{BidDepthTotal:N4}";
    public string AskDepthLabel => $"{AskDepthTotal:N4}";
    public string DepthBalanceLabel => $"{BidDepthLabel} / {AskDepthLabel}";
    public DexOhlcvPoint? LatestTradingCandle => TradingCandles.LastOrDefault();
    public decimal SpreadValue => SelectedMarket?.Spread ?? 0m;
    public string SpreadLabel => SpreadValue > 0 ? $"{SpreadValue:N2}" : "--";
    public decimal SpreadPercent => CurrentTradePrice > 0 ? SpreadValue / CurrentTradePrice * 100m : 0m;
    public string SpreadPercentLabel => $"{SpreadPercent:N3}%";
    public string ChartHighLabel => LatestTradingCandle is null ? "--" : $"{LatestTradingCandle.High:N2}";
    public string ChartLowLabel => LatestTradingCandle is null ? "--" : $"{LatestTradingCandle.Low:N2}";
    public string ChartVolumeLabel => LatestTradingCandle is null ? "--" : $"{LatestTradingCandle.Volume:N2}";
    public string PreferredBookSideLabel => _preferredBookSide;
    public string PreferredBookSideBrush => _preferredBookSide == "BUY" ? "#3DDC84" : "#FF6B6B";
    public string LadderModeLabel => _isLadderCenterLocked ? "LOCK CENTER" : "FREE SCROLL";
    public string LadderModeBrush => _isLadderCenterLocked ? "#21E6C1" : "#F4B860";
    public string LadderOffsetLabel => _isLadderCenterLocked || _ladderManualOffsetTicks == 0 ? "Offset 0" : $"Offset {_ladderManualOffsetTicks:+#;-#;0}";
    public string LimitPriceLabel => LimitPrice > 0 ? $"{LimitPrice:N2}" : "--";
    public string TakeProfitLabel => TakeProfitPrice > 0 ? $"{TakeProfitPrice:N2}" : "--";
    public string StopLossLabel => StopLossPrice > 0 ? $"{StopLossPrice:N2}" : "--";
    public string WorkingOrdersCountLabel => WorkingOrders.Count.ToString();
    public string RecentFillsCountLabel => RecentFills.Count.ToString();
    public string PositionsCountLabel => PositionRows.Count.ToString();
    public string SignalsCountLabel => SignalRows.Count.ToString();
    public bool HasWorkingOrders => WorkingOrders.Count > 0;
    public bool HasRecentFills => RecentFills.Count > 0;
    public bool HasPositionRows => PositionRows.Count > 0;
    public bool HasSignalRows => SignalRows.Count > 0;
    public bool HasRecentActivityFeed => RecentActivityFeed.Count > 0;
    public bool ShowWorkingOrdersPlaceholder => !HasWorkingOrders;
    public bool ShowRecentFillsPlaceholder => !HasRecentFills;
    public bool ShowPositionRowsPlaceholder => !HasPositionRows;
    public bool ShowSignalRowsPlaceholder => !HasSignalRows;
    public bool ShowRecentActivityPlaceholder => !HasRecentActivityFeed;
    public string ExecutionModeLabel => WalletVM.GlobalExecutionModeLabel;
    public bool CanExecuteCexMarketBuy => string.IsNullOrWhiteSpace(CexMarketBuyBlockedReason);
    public bool CanExecuteCexMarketSell => string.IsNullOrWhiteSpace(CexMarketSellBlockedReason);
    public bool CanExecuteCexBuyLimit => string.IsNullOrWhiteSpace(CexBuyLimitBlockedReason);
    public bool CanExecuteCexSellLimit => string.IsNullOrWhiteSpace(CexSellLimitBlockedReason);
    public bool CanExecuteCexTakeProfit => string.IsNullOrWhiteSpace(CexTakeProfitBlockedReason);
    public bool CanExecuteCexStopLoss => string.IsNullOrWhiteSpace(CexStopLossBlockedReason);
    public bool CanExecuteCexClose => string.IsNullOrWhiteSpace(CexCloseBlockedReason);
    public bool CanExecuteCexReverse => string.IsNullOrWhiteSpace(CexReverseBlockedReason);
    public bool CanPlacePrimaryOrder => string.IsNullOrWhiteSpace(PrimaryOrderBlockedReason);
    public string CexMarketBuyBlockedReason => GetCexMarketBuyBlockedReason();
    public string CexMarketSellBlockedReason => GetCexMarketSellBlockedReason();
    public string CexBuyLimitBlockedReason => GetCexBuyLimitBlockedReason();
    public string CexSellLimitBlockedReason => GetCexSellLimitBlockedReason();
    public string CexTakeProfitBlockedReason => GetCexTakeProfitBlockedReason();
    public string CexStopLossBlockedReason => GetCexStopLossBlockedReason();
    public string CexCloseBlockedReason => GetCexCloseBlockedReason();
    public string CexReverseBlockedReason => GetCexReverseBlockedReason();
    public string PrimaryOrderBlockedReason => GetPrimaryOrderBlockedReason();
    public string TradingGuardStatusLabel => CanPlacePrimaryOrder ? "ORDER PATH READY" : "EXECUTION BLOCKED";
    public string TradingGuardStatusBrush => CanPlacePrimaryOrder ? "#21E6C1" : "#F4B860";
    public string TradingGuardSummary => CanPlacePrimaryOrder
        ? $"{SelectedOrderType} {SelectedOrderSide} ticket is armed for {SelectedTradingSymbol}. Guard mode: {WalletVM.GlobalExecutionModeLabel}."
        : PrimaryOrderBlockedReason;
    public string TradingGuardDetail => IsManualFuturesMode
        ? $"{FuturesPrivateApiStatusLabel} | {WalletVM.RouteReadinessSummary}"
        : $"{WalletVM.RouteReadinessSummary} | {WalletVM.GlobalRiskSummary}";
    public string FeedStatusLabel => IsMarketLoading ? "CONNECTING" : "LIVE DATA";
    public string FlatStatusLabel => PositionQuantity > 0
        ? $"LONG {PositionQuantity:0.0000}"
        : PositionQuantity < 0
            ? $"SHORT {Math.Abs(PositionQuantity):0.0000}"
            : "FLAT";
    public string EntryCompactLabel => AverageEntryPrice > 0 ? $"{AverageEntryPrice:N2}" : "—";
    public string PnlCompactLabel => $"{(UnrealizedPnl + RealizedPnl):+0.00;-0.00;0.00}";
    public string PnlCompactBrush => (UnrealizedPnl + RealizedPnl) >= 0 ? "#3DDC84" : "#FF6B6B";
    public string SelectedShellSection => _selectedShellSection;
    public string DashboardNavBackground => GetShellNavBackground(0);
    public string TradingNavBackground => GetShellNavBackground(2);
    public string PortfolioNavBackground => GetShellNavBackground(3);
    public string SignalsNavBackground => GetShellNavBackground(4);
    public string LogsNavBackground => GetShellNavBackground(7);
    public string DashboardNavForeground => GetShellNavForeground(0);
    public string TradingNavForeground => GetShellNavForeground(2);
    public string PortfolioNavForeground => GetShellNavForeground(3);
    public string SignalsNavForeground => GetShellNavForeground(4);
    public string LogsNavForeground => GetShellNavForeground(7);
    public string SniperNavBackground => GetShellSectionBackground("sniper");
    public string SniperNavForeground => GetShellSectionForeground("sniper");
    public string MarketsNavBackground => GetShellSectionBackground("markets");
    public string MarketsNavForeground => GetShellSectionForeground("markets");
    public string RiskNavBackground => GetShellSectionBackground("risk");
    public string RiskNavForeground => GetShellSectionForeground("risk");
    public string BacktestNavBackground => GetShellSectionBackground("backtest");
    public string BacktestNavForeground => GetShellSectionForeground("backtest");
    public string BotsNavBackground => GetShellSectionBackground("bots");
    public string BotsNavForeground => GetShellSectionForeground("bots");
    public string AnalyticsNavBackground => GetShellSectionBackground("analytics");
    public string AnalyticsNavForeground => GetShellSectionForeground("analytics");
    public string SettingsNavBackground => GetShellSectionBackground("settings");
    public string SettingsNavForeground => GetShellSectionForeground("settings");
    public string HelpNavBackground => GetShellSectionBackground("help");
    public string HelpNavForeground => GetShellSectionForeground("help");
    public string LogoutNavBackground => GetShellSectionBackground("logout");
    public string LogoutNavForeground => GetShellSectionForeground("logout");
    public bool IsRiskSectionVisible => IsWorkspaceSection("risk");
    public bool IsBacktestSectionVisible => IsWorkspaceSection("backtest");
    public bool IsBotsSectionVisible => IsWorkspaceSection("bots");
    public bool IsAlertsSectionVisible => IsWorkspaceSection("alerts");
    public string AlertsNavBackground => GetShellSectionBackground("alerts");
    public string AlertsNavForeground => GetShellSectionForeground("alerts");
    public bool IsAnalyticsSectionVisible => IsWorkspaceSection("analytics");
    public bool IsWhaleTrackerSectionVisible => IsWorkspaceSection("whale");
    public string WhaleNavBackground => GetShellSectionBackground("whale");
    public string WhaleNavForeground => GetShellSectionForeground("whale");
    public bool IsTapeSectionVisible => IsWorkspaceSection("tape");
    public string TapeNavBackground => GetShellSectionBackground("tape");
    public string TapeNavForeground => GetShellSectionForeground("tape");
    public bool IsFundingRateSectionVisible => IsWorkspaceSection("funding");
    public string FundingNavBackground => GetShellSectionBackground("funding");
    public string FundingNavForeground => GetShellSectionForeground("funding");
    public bool IsArbSectionVisible    => IsWorkspaceSection("arb");
    public string ArbNavBackground     => GetShellSectionBackground("arb");
    public string ArbNavForeground     => GetShellSectionForeground("arb");
    public bool   IsRouterSectionVisible  => IsWorkspaceSection("router");
    public string RouterNavBackground   => GetShellSectionBackground("router");
    public string RouterNavForeground   => GetShellSectionForeground("router");
    public bool   IsScannerSectionVisible => IsWorkspaceSection("scanner");
    public string ScannerNavBackground  => GetShellSectionBackground("scanner");
    public string ScannerNavForeground  => GetShellSectionForeground("scanner");
    public bool IsLiquidationSectionVisible => IsWorkspaceSection("liquidation");
    public string LiquidationNavBackground => GetShellSectionBackground("liquidation");
    public string LiquidationNavForeground => GetShellSectionForeground("liquidation");
    public bool   IsRulesSectionVisible   => IsWorkspaceSection("rules");
    public string RulesNavBackground      => GetShellSectionBackground("rules");
    public string RulesNavForeground      => GetShellSectionForeground("rules");
    public bool   IsJournalSectionVisible    => IsWorkspaceSection("journal");
    public string JournalNavBackground       => GetShellSectionBackground("journal");
    public string JournalNavForeground       => GetShellSectionForeground("journal");
    public bool   IsGasSectionVisible        => IsWorkspaceSection("gas");
    public string GasNavBackground           => GetShellSectionBackground("gas");
    public string GasNavForeground           => GetShellSectionForeground("gas");
    public bool   IsPositionsSectionVisible  => IsWorkspaceSection("positions");
    public string PositionsNavBackground     => GetShellSectionBackground("positions");
    public string PositionsNavForeground     => GetShellSectionForeground("positions");
    public bool   IsNewsSectionVisible       => IsWorkspaceSection("news");
    public string NewsNavBackground          => GetShellSectionBackground("news");
    public string NewsNavForeground          => GetShellSectionForeground("news");
    public bool   IsOnChainSectionVisible    => IsWorkspaceSection("onchain");
    public string OnChainNavBackground       => GetShellSectionBackground("onchain");
    public string OnChainNavForeground       => GetShellSectionForeground("onchain");
    public bool   IsCopySectionVisible  => IsWorkspaceSection("copy");
    public string CopyNavBackground     => GetShellSectionBackground("copy");
    public string CopyNavForeground     => GetShellSectionForeground("copy");
    public bool   IsStatArbSectionVisible => IsWorkspaceSection("statarb");
    public string StatArbNavBackground    => GetShellSectionBackground("statarb");
    public string StatArbNavForeground    => GetShellSectionForeground("statarb");
    public bool IsSettingsSectionVisible  => IsWorkspaceSection("settings");
    public bool IsHelpSectionVisible => IsWorkspaceSection("help");
    public bool IsLogoutSectionVisible => IsWorkspaceSection("logout");
    public bool IsPlaceholderSectionVisible =>
        IsRiskSectionVisible ||
        IsBacktestSectionVisible ||
        IsBotsSectionVisible ||
        IsAlertsSectionVisible ||
        IsAnalyticsSectionVisible ||
        IsWhaleTrackerSectionVisible ||
        IsTapeSectionVisible ||
        IsFundingRateSectionVisible ||
        IsArbSectionVisible ||
        IsRouterSectionVisible ||
        IsScannerSectionVisible ||
        IsLiquidationSectionVisible ||
        IsRulesSectionVisible ||
        IsJournalSectionVisible ||
        IsGasSectionVisible ||
        IsPositionsSectionVisible ||
        IsNewsSectionVisible ||
        IsOnChainSectionVisible ||
        IsCopySectionVisible ||
        IsStatArbSectionVisible ||
        IsSettingsSectionVisible ||
        IsHelpSectionVisible ||
        IsLogoutSectionVisible;
    public string CurrentSectionTitle => GetCurrentWorkspaceTitle();
    public string CurrentSectionDescription => GetCurrentWorkspaceDescription();
    public string CurrentSectionRoadmap => GetCurrentWorkspaceRoadmap();
    public string RiskRuntimeStatusLabel => WalletVM.GlobalPaperOnlyMode ? "Protected by paper mode" : "Live risk guard active";
    public string RiskRuntimeStatusBrush => WalletVM.GlobalPaperOnlyMode ? "#F4B860" : "#3DDC84";
    public string RiskRuntimeSummary => $"{WalletVM.GlobalRiskCapLabel} | {WalletVM.GlobalRiskSummary}";
    public string RiskSniperGuardSummary => $"Open slots {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions} | Session buys left {SniperVM.RemainingSessionBuys} | Consecutive live losses {SniperVM.ConsecutiveLiveLossCount}";
    public string RiskCexExposureSummary => $"Ticket {TradeNotional:N2} USDT | Exposure {PortfolioExposureLabel} | Equity {AccountEquityLabel}";

    // ── RiskManager (CEX order guard) limits + live budget, surfaced on the Risk page ──
    public decimal RiskLimitPositionInput
    {
        get => _riskLimitPositionInput;
        set => this.RaiseAndSetIfChanged(ref _riskLimitPositionInput, value);
    }

    public decimal RiskLimitDailyLossInput
    {
        get => _riskLimitDailyLossInput;
        set => this.RaiseAndSetIfChanged(ref _riskLimitDailyLossInput, value);
    }

    public string RiskBudgetPositionCapLabel => $"Max position / exposure {_riskManager.MaxPositionSizeUsd:N0} USDT";

    public string RiskBudgetDailyLossLabel
    {
        get
        {
            var snapshot = _riskManager.GetBudgetSnapshot();
            return $"Daily loss {snapshot.DailyLossUsd:N2} / {snapshot.MaxDailyLossUsd:N2} USDT";
        }
    }

    public string RiskBudgetRemainingLabel =>
        $"{_riskManager.GetBudgetSnapshot().RemainingDailyLossBudgetUsd:N2} USDT loss budget left today";

    public string RiskBudgetBlockReason
    {
        get
        {
            var reason = _riskManager.LastBlockReason;
            return string.IsNullOrEmpty(reason) ? "No CEX orders blocked this session." : reason;
        }
    }

    public string RiskBudgetBlockBrush =>
        string.IsNullOrEmpty(_riskManager.LastBlockReason) ? "#8FA3B8" : "#F4B860";

    public string RiskBudgetWarningLabel => _riskManager.GetBudgetSnapshot().Level switch
    {
        RiskManager.RiskBudgetLevel.Critical => "Critical: near or over the daily loss limit — new orders will be blocked.",
        RiskManager.RiskBudgetLevel.Caution => "Caution: over half the daily loss budget is used.",
        _ => "Within daily risk budget."
    };

    public string RiskBudgetWarningBrush => _riskManager.GetBudgetSnapshot().Level switch
    {
        RiskManager.RiskBudgetLevel.Critical => "#FF5D73",
        RiskManager.RiskBudgetLevel.Caution => "#F4B860",
        _ => "#3DDC84"
    };

    public ReactiveCommand<Unit, Unit> ApplyRiskLimitsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetDailyRiskCommand { get; }

    // ── Block audit log + daily-loss sparkline (Risk page) ──
    private const double RiskSparklineWidth = 240d;
    private const double RiskSparklineHeight = 44d;

    public IReadOnlyList<string> RiskBlockHistoryLines =>
        _riskManager.GetRecentBlocks()
            .Select(static b => $"{b.TimeUtc.ToLocalTime():HH:mm:ss}  {b.Reason}")
            .ToList();

    public bool HasRiskBlockHistory => _riskManager.GetRecentBlocks().Count > 0;

    public string RiskBlockHistoryEmptyLabel => "No CEX orders blocked this session.";

    /// <summary>Polyline points for the cumulative daily-loss trajectory, fit to the sparkline box.</summary>
    public List<Avalonia.Point> RiskDailyLossSparkline
    {
        get
        {
            var history = _riskManager.GetDailyLossHistory();
            var points = new List<Avalonia.Point>();
            if (history.Count < 2)
            {
                return points;
            }

            var max = (double)_riskManager.MaxDailyLossUsd;
            // Scale to the cap when set, otherwise to the largest sample so the line still reads.
            var peak = max > 0d ? max : (double)history.Max();
            if (peak <= 0d)
            {
                return points;
            }

            var stepX = RiskSparklineWidth / (history.Count - 1);
            for (var i = 0; i < history.Count; i++)
            {
                var ratio = Math.Min(1d, (double)history[i] / peak);
                var x = i * stepX;
                var y = RiskSparklineHeight - (ratio * RiskSparklineHeight); // invert: more loss = higher
                points.Add(new Avalonia.Point(x, y));
            }

            return points;
        }
    }

    public bool HasRiskDailyLossSparkline => _riskManager.GetDailyLossHistory().Count >= 2;

    public string BacktestStatusLabel => BacktestVM.StatusLabel;
    public string BacktestStatusBrush => BacktestVM.StatusBrush;
    public string BacktestWindowLabel => BacktestVM.WindowLabel;
    public string BacktestTradeCountLabel => BacktestVM.TradeCountLabel;
    public string BacktestWinRateLabel => BacktestVM.WinRateLabel;
    public string BacktestNetReturnLabel => BacktestVM.NetReturnLabel;
    public string BacktestDrawdownLabel => BacktestVM.DrawdownLabel;
    public string BacktestBestTradeLabel => BacktestVM.BestTradeLabel;
    public string BacktestWorstTradeLabel => BacktestVM.WorstTradeLabel;
    public string BacktestLastSignalLabel => BacktestVM.LastSignalLabel;
    public string BacktestBiasLabel => BacktestVM.BiasLabel;
    public string BacktestNarrative => BacktestVM.Narrative;
    public string BotsRuntimeStatusLabel => AIBotVM.IsRunning ? "Rule bot is running" : "Rule bot is idle";
    public string BotsRuntimeStatusBrush => AIBotVM.IsRunning ? "#3DDC84" : "#8FA3B8";
    public string AnalyticsExecutionSummary => $"Working orders {WorkingOrdersCountLabel} | Fills {RecentFillsCountLabel} | Positions {PositionsCountLabel} | Signals {SignalsCountLabel}";
    public string AnalyticsMarketSummary => $"{MarketBreadthLabel} | Strongest {StrongestMarketLabel} | Weakest {WeakestMarketLabel}";
    public string AnalyticsSniperSummary => $"Closed trades {SniperVM.CombinedTradeCount} | Win rate {SniperVM.CombinedWinRateLabel} | Net {SniperVM.NetClosedPnlLabel}";
    public string SettingsConnectivitySummary => $"{WalletVM.ConnectionSummary} | {WalletVM.GlobalExecutionSummary}";
    public string HelpQuickStartSummary => "Use Markets to pick a symbol, Trading for manual execution, Sniper for DEX monitoring, and Logs for the full runtime trail.";
    public string HelpSafetySummary => "Keep live mode disabled until wallet, quote asset, sizing, and futures credentials are fully verified.";
    public string LogoutStatusLabel => $"{WalletVM.GlobalExecutionModeLabel} | Bot {(AIBotVM.IsRunning ? "running" : "stopped")} | Sniper {(SniperVM.IsArmed ? "armed" : "idle")}";
    public bool IsLimitOrderType => string.Equals(SelectedOrderType, "Limit", StringComparison.OrdinalIgnoreCase);
    public string BuySideBackground => GetOrderSideBackground("BUY");
    public string SellSideBackground => GetOrderSideBackground("SELL");
    public string BuySideForeground => GetOrderSideForeground("BUY");
    public string SellSideForeground => GetOrderSideForeground("SELL");
    public string PrimaryOrderButtonText => SelectedOrderSide == "SELL"
        ? IsLimitOrderType ? "PLACE SELL ORDER" : "SELL MARKET"
        : IsLimitOrderType ? "PLACE BUY ORDER" : "BUY MARKET";
    public string PrimaryOrderButtonHint => TradeNotional <= 0
        ? "Set price and quantity to prepare the ticket."
        : $"{TradeQuantity:0.0000} {BaseAssetSymbol} for {TradeNotional:N2} USDT";
    public decimal EstimatedTradingFee => TradeNotional <= 0 ? 0m : TradeNotional * 0.001m;
    public string EstimatedTradingFeeLabel => $"{EstimatedTradingFee:N2} USDT";
    public decimal EstimatedNetworkFeeUsdt => TradeNotional <= 0 ? 0m : Math.Max(0.20m, TradeNotional * 0.00015m);
    public string EstimatedNetworkFeeLabel => $"{EstimatedNetworkFeeUsdt:N2} USDT";
    public string EstimatedTotalCostLabel => $"{TradeNotional + EstimatedTradingFee + EstimatedNetworkFeeUsdt:N2} USDT";
    public decimal AccountEquityUsdt => IsManualFuturesMode
        ? AvailableBalanceUsdt + UnrealizedPnl
        : AvailableBalanceUsdt + (PositionQuantity * CurrentTradePrice);
    public string AccountEquityLabel => $"{AccountEquityUsdt:N2} USDT";
    public string SessionPnlLabel => $"{(UnrealizedPnl + RealizedPnl):+0.00;-0.00;0.00} USDT";
    public string SessionPnlBrush => (UnrealizedPnl + RealizedPnl) >= 0 ? "#3DDC84" : "#FF6B6B";
    public decimal SessionPnlPercent => AccountEquityUsdt <= 0 ? 0m : ((UnrealizedPnl + RealizedPnl) / AccountEquityUsdt) * 100m;
    public string SessionPnlPercentLabel => $"{SessionPnlPercent:+0.##;-0.##;0.##}%";
    public decimal StrategyEntryPrice => LimitPrice > 0 ? LimitPrice : CurrentTradePrice;
    public decimal StrategyStopPrice => StopLossPrice > 0
        ? StopLossPrice
        : StrategyEntryPrice > 0 ? StrategyEntryPrice * 0.992m : 0m;
    public decimal StrategyTargetPrice => TakeProfitPrice > 0
        ? TakeProfitPrice
        : StrategyEntryPrice > 0 ? StrategyEntryPrice * 1.014m : 0m;
    public decimal StrategyTargetTwoPrice => TakeProfitPrice > 0 && StrategyEntryPrice > 0
        ? StrategyEntryPrice + ((TakeProfitPrice - StrategyEntryPrice) * 1.65m)
        : StrategyEntryPrice > 0 ? StrategyEntryPrice * 1.022m : 0m;
    public string AiConfidenceLabel => $"{AiConfidencePercent:0}%";
    public decimal AiConfidencePercent
    {
        get
        {
            var directionBoost = SelectedMarket?.IsPositiveTrend == true ? 10m : 0m;
            var totalDepth = BidDepthTotal + AskDepthTotal;
            var depthBias = totalDepth <= 0 ? 0m : ((BidDepthTotal - AskDepthTotal) / totalDepth) * 14m;
            var spreadPenalty = Math.Min(12m, SpreadPercent * 120m);
            var guardPenalty = WalletVM.GlobalPaperOnlyMode ? 5m : 0m;
            return Math.Clamp(62m + directionBoost + depthBias - spreadPenalty - guardPenalty, 35m, 92m);
        }
    }
    public string AiOutlookLabel => SelectedMarket?.IsPositiveTrend == true ? "Bullish" : "Defensive";
    public string AiOutlookBrush => SelectedMarket?.IsPositiveTrend == true ? "#21E6C1" : "#F4B860";
    public string AiUpdatedLabel => (SelectedMarket?.LastUpdated == default ? DateTime.Now : SelectedMarket?.LastUpdated ?? DateTime.Now).ToLocalTime().ToString("HH:mm:ss");
    public string AiEntryRangeLabel => StrategyEntryPrice <= 0
        ? "--"
        : $"{Math.Max(0m, StrategyEntryPrice - Math.Max(SpreadValue, StrategyEntryPrice * 0.0015m)):N2} - {(StrategyEntryPrice + Math.Max(SpreadValue, StrategyEntryPrice * 0.0015m)):N2}";
    public string AiStopLossDisplay => StrategyStopPrice > 0 ? $"{StrategyStopPrice:N2}" : "--";
    public string AiTakeProfitOneDisplay => StrategyTargetPrice > 0 ? $"{StrategyTargetPrice:N2}" : "--";
    public string AiTakeProfitTwoDisplay => StrategyTargetTwoPrice > 0 ? $"{StrategyTargetTwoPrice:N2}" : "--";
    public string AiRiskRewardLabel
    {
        get
        {
            var risk = StrategyEntryPrice - StrategyStopPrice;
            var reward = StrategyTargetPrice - StrategyEntryPrice;
            return risk > 0 && reward > 0 ? $"1 : {reward / risk:0.0}" : "n/a";
        }
    }
    public string AiValidityLabel => SelectedTradeTimeframe switch
    {
        "1M" => "15m",
        "5M" => "45m",
        "15M" => "2h",
        "1H" => "6h",
        "4H" => "1d",
        "1D" => "3d",
        "1W" => "1w",
        _ => "Open"
    };
    public string AiPositionSizeLabel => $"{TradeQuantity:0.0000} {BaseAssetSymbol} ({PortfolioExposureLabel})";
    public string AiDirectionLabel => SelectedOrderSide;
    public string AiDirectionBrush => SelectedOrderSide == "SELL" ? "#FF6B6B" : "#21E6C1";
    public string AiWarningPrimary => WalletVM.GlobalPaperOnlyMode
        ? "Execution guard is in paper-only mode. Signals stay actionable, but live routing remains blocked."
        : "Live routing is enabled. Double-check ticket size, stop and target before sending the order.";
    public string AiWarningSecondary => SpreadPercent > 0.04m
        ? $"Spread is elevated at {SpreadPercentLabel}. Consider smaller size or a deeper limit entry."
        : $"Open exposure is {CurrentOpenExposureLabel} with daily loss used at {CurrentDailyLossLabel}.";
    public string AiWarningTertiary => TradeNotional <= 0
        ? "No order is armed yet. Size the ticket first to evaluate the setup properly."
        : $"Current ticket consumes {PortfolioExposureLabel} of available balance with ~{EstimatedTradingFeeLabel} in fees.";
    public string ChartInteractionHint => SelectedChartTool switch
    {
        "Trend" => "Trend line: click first point, then second point on the chart.",
        "Horizontal" => "Horizontal line: click a price level on the chart.",
        "Rectangle" => SelectedChartToolPhase == ChartToolPhase.SecondPoint ? "Rectangle: choose opposite corner." : "Rectangle: click first corner, then opposite corner.",
        "Channel" => SelectedChartToolPhase == ChartToolPhase.ThirdPoint ? "Channel: choose channel width with third click." : SelectedChartToolPhase == ChartToolPhase.SecondPoint ? "Channel: choose second point for the base line." : "Channel: click first point, second point, then width.",
        "Erase" => "Erase mode: click a drawing to remove only that one.",
        _ => "Cursor mode: mouse wheel zooms, drag moves history, hover shows time and price."
    };
    public string TradingChartHeader
    {
        get
        {
            if (IsDexTradingMode)
            {
                var lastDexCandle = DexTradingVM.ChartCandles.LastOrDefault();
                if (lastDexCandle is null)
                {
                    return $"{DexTradingVM.SelectedTokenTitle} | {DexTradingVM.SelectedChartRange}";
                }

                return $"{DexTradingVM.SelectedTokenTitle} {DexTradingVM.SelectedChartRange}   O: {lastDexCandle.Open:N6}   H: {lastDexCandle.High:N6}   L: {lastDexCandle.Low:N6}   C: {lastDexCandle.Close:N6}";
            }

            if (SelectedMarket is null)
            {
                return "Waiting for market data";
            }

            var lastCandle = TradingCandles.LastOrDefault();
            if (lastCandle is null)
            {
                return $"{SelectedMarket.DisplaySymbol} | {SelectedTradeTimeframe}";
            }

            return $"{SelectedMarket.DisplaySymbol} {SelectedTradeTimeframe}   O: {lastCandle.Open:N2}   H: {lastCandle.High:N2}   L: {lastCandle.Low:N2}   C: {lastCandle.Close:N2}   V: {lastCandle.Volume:N2}";
        }
    }
    public string ChartRangeLabel => IsDexTradingMode
        ? DexTradingVM.SelectedChartRange switch
        {
            "15M" => "Range: last 15 minutes",
            "1D" => "Range: last day",
            "1W" => "Range: last week",
            _ => $"Range: {DexTradingVM.SelectedChartRange}"
        }
        : SelectedTradeTimeframe switch
        {
            "1M" => "Range: last minute",
            "5M" => "Range: last 5 minutes",
            "15M" => "Range: last 15 minutes",
            "1H" => "Range: last hour",
            "4H" => "Range: last 4 hours",
            "1D" => "Range: last day",
            "1W" => "Range: last week",
            "1MN" => "Range: last month",
            "ALL" => "Range: full Binance history",
            _ => $"Range: {SelectedTradeTimeframe}"
        };
    public string Timeframe1MBackground => GetTimeframeBackground("1M");
    public string Timeframe5MBackground => GetTimeframeBackground("5M");
    public string Timeframe15MBackground => GetTimeframeBackground("15M");
    public string Timeframe1HBackground => GetTimeframeBackground("1H");
    public string Timeframe4HBackground => GetTimeframeBackground("4H");
    public string Timeframe1DBackground => GetTimeframeBackground("1D");
    public string Timeframe1WBackground => GetTimeframeBackground("1W");
    public string Timeframe1MNBackground => GetTimeframeBackground("1MN");
    public string TimeframeAllBackground => GetTimeframeBackground("ALL");
    public string Timeframe1MForeground => GetTimeframeForeground("1M");
    public string Timeframe5MForeground => GetTimeframeForeground("5M");
    public string Timeframe15MForeground => GetTimeframeForeground("15M");
    public string Timeframe1HForeground => GetTimeframeForeground("1H");
    public string Timeframe4HForeground => GetTimeframeForeground("4H");
    public string Timeframe1DForeground => GetTimeframeForeground("1D");
    public string Timeframe1WForeground => GetTimeframeForeground("1W");
    public string Timeframe1MNForeground => GetTimeframeForeground("1MN");
    public string TimeframeAllForeground => GetTimeframeForeground("ALL");
    public string ChartCursorBackground => GetChartToolBackground("Cursor");
    public string ChartTrendBackground => GetChartToolBackground("Trend");
    public string ChartHorizontalBackground => GetChartToolBackground("Horizontal");
    public string ChartCursorForeground => GetChartToolForeground("Cursor");
    public string ChartTrendForeground => GetChartToolForeground("Trend");
    public string ChartHorizontalForeground => GetChartToolForeground("Horizontal");
    public string ChartRectangleBackground => GetChartToolBackground("Rectangle");
    public string ChartChannelBackground => GetChartToolBackground("Channel");
    public string ChartEraseBackground => GetChartToolBackground("Erase");
    public string ChartRectangleForeground => GetChartToolForeground("Rectangle");
    public string ChartChannelForeground => GetChartToolForeground("Channel");
    public string ChartEraseForeground => GetChartToolForeground("Erase");

    public string TradeIdeaTitle
    {
        get => _tradeIdeaTitle;
        set => this.RaiseAndSetIfChanged(ref _tradeIdeaTitle, value);
    }

    public string TradeIdeaSummary
    {
        get => _tradeIdeaSummary;
        set => this.RaiseAndSetIfChanged(ref _tradeIdeaSummary, value);
    }

    public string SuggestedEntryLabel
    {
        get => _suggestedEntryLabel;
        set => this.RaiseAndSetIfChanged(ref _suggestedEntryLabel, value);
    }

    public string SuggestedStopLabel
    {
        get => _suggestedStopLabel;
        set => this.RaiseAndSetIfChanged(ref _suggestedStopLabel, value);
    }

    public string SuggestedTargetLabel
    {
        get => _suggestedTargetLabel;
        set => this.RaiseAndSetIfChanged(ref _suggestedTargetLabel, value);
    }

    public string AiAssistantDraftPrompt
    {
        get => _aiAssistantDraftPrompt;
        set
        {
            this.RaiseAndSetIfChanged(ref _aiAssistantDraftPrompt, value);
            this.RaisePropertyChanged(nameof(CanSendAiAssistantPrompt));
        }
    }

    public string AiAssistantStatusLabel
    {
        get => _aiAssistantStatusLabel;
        set => this.RaiseAndSetIfChanged(ref _aiAssistantStatusLabel, value);
    }

    public string AiAssistantStatusBrush
    {
        get => _aiAssistantStatusBrush;
        set => this.RaiseAndSetIfChanged(ref _aiAssistantStatusBrush, value);
    }

    public AiVisualCardViewModel? SelectedAiVisual
    {
        get => _selectedAiVisual;
        private set
        {
            if (ReferenceEquals(_selectedAiVisual, value))
            {
                return;
            }

            _selectedAiVisual = value;
            foreach (var visual in AiAssistantVisuals)
            {
                visual.IsSelected = ReferenceEquals(visual, value);
            }

            this.RaisePropertyChanged(nameof(SelectedAiVisual));
            this.RaisePropertyChanged(nameof(AiSelectedVisualTitle));
            this.RaisePropertyChanged(nameof(AiSelectedVisualCaption));
            this.RaisePropertyChanged(nameof(AiSelectedVisualMetric));
            this.RaisePropertyChanged(nameof(AiSelectedVisualImage));
        }
    }

    public string AiWorkspaceHeadline => _localization.IsRussian
        ? $"{EffectiveAiContextSymbol} Сигнальный ассистент"
        : $"{EffectiveAiContextSymbol} Signal Copilot";
    public bool IsAiContextAutoMode => SelectedAiContextVenueMode == AiContextVenueMode.Auto;
    public bool IsAiContextCexMode => SelectedAiContextVenueMode == AiContextVenueMode.Cex;
    public bool IsAiContextDexMode => SelectedAiContextVenueMode == AiContextVenueMode.Dex;
    public bool IsAiEffectiveDexContext => EffectiveAiContextVenue == TradingVenueMode.Dex;
    public string AiContextAutoBackground => IsAiContextAutoMode ? "#17373B" : "#0F1721";
    public string AiContextCexBackground => IsAiContextCexMode ? "#17373B" : "#0F1721";
    public string AiContextDexBackground => IsAiContextDexMode ? "#17373B" : "#0F1721";
    public string AiContextAutoForeground => IsAiContextAutoMode ? "#F4F7FB" : "#8FA3B8";
    public string AiContextCexForeground => IsAiContextCexMode ? "#F4F7FB" : "#8FA3B8";
    public string AiContextDexForeground => IsAiContextDexMode ? "#F4F7FB" : "#8FA3B8";
    public string AiContextVenueLabel => _localization.IsRussian
        ? SelectedAiContextVenueMode switch
        {
            AiContextVenueMode.Auto => $"AUTO -> {EffectiveAiContextVenueLabel}",
            AiContextVenueMode.Cex => "CEX КОНТЕКСТ",
            AiContextVenueMode.Dex => "DEX КОНТЕКСТ",
            _ => "AUTO"
        }
        : SelectedAiContextVenueMode switch
        {
            AiContextVenueMode.Auto => $"AUTO -> {EffectiveAiContextVenueLabel}",
            AiContextVenueMode.Cex => "CEX CONTEXT",
            AiContextVenueMode.Dex => "DEX CONTEXT",
            _ => "AUTO"
        };
    public string AiContextVenueSummary => _localization.IsRussian
        ? (IsAiContextAutoMode
            ? "AI-окно следует за активной площадкой терминала."
            : "AI-окно использует собственный независимый источник контекста.")
        : (IsAiContextAutoMode
            ? "The AI desk follows the terminal's active venue."
            : "The AI desk is using its own independent context source.");
    public string AiContextSourcesSummary => _localization.IsRussian
        ? $"Источники: {(AiIncludeMarketContext ? "market " : string.Empty)}{(AiIncludeRiskContext ? "risk " : string.Empty)}{(AiIncludeDexContext ? "dex " : string.Empty)}{(AiIncludeSniperContext ? "sniper " : string.Empty)}{(AiIncludeVisualContext ? "visual" : string.Empty)}".Trim()
        : $"Sources: {(AiIncludeMarketContext ? "market " : string.Empty)}{(AiIncludeRiskContext ? "risk " : string.Empty)}{(AiIncludeDexContext ? "dex " : string.Empty)}{(AiIncludeSniperContext ? "sniper " : string.Empty)}{(AiIncludeVisualContext ? "visual" : string.Empty)}".Trim();
    public string AiMarketChipBackground => AiIncludeMarketContext ? "#17373B" : "#0F1721";
    public string AiRiskChipBackground => AiIncludeRiskContext ? "#17373B" : "#0F1721";
    public string AiDexChipBackground => AiIncludeDexContext ? "#17373B" : "#0F1721";
    public string AiSniperChipBackground => AiIncludeSniperContext ? "#17373B" : "#0F1721";
    public string AiVisualChipBackground => AiIncludeVisualContext ? "#17373B" : "#0F1721";
    public string AiMarketChipForeground => AiIncludeMarketContext ? "#F4F7FB" : "#8FA3B8";
    public string AiRiskChipForeground => AiIncludeRiskContext ? "#F4F7FB" : "#8FA3B8";
    public string AiDexChipForeground => AiIncludeDexContext ? "#F4F7FB" : "#8FA3B8";
    public string AiSniperChipForeground => AiIncludeSniperContext ? "#F4F7FB" : "#8FA3B8";
    public string AiVisualChipForeground => AiIncludeVisualContext ? "#F4F7FB" : "#8FA3B8";
    public string EffectiveAiContextVenueLabel => EffectiveAiContextVenue == TradingVenueMode.Dex ? "DEX" : "CEX";
    public string EffectiveAiContextTitle => EffectiveAiContextVenue == TradingVenueMode.Dex ? DexTradingVM.SelectedTokenTitle : SelectedMarketTitle;
    public string EffectiveAiContextSymbol => EffectiveAiContextVenue == TradingVenueMode.Dex ? DexTradingVM.SelectedToken?.TokenInfo.Symbol ?? DexTradingVM.SelectedTokenTitle : SelectedTradingSymbol;
    public string AiWorkspaceSubheadline => IsAiEffectiveDexContext
        ? (_localization.IsRussian
            ? $"Разберите DEX-исполнение, готовность маршрута, контекст токена и sniper-оверлеи для {EffectiveAiContextTitle}."
            : $"Talk through DEX execution, route readiness, token context, and sniper overlays for {EffectiveAiContextTitle}.")
        : (_localization.IsRussian
            ? $"Используйте этот чат, чтобы разбирать импульс, инвалидацию, риск и исполнение по {EffectiveAiContextSymbol}."
            : $"Use the chat desk to break down momentum, invalidation, risk, and execution for {EffectiveAiContextSymbol}.");
    public string AiConversationCountLabel => _localization.IsRussian ? $"{AiAssistantMessages.Count} сообщ." : $"{AiAssistantMessages.Count} msgs";
    public string AiVisualCountLabel => _localization.IsRussian ? $"{AiAssistantVisuals.Count} визуала" : $"{AiAssistantVisuals.Count} visuals";
    public string AiQuickPromptCountLabel => _localization.IsRussian ? $"{AiAssistantQuickPrompts.Count} промптов" : $"{AiAssistantQuickPrompts.Count} prompts";
    public string AiKnowledgeCountLabel => _localization.IsRussian ? $"{AiAssistantKnowledgeTopics.Count} тем" : $"{AiAssistantKnowledgeTopics.Count} topics";
    public IEnumerable<AiAssistantQuickPromptViewModel> AiEntryPrompts => AiAssistantQuickPrompts.Where(prompt => prompt.CategoryKey == "entry");
    public IEnumerable<AiAssistantQuickPromptViewModel> AiExitPrompts => AiAssistantQuickPrompts.Where(prompt => prompt.CategoryKey == "exit");
    public IEnumerable<AiAssistantQuickPromptViewModel> AiRiskPrompts => AiAssistantQuickPrompts.Where(prompt => prompt.CategoryKey == "risk");
    public IEnumerable<AiAssistantQuickPromptViewModel> AiDexPrompts => AiAssistantQuickPrompts.Where(prompt => prompt.CategoryKey == "dex");
    public IEnumerable<AiAssistantQuickPromptViewModel> AiSniperPrompts => AiAssistantQuickPrompts.Where(prompt => prompt.CategoryKey == "sniper");
    public IEnumerable<AiAssistantQuickPromptViewModel> AiVisualPrompts => AiAssistantQuickPrompts.Where(prompt => prompt.CategoryKey == "visual");
    public string AiPromptPresetAsset
    {
        get => _aiPromptPresetAsset;
        set
        {
            this.RaiseAndSetIfChanged(ref _aiPromptPresetAsset, value);
            this.RaisePropertyChanged(nameof(AiPromptPresetSummary));
            this.RaisePropertyChanged(nameof(AiPromptPresetPreview));
        }
    }
    public string SelectedAiPromptTradeStyle
    {
        get => _selectedAiPromptTradeStyle;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAiPromptTradeStyle, value);
            this.RaisePropertyChanged(nameof(AiPromptPresetSummary));
            this.RaisePropertyChanged(nameof(AiPromptPresetPreview));
        }
    }
    public string SelectedAiPromptHorizon
    {
        get => _selectedAiPromptHorizon;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAiPromptHorizon, value);
            this.RaisePropertyChanged(nameof(AiPromptPresetSummary));
            this.RaisePropertyChanged(nameof(AiPromptPresetPreview));
        }
    }
    public string SelectedAiPromptRiskProfile
    {
        get => _selectedAiPromptRiskProfile;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAiPromptRiskProfile, value);
            this.RaisePropertyChanged(nameof(AiPromptPresetSummary));
            this.RaisePropertyChanged(nameof(AiPromptPresetPreview));
        }
    }
    public string SelectedAiPromptFocus
    {
        get => _selectedAiPromptFocus;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAiPromptFocus, value);
            this.RaisePropertyChanged(nameof(AiPromptPresetSummary));
            this.RaisePropertyChanged(nameof(AiPromptPresetPreview));
        }
    }
    public string AiPromptPresetResolvedAsset => string.IsNullOrWhiteSpace(AiPromptPresetAsset) ? EffectiveAiContextSymbol : AiPromptPresetAsset.Trim().ToUpperInvariant();
    public string AiCustomPresetName
    {
        get => _aiCustomPresetName;
        set
        {
            this.RaiseAndSetIfChanged(ref _aiCustomPresetName, value);
            this.RaisePropertyChanged(nameof(CanSaveAiCustomPromptPreset));
        }
    }
    public string AiPromptPresetSummary => _localization.IsRussian
        ? $"{AiPromptPresetResolvedAsset} · {SelectedAiPromptTradeStyle} · {SelectedAiPromptHorizon} · {SelectedAiPromptRiskProfile} · {SelectedAiPromptFocus}"
        : $"{AiPromptPresetResolvedAsset} · {SelectedAiPromptTradeStyle} · {SelectedAiPromptHorizon} · {SelectedAiPromptRiskProfile} · {SelectedAiPromptFocus}";
    public string AiPromptPresetPreview => BuildAiPresetPromptText();
    public string AiSavedPresetCountLabel => _localization.IsRussian ? $"{AiSavedPromptPresets.Count} пресетов" : $"{AiSavedPromptPresets.Count} presets";
    public string AiCustomPresetCountLabel => _localization.IsRussian ? $"{AiCustomPromptPresets.Count} моих" : $"{AiCustomPromptPresets.Count} mine";
    public bool CanSaveAiCustomPromptPreset => !string.IsNullOrWhiteSpace(AiCustomPresetName);
    public string AiAssistantComposerPlaceholder => TranslateUi("Ask about the setup, risk, route, sniper state, or request a visual breakdown...");
    public bool CanSendAiAssistantPrompt => !string.IsNullOrWhiteSpace(AiAssistantDraftPrompt);
    public string AiAssistantScopeLabel => $"{AiContextVenueLabel} · {WalletVM.GlobalExecutionModeLabel}";
    public string AiAssistantModeSummary => $"{EffectiveAiTradingSummary} {Environment.NewLine}{WalletVM.GlobalExecutionSummary}";
    public string AiEngineAvailabilityLabel => TranslateUi("GPT SLOT RESERVED");
    public string AiEngineAvailabilityBrush => "#F4B860";
    public string AiEngineAvailabilitySummary => TranslateUi("This desk is fully functional in local mode. External multimodal model wiring is intentionally left disconnected for now.");
    public string AiAttachmentStatusLabel => TranslateUi("Local visuals ready");
    public string AiAttachmentStatusSummary => TranslateUi("Image viewing works in this panel now. User-upload image analysis will be enabled once the GPT bridge is connected.");
    public string AiKnowledgeSummary => TranslateUi("Offline crypto knowledge cards for execution, risk, DEX flow, and new-pair safety checks.");
    public string AiSelectedVisualTitle => SelectedAiVisual?.Title ?? TranslateUi("Select a visual");
    public string AiSelectedVisualCaption => SelectedAiVisual?.Caption ?? TranslateUi("Choose a snapshot to inspect its context.");
    public string AiSelectedVisualMetric => SelectedAiVisual?.Metric ?? "--";
    public Bitmap? AiSelectedVisualImage => SelectedAiVisual?.Image;
    public AiContextVenueMode SelectedAiContextVenueMode
    {
        get => _selectedAiContextVenueMode;
        set
        {
            if (_selectedAiContextVenueMode != value)
            {
                this.RaiseAndSetIfChanged(ref _selectedAiContextVenueMode, value);
                RaiseAiContextSelectionStateChanged();
                RefreshAiSignalStudioContext();
            }
        }
    }
    public bool AiIncludeMarketContext
    {
        get => _aiIncludeMarketContext;
        set => SetAiContextToggle(ref _aiIncludeMarketContext, value, nameof(AiIncludeMarketContext), nameof(AiMarketChipBackground), nameof(AiMarketChipForeground));
    }
    public bool AiIncludeRiskContext
    {
        get => _aiIncludeRiskContext;
        set => SetAiContextToggle(ref _aiIncludeRiskContext, value, nameof(AiIncludeRiskContext), nameof(AiRiskChipBackground), nameof(AiRiskChipForeground));
    }
    public bool AiIncludeDexContext
    {
        get => _aiIncludeDexContext;
        set => SetAiContextToggle(ref _aiIncludeDexContext, value, nameof(AiIncludeDexContext), nameof(AiDexChipBackground), nameof(AiDexChipForeground));
    }
    public bool AiIncludeSniperContext
    {
        get => _aiIncludeSniperContext;
        set => SetAiContextToggle(ref _aiIncludeSniperContext, value, nameof(AiIncludeSniperContext), nameof(AiSniperChipBackground), nameof(AiSniperChipForeground));
    }
    public bool AiIncludeVisualContext
    {
        get => _aiIncludeVisualContext;
        set => SetAiContextToggle(ref _aiIncludeVisualContext, value, nameof(AiIncludeVisualContext), nameof(AiVisualChipBackground), nameof(AiVisualChipForeground));
    }

    public AIBotViewModel AIBotVM { get; }
    public AiTraderViewModel AiTraderVM { get; }
    public CopilotViewModel CopilotVM { get; private set; } = null!;
    public TelegramAccountViewModel TelegramAccountVM { get; private set; } = null!;
    public DailyBriefingViewModel BriefingVM { get; private set; } = null!;
    public PreTradeRiskViewModel RiskCheckVM { get; private set; } = null!;
    public CorrelationInsightViewModel CorrelationInsightVM { get; private set; } = null!;
    public OptionsStrategyViewModel OptionsStrategyVM { get; private set; } = null!;
    public GridBotViewModel GridBotVM { get; }
    public DcaBotViewModel DcaBotVM { get; }
    public WhaleTrackerViewModel      WhaleTrackerVM      { get; private set; } = null!;
    public FundingRateViewModel           FundingRateVM          { get; private set; } = null!;
    public FundingArbitrageViewModel      FundingArbitrageVM     { get; private set; } = null!;
    public CrossExchangeArbitrageViewModel CrossExchangeArbVM    { get; private set; } = null!;
    public CopyTradingViewModel            CopyTradingVM          { get; private set; } = null!;
    public StatArbViewModel                StatArbVM              { get; private set; } = null!;
    public BestExecutionViewModel          BestExecutionVM        { get; private set; } = null!;
    public MarketScannerViewModel          ScannerVM              { get; private set; } = null!;
    public LiquidationHeatmapViewModel LiquidationHeatmapVM { get; private set; } = null!;
    public SentimentViewModel          SentimentVM          { get; private set; } = null!;
    public DexTrendingViewModel        DexTrendingVM        { get; private set; } = null!;
    public PortfolioRebalanceViewModel PortfolioRebalanceVM { get; private set; } = null!;
    public PnlDashboardViewModel      PnlDashboardVM      { get; private set; } = null!;
    public DashboardViewModel              DashboardVM          { get; private set; } = null!;
    public ConfigurationProfileService    ProfileService       { get; } = new();
    public DexTradingViewModel DexTradingVM { get; }
    public WalletWorkspaceViewModel WalletVM { get; }
    public SniperViewModel SniperVM { get; }
    public BacktestViewModel BacktestVM { get; }
    public AlertsViewModel AlertsVM { get; }
    public CompositeRuleViewModel        CompositeRuleVM    { get; }
    public OrderTemplatesViewModel       OrderTemplatesVM   { get; }
    public AdvancedTrailingStopViewModel AdvancedTrailingVM { get; }
    public TradeJournalViewModel         TradeJournalVM     { get; private set; } = null!;
    public TelegramSignalViewModel       TelegramSignalVM   { get; private set; } = null!;
    public MarketTapeViewModel           MarketTapeVM       { get; private set; } = null!;
    public HotkeySettings                HotkeySettings     { get; private set; } = null!;
    public GasMonitorViewModel           GasMonitorVM       { get; private set; } = null!;
    public AllPositionsViewModel         AllPositionsVM     { get; private set; } = null!;
    public NewsFeedViewModel             NewsFeedVM         { get; private set; } = null!;
    public OnChainMetricsViewModel       OnChainVM          { get; private set; } = null!;
    public OnboardingViewModel           OnboardingVM       { get; } = new();
    public LicenseViewModel              LicenseVM          { get; } = new();

    // ── private services ──────────────────────────────────────────────────────
    private readonly TelegramNotificationService _telegram = null!;
    private readonly DiscordWebhookNotificationService _discord = null!;
    private readonly NtfyNotificationService _ntfy = null!;
    private readonly EmailNotificationService _email = null!;
    private GasMonitorService?    _gasMonitorService;
    private NewsFeedService?      _newsFeedService;
    private OnChainMetricsService? _onChainService;
    private WebApiSnapshotWriter? _webApiSnapshotWriter;
    private WebApiQueueProcessor? _webApiQueueProcessor;
    private BalanceRefresher?     _balanceRefresher;

    private string _toastMessage = string.Empty;
    private bool _isToastVisible;
    private bool _isWelcomeVisible;

    private static string WelcomeMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal",
        ".welcome-shown");

    private static bool IsFirstRun()
    {
        try { return !File.Exists(WelcomeMarkerPath); }
        catch { return false; }
    }

    private static void MarkWelcomeShown()
    {
        try
        {
            var path = WelcomeMarkerPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
        }
        catch
        {
            // Non-fatal: worst case the welcome shows again next launch.
        }
    }

    /// <summary>First-run welcome / demo overlay visibility.</summary>
    public bool IsWelcomeVisible
    {
        get => _isWelcomeVisible;
        private set => this.RaiseAndSetIfChanged(ref _isWelcomeVisible, value);
    }

    // ── Update banner ────────────────────────────────────────────────────────
    private bool _isUpdateAvailable;
    private string _updateBannerText = "";
    private string _updateUrl = "";

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => this.RaiseAndSetIfChanged(ref _isUpdateAvailable, value);
    }

    public string UpdateBannerText
    {
        get => _updateBannerText;
        private set => this.RaiseAndSetIfChanged(ref _updateBannerText, value);
    }

    private void StartUpdateCheck()
    {
        RunLoggedAsync(async () =>
        {
            var result = await new UpdateCheckService().CheckAsync().ConfigureAwait(true);
            if (!result.IsUpdateAvailable) return;

            _updateUrl       = result.ReleaseUrl;
            UpdateBannerText = $"Update available: v{result.LatestVersion} (you have v{result.CurrentVersion})";
            IsUpdateAvailable = true;
        }, "Update check");
    }

    private void OpenUpdateUrl()
    {
        IsUpdateAvailable = false;
        if (string.IsNullOrWhiteSpace(_updateUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    private void DismissUpdateBanner() => IsUpdateAvailable = false;

    private void ApplyLicenseState(Services.LicenseSnapshot snapshot)
    {
        // Trial expired and unlicensed → block live trading and pin to paper.
        WalletVM.LicenseAllowsLive = !snapshot.IsExpired;
        if (snapshot.IsExpired)
            WalletVM.GlobalPaperOnlyMode = true;
    }

    private void StartDemoExploring()
    {
        // Demo path: keep everything in paper-only mode (the safe default) and dismiss.
        WalletVM.GlobalPaperOnlyMode = true;
        DismissWelcome();
    }

    private void OpenApiKeysFromWelcome()
    {
        DismissWelcome();
        OnboardingVM.Open();
    }

    private void DismissWelcome()
    {
        IsWelcomeVisible = false;
        MarkWelcomeShown();
    }
    private IDisposable? _toastTimer;

    public string ToastMessage
    {
        get => _toastMessage;
        private set => this.RaiseAndSetIfChanged(ref _toastMessage, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => this.RaiseAndSetIfChanged(ref _isToastVisible, value);
    }

    private bool _isNotificationCenterVisible;

    /// <summary>Full registry of fired notifications, newest first.</summary>
    public ObservableCollection<NotificationEntry> Notifications { get; } = [];

    public bool IsNotificationCenterVisible
    {
        get => _isNotificationCenterVisible;
        private set => this.RaiseAndSetIfChanged(ref _isNotificationCenterVisible, value);
    }

    public bool HasNotifications => Notifications.Count > 0;
    public string NotificationCountLabel => Notifications.Count.ToString();

    private void ShowToast(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Notifications.Insert(0, new NotificationEntry(message, DateTime.Now, ResolveNotificationSymbol(message)));
            while (Notifications.Count > 200)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }
            this.RaisePropertyChanged(nameof(HasNotifications));
            this.RaisePropertyChanged(nameof(NotificationCountLabel));

            ToastMessage = message;
            IsToastVisible = true;
            _toastTimer?.Dispose();
            _toastTimer = System.Reactive.Linq.Observable
                .Timer(TimeSpan.FromSeconds(12))
                .Subscribe(_ => Dispatcher.UIThread.Post(() => IsToastVisible = false));
        });
    }

    /// <summary>Hide the current toast (✕ button) without clearing history.</summary>
    public void DismissToast()
    {
        _toastTimer?.Dispose();
        IsToastVisible = false;
    }

    /// <summary>Open the full notification registry (clicking a toast or the bell).</summary>
    public void OpenNotificationCenter()
    {
        IsToastVisible = false;
        IsNotificationCenterVisible = true;
    }

    public void CloseNotificationCenter() => IsNotificationCenterVisible = false;

    public void ClearNotifications()
    {
        Notifications.Clear();
        this.RaisePropertyChanged(nameof(HasNotifications));
        this.RaisePropertyChanged(nameof(NotificationCountLabel));
    }

    /// <summary>
    /// Best-effort: find a trading symbol mentioned in a notification message so the
    /// registry entry can deep-link to that market. Prefers a full symbol (BTCUSDT)
    /// then a standalone base ticker (BTC).
    /// </summary>
    private string? ResolveNotificationSymbol(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var upper = message.ToUpperInvariant();

        foreach (var market in Markets)
        {
            if (upper.Contains(market.Symbol, StringComparison.Ordinal))
            {
                return market.Symbol;
            }
        }

        foreach (var market in Markets)
        {
            var baseAsset = market.BaseAssetSymbol.ToUpperInvariant();
            if (baseAsset.Length >= 2 &&
                System.Text.RegularExpressions.Regex.IsMatch(
                    upper, $@"\b{System.Text.RegularExpressions.Regex.Escape(baseAsset)}\b"))
            {
                return market.Symbol;
            }
        }

        return null;
    }

    /// <summary>Clicking a registry entry: jump to that market's Trading view.</summary>
    public void ActivateNotification(NotificationEntry entry)
    {
        if (entry?.Symbol is null)
        {
            CloseNotificationCenter();
            return;
        }

        var market = Markets.FirstOrDefault(m =>
            string.Equals(m.Symbol, entry.Symbol, StringComparison.OrdinalIgnoreCase));
        if (market is not null)
        {
            SelectedMarket = market;
        }

        SelectMainTab("trading");
        CloseNotificationCenter();
    }

    private IExchangeGateway ActiveCexGateway => IsManualFuturesMode ? ActiveFuturesGateway : _gateway;
    private string AiCustomPresetsStoragePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal",
        "ai-prompt-presets.json");

    private void StartManualTradingModeRefresh()
    {
        _manualModeRefreshCts?.Cancel();
        _manualModeRefreshCts?.Dispose();
        _manualModeRefreshCts = new CancellationTokenSource();
        RunLoggedAsync(() => RefreshManualTradingModeAsync(_manualModeRefreshCts.Token), "Manual CEX mode refresh");
    }

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
                AddLog($"{label} failed: {ex.Message}");
            }
        }
    }

    private void ObserveCommandErrors()
    {
        SubscribeCommandErrors(BuyMarketCommand, "Buy market");
        SubscribeCommandErrors(SellMarketCommand, "Sell market");
        SubscribeCommandErrors(PlaceBuyLimitCommand, "Buy limit");
        SubscribeCommandErrors(PlaceSellLimitCommand, "Sell limit");
        SubscribeCommandErrors(ArmTakeProfitCommand, "Arm take-profit");
        SubscribeCommandErrors(ArmStopLossCommand, "Arm stop-loss");
        SubscribeCommandErrors(CancelAllOrdersCommand, "Cancel orders");
        SubscribeCommandErrors(ClosePositionCommand, "Close position");
        SubscribeCommandErrors(ReversePositionCommand, "Reverse position");
        SubscribeCommandErrors(PlacePrimaryOrderCommand, "Primary order");
        SubscribeCommandErrors(RefreshMarketsCommand, "Market refresh");
    }

    private void SubscribeCommandErrors<TParam, TResult>(ReactiveCommand<TParam, TResult> command, string label)
    {
        _commandErrorSubscriptions.Add(command.ThrownExceptions.Subscribe(ex => AddLog($"{label} failed: {ex.Message}")));
    }

    private void QueueFuturesUiMarketData(MarketData data)
    {
        var snapshot = new MarketData
        {
            Symbol = data.Symbol,
            BestBid = data.BestBid,
            BestAsk = data.BestAsk,
            LastPrice = data.LastPrice,
            Timestamp = data.Timestamp
        };

        var shouldScheduleDeferredUpdate = false;
        lock (_futuresUiUpdateLock)
        {
            _pendingFuturesUiMarketData = snapshot;

            var elapsed = DateTime.UtcNow - _lastFuturesUiMarketDataUiUpdateUtc;
            if (elapsed >= TimeSpan.FromMilliseconds(150) && !_isFuturesUiUpdateScheduled)
            {
                _isFuturesUiUpdateScheduled = true;
                Dispatcher.UIThread.Post(FlushPendingFuturesUiMarketData, DispatcherPriority.Background);
                return;
            }

            if (!_isFuturesUiUpdateScheduled)
            {
                _isFuturesUiUpdateScheduled = true;
                shouldScheduleDeferredUpdate = true;
            }
        }

        if (shouldScheduleDeferredUpdate)
        {
            RunLoggedAsync(async () =>
            {
                await Task.Delay(150);
                await Dispatcher.UIThread.InvokeAsync(FlushPendingFuturesUiMarketData, DispatcherPriority.Background);
            }, "Futures UI market data flush");
        }
    }

    private void FlushPendingFuturesUiMarketData()
    {
        MarketData? dataToApply;
        lock (_futuresUiUpdateLock)
        {
            dataToApply = _pendingFuturesUiMarketData;
            _pendingFuturesUiMarketData = null;
            _isFuturesUiUpdateScheduled = false;
            _lastFuturesUiMarketDataUiUpdateUtc = DateTime.UtcNow;
        }

        if (dataToApply is null ||
            !IsManualFuturesMode ||
            SelectedMarket is null ||
            !string.Equals(SelectedMarket.Symbol, dataToApply.Symbol, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedMarket.UpdateMarketData(dataToApply);
        CurrentMarketData = dataToApply;
        ScheduleWorkingOrdersEvaluation();
    }

    private void ScheduleWorkingOrdersEvaluation()
    {
        lock (_workingOrderEvaluationLock)
        {
            if (_isWorkingOrderEvaluationScheduled || _isWorkingOrderEvaluationRunning)
            {
                return;
            }

            _isWorkingOrderEvaluationScheduled = true;
        }

        RunLoggedAsync(async () =>
        {
            await Task.Delay(120);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                lock (_workingOrderEvaluationLock)
                {
                    _isWorkingOrderEvaluationScheduled = false;
                    if (_isWorkingOrderEvaluationRunning)
                    {
                        return;
                    }

                    _isWorkingOrderEvaluationRunning = true;
                }

                try
                {
                    await EvaluateWorkingOrdersAsync();
                }
                finally
                {
                    lock (_workingOrderEvaluationLock)
                    {
                        _isWorkingOrderEvaluationRunning = false;
                    }
                }
            }, DispatcherPriority.Background);
        }, "Working order evaluation");
    }

    private void SchedulePassiveManualModeSync()
    {
        if (_isManualModeSyncScheduled)
        {
            return;
        }

        _isManualModeSyncScheduled = true;
        RunLoggedAsync(async () =>
        {
            try
            {
                await Task.Delay(350);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _isManualModeSyncScheduled = false;
                    StartManualTradingModeRefresh();
                }, DispatcherPriority.Background);
            }
            catch
            {
                _isManualModeSyncScheduled = false;
                throw;
            }
        }, "Manual mode sync scheduler");
    }

    private async Task RefreshManualTradingModeAsync(CancellationToken cancellationToken)
    {
        if (SelectedTradingVenue != TradingVenueMode.Cex)
        {
            return;
        }

        await Task.Yield();
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var refreshTasks = new List<Task>();
        if (SelectedMarket is not null)
        {
            _focusLatestCandlesOnNextRefresh = true;
            refreshTasks.Add(RunBoundedRefreshAsync(
                () => RefreshSelectedOrderBookAsync(),
                TimeSpan.FromSeconds(4),
                "Manual mode order book refresh timed out.",
                cancellationToken));
            refreshTasks.Add(RunBoundedRefreshAsync(
                () => RefreshSelectedCandlesAsync(),
                TimeSpan.FromSeconds(4),
                "Manual mode candle refresh timed out.",
                cancellationToken));
        }

        refreshTasks.Add(RunBoundedRefreshAsync(
            () => RefreshManualAccountStateAsync(),
            TimeSpan.FromSeconds(4),
            "Manual mode account sync timed out.",
            cancellationToken));

        await Task.WhenAll(refreshTasks);
        if (!cancellationToken.IsCancellationRequested)
        {
            AddLog($"Manual CEX mode switched to {CexMarketModeSummary}.");
        }
    }

    private async Task RefreshManualAccountStateAsync()
    {
        if (IsManualFuturesMode && !ActiveFuturesGateway.HasPrivateApiCredentials)
        {
            PositionQuantity = 0m;
            AverageEntryPrice = 0m;
            _currentFuturesLiquidationPrice = 0m;
            _currentFuturesMarkPrice = 0m;
            _currentFuturesExchangeUnrealizedPnl = 0m;
            RaisePositionStateChanged();
            return;
        }

        try
        {
            AvailableBalanceUsdt = await ActiveCexGateway.GetBalanceAsync("USDT");
        }
        catch (Exception ex)
        {
            AddLog($"Balance refresh failed for {SelectedCexMarketMode}: {ex.Message}");
        }

        if (!IsManualFuturesMode)
        {
            _currentFuturesLiquidationPrice = 0m;
            _currentFuturesMarkPrice = 0m;
            _currentFuturesExchangeUnrealizedPnl = 0m;
            RaisePositionStateChanged();
            return;
        }

        try
        {
            var positions = await ActiveFuturesGateway.GetOpenPositionsAsync();
            var selectedPosition = positions.FirstOrDefault(static position => position.Quantity != 0);
            var matchingPosition = positions.FirstOrDefault(position =>
                string.Equals(position.Symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase) && position.Quantity != 0);

            var position = matchingPosition ?? selectedPosition;
            if (position is null)
            {
                PositionQuantity = 0m;
                AverageEntryPrice = 0m;
                _currentFuturesLiquidationPrice = 0m;
                _currentFuturesMarkPrice = 0m;
                _currentFuturesExchangeUnrealizedPnl = 0m;
                RemoveExchangeManagedOrdersForSymbolLocally(SelectedTradingSymbol);
            }
            else
            {
                PositionQuantity = position.Quantity;
                AverageEntryPrice = position.EntryPrice;
                _currentFuturesLiquidationPrice = position.LiquidationPrice;
                _currentFuturesMarkPrice = position.MarkPrice;
                _currentFuturesExchangeUnrealizedPnl = position.UnrealizedPnl;
                ManualFuturesLeverage = Math.Max(1, position.Leverage);
                ManualFuturesMarginMode = position.MarginMode == FuturesMarginMode.Isolated ? "Isolated" : "Cross";
            }

            await SyncExchangeManagedOrdersAsync(SelectedTradingSymbol);
            await SyncExchangeRecentTradesAsync(SelectedTradingSymbol);
            RefreshPositionRows();
            RaisePositionStateChanged();
        }
        catch (Exception ex)
        {
            AddLog($"Futures position sync failed: {ex.Message}");
        }
    }

    private async Task RunBoundedRefreshAsync(Func<Task> operation, TimeSpan timeout, string timeoutMessage, CancellationToken cancellationToken, [CallerMemberName] string? caller = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var operationTask = operation();
        var completedTask = await Task.WhenAny(operationTask, Task.Delay(timeout, cancellationToken));
        if (completedTask == operationTask)
        {
            await operationTask;
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            AddLog(timeoutMessage);
        }
    }

    private async Task SyncExchangeRecentTradesAsync(string symbol)
    {
        if (!IsManualFuturesMode || !ActiveFuturesGateway.HasPrivateApiCredentials)
        {
            return;
        }

        IReadOnlyList<TradeExecution> trades;
        try
        {
            trades = await ActiveFuturesGateway.GetRecentTradesAsync(symbol, limit: 25);
        }
        catch
        {
            return;
        }

        var orderedTrades = trades
            .Where(trade => string.Equals(trade.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(trade => trade.TimestampUtc)
            .ToList();

        RealizedPnl = orderedTrades
            .Where(trade => trade.TimestampUtc.ToLocalTime().Date == DateTime.Now.Date)
            .Sum(trade => trade.RealizedPnl - trade.Fee);

        RecentFills.Clear();
        foreach (var trade in orderedTrades.Take(12).OrderBy(trade => trade.TimestampUtc))
        {
            RecentFills.Insert(0, new TradeFillViewModel(
                trade.Symbol,
                trade.Side == CryptoAITerminal.Core.Enums.OrderSide.Buy ? OrderSide.Buy : OrderSide.Sell,
                trade.Price,
                trade.Quantity,
                "FILLED"));
        }

        this.RaisePropertyChanged(nameof(RecentFillsCountLabel));
        this.RaisePropertyChanged(nameof(HasRecentFills));
        this.RaisePropertyChanged(nameof(ShowRecentFillsPlaceholder));
        RaisePositionStateChanged();
    }

    private void RemoveExchangeManagedOrdersForSymbolLocally(string symbol)
    {
        var staleOrders = WorkingOrders
            .Where(order => order.IsExchangeManaged &&
                            string.Equals(order.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                            order.Kind is WorkingOrderKind.LimitBuy or WorkingOrderKind.LimitSell or WorkingOrderKind.TakeProfit or WorkingOrderKind.StopLoss)
            .ToList();

        if (staleOrders.Count == 0)
        {
            return;
        }

        foreach (var order in staleOrders)
        {
            WorkingOrders.Remove(order);
        }

        RaiseWorkingOrdersCollectionChanged();
    }

    private async Task<FuturesPosition?> GetSelectedFuturesPositionAsync()
    {
        var positions = await ActiveFuturesGateway.GetOpenPositionsAsync();
        var matchingPosition = positions.FirstOrDefault(position =>
            string.Equals(position.Symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase) &&
            position.Quantity != 0);

        return matchingPosition ?? positions.FirstOrDefault(static position => position.Quantity != 0);
    }

    private async Task SyncExchangeManagedOrdersAsync(string symbol)
    {
        if (!IsManualFuturesMode)
        {
            return;
        }

        IReadOnlyList<Order> openOrders;
        try
        {
            openOrders = await ActiveFuturesGateway.GetOpenOrdersAsync(symbol);
        }
        catch
        {
            return;
        }

        var syncedOrders = openOrders
            .Where(order => string.Equals(order.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Where(order => order.Type == OrderType.Limit || order.StopPrice is > 0 || IsExchangeProtectionType(order.ExchangeType))
            .Where(order => order.Status is OrderStatus.New or OrderStatus.PartiallyFilled)
            .Select(MapExchangeManagedOrder)
            .Where(static order => order is not null)
            .Cast<WorkingOrderViewModel>()
            .ToList();

        var staleOrders = WorkingOrders
            .Where(order => order.IsExchangeManaged && string.Equals(order.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var stale in staleOrders)
        {
            WorkingOrders.Remove(stale);
        }

        foreach (var synced in syncedOrders)
        {
            synced.AttachCancel(() => CancelSingleOrder(synced));
            WorkingOrders.Add(synced);
        }

        RaiseWorkingOrdersCollectionChanged();
    }

    private static bool IsExchangeProtectionType(string exchangeType) =>
        exchangeType.Contains("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase) ||
        exchangeType.Contains("STOP", StringComparison.OrdinalIgnoreCase) ||
        exchangeType.Contains("TRIGGER", StringComparison.OrdinalIgnoreCase) ||
        exchangeType.Contains("CONDITIONAL", StringComparison.OrdinalIgnoreCase);

    private static WorkingOrderViewModel? MapExchangeManagedOrder(Order order)
    {
        if (order.StopPrice is > 0)
        {
            var kind = (order.ExchangeType.Contains("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase)
                        || order.ExchangeType.Contains("TP", StringComparison.OrdinalIgnoreCase))
                ? WorkingOrderKind.TakeProfit
                : WorkingOrderKind.StopLoss;
            return WorkingOrderViewModel.CreateExchangeProtection(
                kind,
                order.Symbol,
                order.Quantity,
                order.StopPrice.Value,
                order.Id,
                order.FilledQuantity,
                order.Status.ToString(),
                order.CreatedAt.ToLocalTime(),
                order.ReduceOnly,
                order.ExchangeType);
        }

        return WorkingOrderViewModel.CreateExchangeLimit(
            order.Side == OrderSide.Buy ? OrderSide.Buy : OrderSide.Sell,
            order.Symbol,
            order.Quantity,
            order.Price,
            string.IsNullOrWhiteSpace(order.TimeInForce) ? "GTC" : order.TimeInForce,
            order.Id,
            order.FilledQuantity,
            order.Status.ToString(),
            order.CreatedAt.ToLocalTime(),
            order.ReduceOnly,
            order.ExchangeType);
    }

    private async Task ArmExchangeProtectionAsync(WorkingOrderKind kind, decimal triggerPrice)
    {
        try
        {
            var openPosition = await GetSelectedFuturesPositionAsync();
            if (openPosition is null)
            {
                AddLog($"No open {SelectedFuturesExchange} futures position is available for protection.");
                return;
            }

            if (!TryValidateFuturesProtectionTrigger(openPosition, kind, triggerPrice, out var validationReason))
            {
                AddLog(validationReason);
                return;
            }

            var existingProtection = WorkingOrders
                .Where(order => order.IsExchangeManaged &&
                                order.Kind == kind &&
                                string.Equals(order.Symbol, openPosition.Symbol, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var protection in existingProtection)
            {
                await CancelSingleOrderAsync(protection, suppressLog: true);
            }

            var closeSide = openPosition.Quantity > 0
                ? CryptoAITerminal.Core.Enums.OrderSide.Sell
                : CryptoAITerminal.Core.Enums.OrderSide.Buy;

            Order result;
            try
            {
                result = kind == WorkingOrderKind.TakeProfit
                    ? await ActiveFuturesGateway.PlaceTakeProfitOrderAsync(openPosition.Symbol, closeSide, Math.Abs(openPosition.Quantity), triggerPrice, openPosition.PositionSide)
                    : await ActiveFuturesGateway.PlaceStopLossOrderAsync(openPosition.Symbol, closeSide, Math.Abs(openPosition.Quantity), triggerPrice, openPosition.PositionSide);
            }
            catch (NotSupportedException)
            {
                // Exchange does not support native TP/SL — register as software-simulated working order.
                var softId = Guid.NewGuid().ToString("N")[..8];
                var softOrder = WorkingOrderViewModel.CreateExchangeProtection(
                    kind, openPosition.Symbol, Math.Abs(openPosition.Quantity), triggerPrice,
                    softId, 0m, "New", DateTime.Now, true, kind == WorkingOrderKind.TakeProfit ? "TAKE_PROFIT" : "STOP");
                softOrder.AttachCancel(() => CancelSingleOrder(softOrder));
                WorkingOrders.Add(softOrder);
                RaiseWorkingOrdersCollectionChanged();
                AddLog($"{softOrder.KindLabel} (software) registered at {triggerPrice:N2} — {SelectedFuturesExchange} does not support native conditional orders.");
                return;
            }

            var order = WorkingOrderViewModel.CreateExchangeProtection(kind, openPosition.Symbol, result.Quantity, triggerPrice, result.Id, result.FilledQuantity, result.Status.ToString(), result.CreatedAt.ToLocalTime(), result.ReduceOnly, result.ExchangeType);
            order.AttachCancel(() => CancelSingleOrder(order));
            WorkingOrders.Add(order);
            RaiseWorkingOrdersCollectionChanged();
            AddLog($"{order.KindLabel} sent to {SelectedFuturesExchange} futures at {triggerPrice:N2}.");
        }
        catch (Exception ex)
        {
            AddLog($"Failed to arm {SelectedFuturesExchange} futures protection: {ex.Message}");
        }
    }

    private async Task PlaceExchangeLimitAsync(CryptoAITerminal.Core.Enums.OrderSide side, decimal limitPrice)
    {
        try
        {
            var openPosition = await GetSelectedFuturesPositionAsync();
            var requestedQuantity = Math.Max(TradeQuantity, 0m);
            if (requestedQuantity <= 0)
            {
                AddLog($"Set a positive quantity before placing a {SelectedFuturesExchange} futures limit order.");
                return;
            }

            var isClosingLong = openPosition?.Quantity > 0 && side == CryptoAITerminal.Core.Enums.OrderSide.Sell;
            var isClosingShort = openPosition?.Quantity < 0 && side == CryptoAITerminal.Core.Enums.OrderSide.Buy;
            var reduceOnly = isClosingLong || isClosingShort;
            var quantity = reduceOnly && openPosition is not null
                ? Math.Min(requestedQuantity, Math.Abs(openPosition.Quantity))
                : requestedQuantity;

            if (quantity <= 0)
            {
                AddLog($"Nothing to place on {SelectedFuturesExchange} futures: quantity resolved to zero.");
                return;
            }

            if (!TryValidateFuturesLimitIntent(openPosition, side, limitPrice, reduceOnly, out var validationReason))
            {
                AddLog(validationReason);
                return;
            }

            var symbol = openPosition?.Symbol ?? SelectedTradingSymbol;
            var sideKind = side == CryptoAITerminal.Core.Enums.OrderSide.Buy ? WorkingOrderKind.LimitBuy : WorkingOrderKind.LimitSell;
            var existingLimits = WorkingOrders
                .Where(order => order.IsExchangeManaged &&
                                order.Kind == sideKind &&
                                string.Equals(order.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var existing in existingLimits)
            {
                await CancelSingleOrderAsync(existing, suppressLog: true);
            }
            var positionSide = reduceOnly
                ? openPosition!.PositionSide
                : side == CryptoAITerminal.Core.Enums.OrderSide.Buy
                    ? FuturesPositionSide.Long
                    : FuturesPositionSide.Short;

            if (!reduceOnly)
            {
                var requestedSpend = quantity * limitPrice;
                if (!WalletVM.TryApproveUsdRisk(requestedSpend, CurrentOpenExposureUsdt, RealizedPnl, out var riskReason))
                {
                    AddLog(riskReason);
                    return;
                }

                var riskOrder = new Order
                {
                    Symbol = symbol,
                    Side = side,
                    Type = OrderType.Limit,
                    Quantity = quantity,
                    Price = limitPrice,
                    MarketType = TradingMarketType.FuturesUsdM,
                    Leverage = ManualFuturesLeverage,
                    MarginMode = SelectedManualFuturesMarginModeEnum,
                    PositionSide = positionSide
                };

                if (!_riskManager.CanPlaceOrder(riskOrder, limitPrice, AvailableBalanceUsdt, CurrentOpenExposureUsdt))
                {
                    AddLog($"Risk manager rejected the {SelectedFuturesExchange} futures limit order.");
                    return;
                }
            }

            var result = await ActiveFuturesGateway.PlaceOrderAsync(new Order
            {
                Symbol = symbol,
                Side = side,
                Type = OrderType.Limit,
                Quantity = quantity,
                Price = limitPrice,
                MarketType = TradingMarketType.FuturesUsdM,
                Leverage = ManualFuturesLeverage,
                MarginMode = SelectedManualFuturesMarginModeEnum,
                ReduceOnly = reduceOnly,
                PositionSide = positionSide
            });

            var orderSide = side == CryptoAITerminal.Core.Enums.OrderSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var order = WorkingOrderViewModel.CreateExchangeLimit(orderSide, symbol, quantity, limitPrice, SelectedTimeInForce, result.Id, result.FilledQuantity, result.Status.ToString(), result.CreatedAt.ToLocalTime(), result.ReduceOnly, result.ExchangeType);
            order.AttachCancel(() => CancelSingleOrder(order));
            WorkingOrders.Add(order);
            RaiseWorkingOrdersCollectionChanged();
            AddLog($"{(reduceOnly ? "Reduce-only " : string.Empty)}{side.ToString().ToUpperInvariant()} LIMIT sent to {SelectedFuturesExchange} futures at {limitPrice:N2}.");
        }
        catch (Exception ex)
        {
            AddLog($"Failed to place {SelectedFuturesExchange} futures limit order: {ex.Message}");
        }
    }

    private bool TryValidateFuturesProtectionTrigger(FuturesPosition position, WorkingOrderKind kind, decimal triggerPrice, out string reason)
    {
        reason = string.Empty;
        if (triggerPrice <= 0)
        {
            reason = "Protection trigger must be above zero.";
            return false;
        }

        var referencePrice = position.MarkPrice > 0 ? position.MarkPrice : CurrentTradePrice;
        if (position.Quantity > 0)
        {
            if (kind == WorkingOrderKind.TakeProfit && triggerPrice <= referencePrice)
            {
                reason = "Long take-profit should be above the current futures price.";
                return false;
            }

            if (kind == WorkingOrderKind.StopLoss && triggerPrice >= referencePrice)
            {
                reason = "Long stop-loss should be below the current futures price.";
                return false;
            }

            return true;
        }

        if (kind == WorkingOrderKind.TakeProfit && triggerPrice >= referencePrice)
        {
            reason = "Short take-profit should be below the current futures price.";
            return false;
        }

        if (kind == WorkingOrderKind.StopLoss && triggerPrice <= referencePrice)
        {
            reason = "Short stop-loss should be above the current futures price.";
            return false;
        }

        return true;
    }

    private bool TryValidateFuturesLimitIntent(FuturesPosition? openPosition, CryptoAITerminal.Core.Enums.OrderSide side, decimal limitPrice, bool reduceOnly, out string reason)
    {
        reason = string.Empty;
        if (limitPrice <= 0)
        {
            reason = "Limit price must be above zero.";
            return false;
        }

        var referencePrice = EffectivePositionMarkPrice > 0 ? EffectivePositionMarkPrice : CurrentTradePrice;
        if (!reduceOnly || openPosition is null || referencePrice <= 0)
        {
            return true;
        }

        if (openPosition.Quantity > 0 && side == CryptoAITerminal.Core.Enums.OrderSide.Sell && limitPrice <= referencePrice)
        {
            reason = "Reduce-only long sell limit should typically be above the current futures price.";
            return false;
        }

        if (openPosition.Quantity < 0 && side == CryptoAITerminal.Core.Enums.OrderSide.Buy && limitPrice >= referencePrice)
        {
            reason = "Reduce-only short buy limit should typically be below the current futures price.";
            return false;
        }

        return true;
    }

    // Кэш hedge/one-way mode per-symbol для manual торговли (как в TradingBot).
    private readonly Dictionary<string, bool> _manualHedgeModeBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manualFuturesSetupDone = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Применяет leverage и margin mode перед manual фьючерсным ордером.
    /// Делается один раз на символ — потом кэшируется в `_manualFuturesSetupDone`.
    /// Ошибки логируются но не валят ордер (биржи часто отклоняют повторную установку
    /// того же leverage с ошибкой типа "leverage not modified").
    /// </summary>
    private async Task EnsureManualFuturesSetupAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        if (_manualFuturesSetupDone.Contains(symbol)) return;

        try { await ActiveFuturesGateway.SetLeverageAsync(symbol, ManualFuturesLeverage); }
        catch (Exception ex) { AddLog($"SetLeverage warning [{symbol}]: {ex.Message}"); }

        try { await ActiveFuturesGateway.SetMarginModeAsync(symbol, SelectedManualFuturesMarginModeEnum); }
        catch (Exception ex) { AddLog($"SetMarginMode warning [{symbol}]: {ex.Message}"); }

        // Определяем hedge vs one-way по существующим позициям (если есть PositionSide.Both — one-way).
        try
        {
            var positions = await ActiveFuturesGateway.GetOpenPositionsAsync();
            var hasBoth = positions.Any(p => p.PositionSide == FuturesPositionSide.Both);
            _manualHedgeModeBySymbol[symbol] = !hasBoth;
        }
        catch { _manualHedgeModeBySymbol[symbol] = true; /* default hedge */ }

        _manualFuturesSetupDone.Add(symbol);
    }

    private FuturesPositionSide ManualEntryPositionSide(string symbol, CryptoAITerminal.Core.Enums.OrderSide side)
    {
        var isHedge = _manualHedgeModeBySymbol.TryGetValue(symbol, out var h) ? h : true;
        if (!isHedge) return FuturesPositionSide.Both;
        return side == CryptoAITerminal.Core.Enums.OrderSide.Buy
            ? FuturesPositionSide.Long
            : FuturesPositionSide.Short;
    }

    private static bool IsPositionSideMismatch(Exception ex)
    {
        var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return msg.Contains("position side") || msg.Contains("position mode")
            || msg.Contains("position idx") || msg.Contains("51124");
    }

    private async Task<Order> PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide side, decimal quantity, bool reduceOnly = false)
    {
        if (IsManualFuturesMode)
        {
            await EnsureManualFuturesSetupAsync(SelectedTradingSymbol);

            var futuresQuantity = quantity;
            var positionSide = ManualEntryPositionSide(SelectedTradingSymbol, side);

            if (reduceOnly)
            {
                var openPosition = await GetSelectedFuturesPositionAsync();
                if (openPosition is null)
                {
                    throw new InvalidOperationException($"No open {SelectedFuturesExchange} futures position is available to reduce.");
                }

                futuresQuantity = Math.Min(quantity <= 0 ? Math.Abs(openPosition.Quantity) : quantity, Math.Abs(openPosition.Quantity));
                positionSide = openPosition.PositionSide;
            }

            var order = new Order
            {
                Symbol = SelectedTradingSymbol,
                Side = side,
                Type = OrderType.Market,
                Quantity = futuresQuantity,
                MarketType = TradingMarketType.FuturesUsdM,
                Leverage = ManualFuturesLeverage,
                MarginMode = SelectedManualFuturesMarginModeEnum,
                ReduceOnly = reduceOnly,
                PositionSide = positionSide
            };

            try
            {
                return await ActiveFuturesGateway.PlaceOrderAsync(order);
            }
            catch (Exception ex) when (IsPositionSideMismatch(ex))
            {
                // Эвристика подсказала не тот mode — флипаем и пробуем ещё раз.
                _manualHedgeModeBySymbol[SelectedTradingSymbol] = !_manualHedgeModeBySymbol.GetValueOrDefault(SelectedTradingSymbol, true);
                order.PositionSide = reduceOnly ? order.PositionSide : ManualEntryPositionSide(SelectedTradingSymbol, side);
                AddLog($"Position-side mismatch — switched mode and retrying for {SelectedTradingSymbol}.");
                return await ActiveFuturesGateway.PlaceOrderAsync(order);
            }
        }

        var router = new MarketOrderRouter(_gateway);
        return side == CryptoAITerminal.Core.Enums.OrderSide.Buy
            ? await router.BuyMarketAsync(SelectedTradingSymbol, quantity)
            : await router.SellMarketAsync(SelectedTradingSymbol, quantity);
    }

    private async Task CancelSingleOrderAsync(WorkingOrderViewModel order, bool suppressLog = false)
    {
        try
        {
            if (order.IsExchangeManaged)
            {
                await ActiveFuturesGateway.CancelOrderAsync(order.Symbol, order.ExchangeOrderId ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            AddLog($"Cancel failed for {order.KindLabel}: {ex.Message}");
            return;
        }

        if (!WorkingOrders.Remove(order))
        {
            return;
        }

        RaiseWorkingOrdersCollectionChanged();
        if (!suppressLog)
        {
            AddLog($"Canceled {order.KindLabel} at {order.TriggerLabel}.");
        }
    }

    private async Task CancelExchangeManagedProtectionOrdersAsync(bool suppressLog = true)
    {
        var protectionOrders = WorkingOrders
            .Where(order => order.IsExchangeManaged && order.Kind is WorkingOrderKind.TakeProfit or WorkingOrderKind.StopLoss)
            .ToList();

        foreach (var order in protectionOrders)
        {
            await CancelSingleOrderAsync(order, suppressLog);
        }
    }

    private async Task SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide side, decimal executionPrice, decimal quantity)
    {
        if (IsManualFuturesMode)
        {
            await RefreshManualAccountStateAsync();
            return;
        }

        if (side == CryptoAITerminal.Core.Enums.OrderSide.Buy)
        {
            ApplyFilledBuy(executionPrice, quantity);
        }
        else
        {
            ApplyFilledSell(executionPrice, quantity);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            AddLog($"Connecting public Binance market data for {string.Join(", ", DefaultSymbols)}");

            try
            {
                await _gateway.ConnectAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Spot market data stream unavailable: {ex.Message}");
            }

            try
            {
                await _futuresGateway.ConnectAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Binance futures market data unavailable: {ex.Message}");
            }

            // Connect remaining futures gateways (Bybit, OKX, KuCoin) for multi-exchange futures support.
            if (_futuresGatewaysMap is not null)
            {
                foreach (var (name, gw) in _futuresGatewaysMap)
                {
                    if (ReferenceEquals(gw, _futuresGateway)) continue;
                    try { await gw.ConnectAsync(); }
                    catch (Exception ex) { AddLog($"{name} futures connection failed: {ex.Message}"); }
                }
            }

            await RefreshManualAccountStateAsync();
            await RefreshSelectedOrderBookAsync();
            await RefreshSelectedCandlesAsync();
            SeedTicketDefaults();
            AddLog("Primary market snapshot is live.");

            RunLoggedAsync(async () =>
            {
                await RefreshAllOrderBooksAsync();
            }, "Background market warmup");

            _orderBookTimer.Start();
            AddLog("Autonomous market parser is live.");
        }
        catch (Exception ex)
        {
            AddLog($"Market initialization failed: {ex.Message}");
        }
        finally
        {
            IsMarketLoading = false;
        }
    }

    private async Task ExecuteBuyMarket()
    {
        var symbol = SelectedTradingSymbol;
        if (!WalletVM.TryApproveLiveExecution("CEX market buy", out var executionReason))
        {
            AddLog(executionReason);
            return;
        }

        if (IsManualFuturesMode && PositionQuantity < 0)
        {
            var quantityToBuy = Math.Min(TradeQuantity, Math.Abs(PositionQuantity));
            if (quantityToBuy <= 0)
            {
                AddLog("No open manual futures short position to buy back.");
                return;
            }

            AddLog($"Executing reduce-only market buy of {quantityToBuy} {symbol} to close short exposure...");
            var result = await PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide.Buy, quantityToBuy, reduceOnly: true);
            await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Buy, result.Price > 0 ? result.Price : CurrentTradePrice, result.Quantity);
            AddLog($"Buy order placed: {result.Id} - {result.Status}");
            return;
        }

        if (!WalletVM.TryApproveUsdRisk(TradeNotional, CurrentOpenExposureUsdt, RealizedPnl, out var riskReason))
        {
            AddLog(riskReason);
            return;
        }

        AddLog($"Executing market buy of {TradeQuantity} {symbol}...");

        // PositionSide для risk-check (фактическая корректировка hedge/one-way происходит в PlaceCexMarketOrderAsync).
        var entrySide = IsManualFuturesMode
            ? ManualEntryPositionSide(symbol, CryptoAITerminal.Core.Enums.OrderSide.Buy)
            : FuturesPositionSide.Both;
        var order = new Order
        {
            Symbol = symbol,
            Side = OrderSide.Buy,
            Quantity = TradeQuantity,
            Type = OrderType.Market,
            MarketType = IsManualFuturesMode ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot,
            Leverage = IsManualFuturesMode ? ManualFuturesLeverage : null,
            MarginMode = IsManualFuturesMode ? SelectedManualFuturesMarginModeEnum : FuturesMarginMode.Cross,
            PositionSide = entrySide
        };

        if (_riskManager.CanPlaceOrder(order, CurrentTradePrice, AvailableBalanceUsdt, CurrentOpenExposureUsdt))
        {
            var result = await PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide.Buy, TradeQuantity);
            await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Buy, result.Price > 0 ? result.Price : CurrentTradePrice, result.Quantity);
            AddLog($"Buy order placed: {result.Id} - {result.Status}");
        }
        else
        {
            AddLog("Risk manager rejected the buy order.");
        }
    }

    private async Task ExecuteSellMarket()
    {
        var symbol = SelectedTradingSymbol;
        if (!WalletVM.TryApproveLiveExecution("CEX market sell", out var executionReason))
        {
            AddLog(executionReason);
            return;
        }

        if (IsManualFuturesMode && PositionQuantity > 0)
        {
            var reduceQuantity = Math.Min(TradeQuantity, PositionQuantity);
            AddLog($"Executing reduce-only market sell of {reduceQuantity} {symbol} to close long exposure...");
            var reduceResult = await PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, reduceQuantity, reduceOnly: true);
            await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, reduceResult.Price > 0 ? reduceResult.Price : CurrentTradePrice, reduceResult.Quantity);
            AddLog($"Sell order placed: {reduceResult.Id} - {reduceResult.Status}");
            return;
        }

        if (!IsManualFuturesMode && PositionQuantity <= 0)
        {
            AddLog("No open spot position to sell.");
            return;
        }

        if (IsManualFuturesMode && !WalletVM.TryApproveUsdRisk(TradeNotional, CurrentOpenExposureUsdt, RealizedPnl, out var riskReason))
        {
            AddLog(riskReason);
            return;
        }

        if (IsManualFuturesMode)
        {
            var shortOrder = new Order
            {
                Symbol = symbol,
                Side = OrderSide.Sell,
                Type = OrderType.Market,
                Quantity = TradeQuantity,
                MarketType = TradingMarketType.FuturesUsdM,
                Leverage = ManualFuturesLeverage,
                MarginMode = SelectedManualFuturesMarginModeEnum,
                PositionSide = ManualEntryPositionSide(symbol, CryptoAITerminal.Core.Enums.OrderSide.Sell)
            };

            if (!_riskManager.CanPlaceOrder(shortOrder, CurrentTradePrice, AvailableBalanceUsdt, CurrentOpenExposureUsdt))
            {
                AddLog("Risk manager rejected the sell order.");
                return;
            }
        }

        var quantityToSell = IsManualFuturesMode ? TradeQuantity : Math.Min(TradeQuantity, PositionQuantity);
        AddLog($"Executing market sell of {quantityToSell} {symbol}...");
        var result = await PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, quantityToSell, reduceOnly: false);
        await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, result.Price > 0 ? result.Price : CurrentTradePrice, result.Quantity);
        AddLog($"Sell order placed: {result.Id} - {result.Status}");
    }

    private void PlaceBuyLimit()
    {
        if (LimitPrice <= 0 || TradeQuantity <= 0)
        {
            AddLog("Set a valid limit price and quantity first.");
            return;
        }

        if (IsManualFuturesMode)
        {
            _ = PlaceExchangeLimitAsync(CryptoAITerminal.Core.Enums.OrderSide.Buy, LimitPrice);
            return;
        }

        var requestedSpend = TradeQuantity * LimitPrice;
        if (!WalletVM.TryApproveUsdRisk(requestedSpend, CurrentOpenExposureUsdt, RealizedPnl, out var riskReason))
        {
            AddLog(riskReason);
            return;
        }

        var order = WorkingOrderViewModel.CreateLimit(OrderSide.Buy, SelectedTradingSymbol, TradeQuantity, LimitPrice, SelectedTimeInForce);
        order.AttachCancel(() => CancelSingleOrder(order));
        WorkingOrders.Add(order);
        RaiseWorkingOrdersCollectionChanged();
        AddLog($"BUY LIMIT armed at {LimitPrice:N2} for {TradeQuantity:0.0000} {BaseAssetSymbol}.");
    }

    private void PlaceSellLimit()
    {
        if (LimitPrice <= 0 || TradeQuantity <= 0)
        {
            AddLog("Set a valid limit price and quantity first.");
            return;
        }

        if (IsManualFuturesMode)
        {
            _ = PlaceExchangeLimitAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, LimitPrice);
            return;
        }

        var order = WorkingOrderViewModel.CreateLimit(OrderSide.Sell, SelectedTradingSymbol, Math.Min(TradeQuantity, Math.Max(PositionQuantity, TradeQuantity)), LimitPrice, SelectedTimeInForce);
        order.AttachCancel(() => CancelSingleOrder(order));
        WorkingOrders.Add(order);
        RaiseWorkingOrdersCollectionChanged();
        AddLog($"SELL LIMIT armed at {LimitPrice:N2} for {order.Quantity:0.0000} {BaseAssetSymbol}.");
    }

    private void ArmTakeProfit()
    {
        if (TakeProfitPrice <= 0 || PositionQuantity == 0)
        {
            AddLog("Take-profit requires an open position and a valid trigger.");
            return;
        }

        if (IsManualFuturesMode)
        {
            _ = ArmExchangeProtectionAsync(WorkingOrderKind.TakeProfit, TakeProfitPrice);
            return;
        }

        var order = WorkingOrderViewModel.CreateProtection(WorkingOrderKind.TakeProfit, SelectedTradingSymbol, Math.Abs(PositionQuantity), TakeProfitPrice);
        order.AttachCancel(() => CancelSingleOrder(order));
        WorkingOrders.Add(order);
        RaiseWorkingOrdersCollectionChanged();
        AddLog($"Take-profit armed at {TakeProfitPrice:N2}.");
    }

    private void ArmStopLoss()
    {
        if (StopLossPrice <= 0 || PositionQuantity == 0)
        {
            AddLog("Stop-loss requires an open position and a valid trigger.");
            return;
        }

        if (IsManualFuturesMode)
        {
            _ = ArmExchangeProtectionAsync(WorkingOrderKind.StopLoss, StopLossPrice);
            return;
        }

        var order = WorkingOrderViewModel.CreateProtection(WorkingOrderKind.StopLoss, SelectedTradingSymbol, Math.Abs(PositionQuantity), StopLossPrice);
        order.AttachCancel(() => CancelSingleOrder(order));
        WorkingOrders.Add(order);
        RaiseWorkingOrdersCollectionChanged();
        AddLog($"Stop-loss armed at {StopLossPrice:N2}.");
    }

    private void CancelAllOrders()
    {
        _ = CancelAllOrdersAsync();
    }

    private void CancelSingleOrder(WorkingOrderViewModel order)
    {
        _ = CancelSingleOrderAsync(order);
    }

    private async Task CancelAllOrdersAsync()
    {
        var orders = WorkingOrders.ToList();
        if (orders.Count == 0)
        {
            AddLog("No working orders to cancel.");
            return;
        }

        foreach (var order in orders)
        {
            await CancelSingleOrderAsync(order, suppressLog: true);
        }

        AddLog($"Canceled {orders.Count} working orders.");
    }

    private async Task ExecuteClosePosition()
    {
        if (!WalletVM.TryApproveLiveExecution("CEX close position", out var executionReason))
        {
            AddLog(executionReason);
            return;
        }

        if (PositionQuantity == 0)
        {
            AddLog("No open position to close.");
            return;
        }

        var closeSize = Math.Abs(PositionQuantity);
        var closeSide = PositionQuantity > 0 ? CryptoAITerminal.Core.Enums.OrderSide.Sell : CryptoAITerminal.Core.Enums.OrderSide.Buy;
        AddLog($"Closing position: {closeSize:0.0000} {BaseAssetSymbol} via {closeSide.ToString().ToUpperInvariant()} market.");
        var result = await PlaceCexMarketOrderAsync(closeSide, closeSize, reduceOnly: IsManualFuturesMode);
        await SyncManualExecutionStateAsync(closeSide, result.Price > 0 ? result.Price : CurrentTradePrice, result.Quantity);
        AddLog($"Position closed at {(result.Price > 0 ? result.Price : CurrentTradePrice):N2}.");
    }

    private async Task ExecuteReversePosition()
    {
        if (!WalletVM.TryApproveLiveExecution("CEX reverse position", out var executionReason))
        {
            AddLog(executionReason);
            return;
        }

        if (TradeQuantity <= 0)
        {
            AddLog("Set a positive quantity before reversing.");
            return;
        }

        if (PositionQuantity == 0)
        {
            AddLog("Open a position first before using reverse.");
            return;
        }

        var closeSize = Math.Abs(PositionQuantity);
        var closeSide = PositionQuantity > 0 ? CryptoAITerminal.Core.Enums.OrderSide.Sell : CryptoAITerminal.Core.Enums.OrderSide.Buy;
        var openSide = PositionQuantity > 0 ? CryptoAITerminal.Core.Enums.OrderSide.Sell : CryptoAITerminal.Core.Enums.OrderSide.Buy;

        AddLog($"Reversing position: first closing {closeSize:0.0000} {BaseAssetSymbol}.");
        var closeResult = await PlaceCexMarketOrderAsync(closeSide, closeSize, reduceOnly: IsManualFuturesMode);
        await SyncManualExecutionStateAsync(closeSide, closeResult.Price > 0 ? closeResult.Price : CurrentTradePrice, closeResult.Quantity);

        if (IsManualFuturesMode)
        {
            var openOrder = new Order
            {
                Symbol = SelectedTradingSymbol,
                Side = openSide,
                Type = OrderType.Market,
                Quantity = TradeQuantity,
                MarketType = TradingMarketType.FuturesUsdM,
                Leverage = ManualFuturesLeverage,
                MarginMode = SelectedManualFuturesMarginModeEnum,
                PositionSide = openSide == CryptoAITerminal.Core.Enums.OrderSide.Buy ? FuturesPositionSide.Long : FuturesPositionSide.Short
            };

            if (!WalletVM.TryApproveUsdRisk(TradeNotional, CurrentOpenExposureUsdt, RealizedPnl, out var reverseRiskReason))
            {
                AddLog(reverseRiskReason);
                return;
            }

            if (!_riskManager.CanPlaceOrder(openOrder, CurrentTradePrice, AvailableBalanceUsdt, CurrentOpenExposureUsdt))
            {
                AddLog("Risk manager rejected the reverse open leg.");
                return;
            }
        }

        AddLog($"Opening fresh {(openSide == CryptoAITerminal.Core.Enums.OrderSide.Buy ? "long" : "short")} after reverse: {TradeQuantity:0.0000} {BaseAssetSymbol}.");
        var openResult = await PlaceCexMarketOrderAsync(openSide, TradeQuantity);
        await SyncManualExecutionStateAsync(openSide, openResult.Price > 0 ? openResult.Price : CurrentTradePrice, openResult.Quantity);
        AddLog($"Reverse completed at {(openResult.Price > 0 ? openResult.Price : CurrentTradePrice):N2}.");
    }

    private void SelectLimitPrice(decimal price)
    {
        if (price <= 0)
        {
            return;
        }

        _selectedLadderPrice = price;
        LimitPrice = price;
        UpdateSelectedPriceHighlights();
        AddLog($"Limit price synced from order book: {price:N2}.");
    }

    private void SelectBidPrice(decimal price)
    {
        _preferredBookSide = "BUY";
        SelectedOrderSide = "BUY";
        SelectLimitPrice(price);
        RaiseTradingStateChanged();
        AddLog($"Bid level selected: preparing BUY bias at {price:N2}.");
    }

    private void SelectAskPrice(decimal price)
    {
        _preferredBookSide = "SELL";
        SelectedOrderSide = "SELL";
        SelectLimitPrice(price);
        RaiseTradingStateChanged();
        AddLog($"Ask level selected: preparing SELL bias at {price:N2}.");
    }

    private void SelectOrderSide(string? side)
    {
        var normalized = string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
        _preferredBookSide = normalized;
        SelectedOrderSide = normalized;
        RaiseTradingStateChanged();
    }

    private void SelectMainTab(string? indexRaw)
    {
        var normalized = NormalizeSectionKey(indexRaw);
        var definition = GetSectionDefinition(normalized);
        _selectedShellSection = normalized;
        SelectedTabIndex = definition.TabIndex;
        RaiseShellNavigationStateChanged();

        // Refresh P&L dashboard whenever the analytics section is opened
        if (normalized == "analytics" && PnlDashboardVM is not null)
            PnlDashboardVM.Refresh();

        // Only poll the live tape while its section is on screen.
        if (normalized == "tape")
            MarketTapeVM?.Start();
        else
            MarketTapeVM?.Stop();
    }

    private void FocusMarket(CexMarketItemViewModel? market)
    {
        if (market is null)
        {
            return;
        }

        SelectedMarket = market;
        if (!string.Equals(_selectedShellSection, "markets", StringComparison.OrdinalIgnoreCase))
        {
            _selectedShellSection = "markets";
            RaiseShellNavigationStateChanged();
        }
    }

    private void OpenMarketInTrading(CexMarketItemViewModel? market)
    {
        var targetMarket = market is null
            ? SelectedMarket
            : Markets.FirstOrDefault(item => string.Equals(item.Symbol, market.Symbol, StringComparison.OrdinalIgnoreCase)) ?? market;

        if (SelectedTradingVenue != TradingVenueMode.Cex)
        {
            SelectTradingVenue("CEX");
        }

        if (targetMarket is not null)
        {
            SelectedMarket = targetMarket;
            _focusLatestCandlesOnNextRefresh = true;
            _ = RefreshSelectedOrderBookAsync();
            _ = RefreshSelectedCandlesAsync();
        }

        SelectMainTab("trading");
    }

    private async Task ExecuteSafeLogoutAsync()
    {
        WalletVM.ApplyGlobalExecutionModeCommand.Execute("PAPER").Subscribe(_ => { });

        if (SniperVM.IsArmed || SniperVM.IsScanning)
        {
            SniperVM.StopCommand.Execute().Subscribe(_ => { });
        }

        if (AIBotVM.IsRunning)
        {
            AIBotVM.StopBot();
        }

        if (WalletVM.IsConnected)
        {
            WalletVM.DisconnectWalletCommand.Execute().Subscribe(_ => { });
        }

        SelectMainTab("dashboard");
        AddLog("Safe logout executed: paper mode restored, bot stopped, sniper disarmed, wallet disconnected.");
        await Task.CompletedTask;
    }

    private void ToggleMarketFavorite(CexMarketItemViewModel? market)
    {
        if (market is null)
        {
            return;
        }

        market.IsFavorite = !market.IsFavorite;
        RaiseMarketExplorerStateChanged();
    }

    private async Task RefreshMarketsHubAsync()
    {
        AddLog("Refreshing market radar data and order books.");
        await RefreshAllOrderBooksAsync();
        await RefreshSelectedCandlesAsync();
        RaiseMarketExplorerStateChanged();
    }

    private void ToggleLadderCenterMode()
    {
        _isLadderCenterLocked = !_isLadderCenterLocked;
        if (_isLadderCenterLocked)
        {
            _ladderManualOffsetTicks = 0;
        }

        RebuildTradingLadder();
        RaiseTradingStateChanged();
        AddLog(_isLadderCenterLocked
            ? "DOM recentered and locked to current price."
            : "DOM switched to free scroll mode.");
    }

    public void ScrollLadderByTicks(int tickDelta)
    {
        if (_isLadderCenterLocked || tickDelta == 0)
        {
            return;
        }

        _ladderManualOffsetTicks += tickDelta;
        RebuildTradingLadder();
        RaiseTradingStateChanged();
    }

    private void SelectTradeAllocation(string allocationPercentRaw)
    {
        if (!decimal.TryParse(allocationPercentRaw, out var allocationPercent))
        {
            return;
        }

        WalletVM.GlobalPositionSizingPercent = allocationPercent;
        _pendingGlobalCexSizingApply = true;
        ApplyGlobalCexSizingIfReady();
    }

    private void ApplyGlobalCexSizingIfReady()
    {
        if (CurrentTradePrice <= 0 || AvailableBalanceUsdt <= 0)
        {
            return;
        }

        var ratio = WalletVM.GlobalPositionSizingPercent / 100m;
        TradeQuantity = Math.Round((AvailableBalanceUsdt * ratio) / CurrentTradePrice, 4, MidpointRounding.AwayFromZero);
        _pendingGlobalCexSizingApply = false;
        AddLog($"Global CEX order size synced to {WalletVM.GlobalPositionSizingPercent:0}% of available USDT.");
    }

    private void OnWalletWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WalletWorkspaceViewModel.GlobalPositionSizingPercent))
        {
            _pendingGlobalCexSizingApply = true;
            ApplyGlobalCexSizingIfReady();
            this.RaisePropertyChanged(nameof(GlobalPositionSizingLabel));
            this.RaisePropertyChanged(nameof(GlobalPositionSizingSummary));
            this.RaisePropertyChanged(nameof(RiskModeLabel));
            this.RaisePropertyChanged(nameof(RiskModeBrush));
            this.RaisePropertyChanged(nameof(AiWarningTertiary));
        }

        if (e.PropertyName is nameof(WalletWorkspaceViewModel.IsConnected) or
            nameof(WalletWorkspaceViewModel.WalletAddressShort) or
            nameof(WalletWorkspaceViewModel.SelectedNetwork) or
            nameof(WalletWorkspaceViewModel.SelectedProvider))
        {
            this.RaisePropertyChanged(nameof(ConnectionStateLabel));
            this.RaisePropertyChanged(nameof(ConnectionStateDetailLabel));
            this.RaisePropertyChanged(nameof(SettingsConnectivitySummary));
            this.RaisePropertyChanged(nameof(LogoutStatusLabel));
        }

        if (e.PropertyName is nameof(WalletWorkspaceViewModel.GlobalPositionSizingLabel) or
            nameof(WalletWorkspaceViewModel.GlobalPositionSizingSummary) or
            nameof(WalletWorkspaceViewModel.GlobalPaperOnlyMode) or
            nameof(WalletWorkspaceViewModel.GlobalExecutionModeLabel) or
            nameof(WalletWorkspaceViewModel.GlobalExecutionModeBrush) or
            nameof(WalletWorkspaceViewModel.GlobalExecutionSummary) or
            nameof(WalletWorkspaceViewModel.GlobalMaxSpendPerTradeUsdt) or
            nameof(WalletWorkspaceViewModel.GlobalMaxDailyLossUsdt) or
            nameof(WalletWorkspaceViewModel.GlobalMaxOpenExposureUsdt) or
            nameof(WalletWorkspaceViewModel.GlobalRiskCapLabel) or
            nameof(WalletWorkspaceViewModel.GlobalRiskSummary))
        {
            RaiseCexActionStateChanged();
            this.RaisePropertyChanged(nameof(GlobalPositionSizingLabel));
            this.RaisePropertyChanged(nameof(GlobalPositionSizingSummary));
            this.RaisePropertyChanged(nameof(GlobalExecutionModeLabel));
            this.RaisePropertyChanged(nameof(GlobalExecutionModeBrush));
            this.RaisePropertyChanged(nameof(GlobalExecutionSummary));
            this.RaisePropertyChanged(nameof(ExecutionModeLabel));
            this.RaisePropertyChanged(nameof(GlobalRiskCapLabel));
            this.RaisePropertyChanged(nameof(GlobalRiskSummary));
            this.RaisePropertyChanged(nameof(RiskModeLabel));
            this.RaisePropertyChanged(nameof(RiskModeBrush));
            this.RaisePropertyChanged(nameof(RiskModeSummaryLabel));
            this.RaisePropertyChanged(nameof(AiConfidencePercent));
            this.RaisePropertyChanged(nameof(AiConfidenceLabel));
            this.RaisePropertyChanged(nameof(AiWarningPrimary));
            this.RaisePropertyChanged(nameof(AiWarningSecondary));
            this.RaisePropertyChanged(nameof(AiWarningTertiary));
            this.RaisePropertyChanged(nameof(RiskRuntimeStatusLabel));
            this.RaisePropertyChanged(nameof(RiskRuntimeStatusBrush));
            this.RaisePropertyChanged(nameof(RiskRuntimeSummary));
            this.RaisePropertyChanged(nameof(SettingsConnectivitySummary));
            this.RaisePropertyChanged(nameof(LogoutStatusLabel));
        }

        RefreshAiSignalStudioContext();
    }

    private void OnAIBotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AIBotViewModel.IsRunning) or nameof(AIBotViewModel.SelectedMarketMode) or nameof(AIBotViewModel.FuturesLeverage))
        {
            this.RaisePropertyChanged(nameof(BotsRuntimeStatusLabel));
            this.RaisePropertyChanged(nameof(BotsRuntimeStatusBrush));
            this.RaisePropertyChanged(nameof(LogoutStatusLabel));
        }

        // Share the Claude credentials entered in the AI Bot with the sniper's
        // AI verdict so a single key configures both.
        if (e.PropertyName is nameof(AIBotViewModel.ClaudeApiKey) or nameof(AIBotViewModel.ClaudeModel))
        {
            SniperVM.ConfigureAiVerdict(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            NewsFeedVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            AiTraderVM.Configure(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            CopilotVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            BriefingVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            RiskCheckVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            CorrelationInsightVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            OptionsStrategyVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            AlertsVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            ScannerVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            BacktestVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            CompositeRuleVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            TradeJournalVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            PortfolioRebalanceVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            WhaleTrackerVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            OnChainVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            SentimentVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            LiquidationHeatmapVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            GridBotVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            DexTrendingVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            StatArbVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
            BestExecutionVM.ConfigureAi(AIBotVM.ClaudeApiKey, AIBotVM.ClaudeModel);
        }

        RefreshAiSignalStudioContext();
    }

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
            RefreshMarketExplorerCollections();
            RaiseMarketExplorerStateChanged();
        }
    }

    private async Task PlacePrimaryOrderAsync()
    {
        var isSell = string.Equals(SelectedOrderSide, "SELL", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(SelectedOrderType, "Market", StringComparison.OrdinalIgnoreCase))
        {
            if (isSell)
            {
                await ExecuteSellMarket();
            }
            else
            {
                await ExecuteBuyMarket();
            }

            return;
        }

        if (isSell)
        {
            PlaceSellLimit();
        }
        else
        {
            PlaceBuyLimit();
        }
    }

    private void ShiftLimitPrice(decimal delta)
    {
        var step = PriceStep > 0 ? PriceStep : 0.01m;
        if (LimitPrice <= 0)
        {
            LimitPrice = CurrentTradePrice > 0 ? CurrentTradePrice : step;
        }

        LimitPrice = Math.Max(0m, LimitPrice + (delta < 0 ? -step : step));
        _selectedLadderPrice = LimitPrice;
        UpdateSelectedPriceHighlights();
    }

    // ── Order Template application ────────────────────────────────────────────

    private void ApplyOrderTemplate(Services.OrderTemplate t)
    {
        // Side + type
        SelectedOrderSide = t.Side;
        SelectedOrderType = t.OrderType;

        // Quantity
        TradeQuantity = t.Quantity;

        // Limit price offset from current market
        var refPrice = CurrentTradePrice > 0 ? CurrentTradePrice : LimitPrice;
        if (refPrice > 0 && t.LimitOffsetPct != 0m)
            LimitPrice = Math.Max(0m, refPrice * (1m + t.LimitOffsetPct / 100m));
        else if (refPrice > 0)
            LimitPrice = refPrice;

        // TP / SL computed from the resolved limit price
        var entry = LimitPrice > 0 ? LimitPrice : refPrice;
        if (entry > 0)
        {
            if (t.Side == "BUY")
            {
                TakeProfitPrice = entry * (1m + t.TakeProfitPct / 100m);
                StopLossPrice   = entry * (1m - t.StopLossPct   / 100m);
            }
            else
            {
                TakeProfitPrice = entry * (1m - t.TakeProfitPct / 100m);
                StopLossPrice   = entry * (1m + t.StopLossPct   / 100m);
            }
        }

        AddLog($"[Template] '{t.Name}' (Shift+{t.Slot}) applied — {t.Side} {t.Symbol}  " +
               $"Lmt {LimitPrice:N2}  TP {TakeProfitPrice:N2}  SL {StopLossPrice:N2}");
        ShowToast($"Template '{t.Name}' applied  (Shift+{t.Slot})");
    }

    private void ApplyAtrPreset()
    {
        if (CurrentTradePrice <= 0)
        {
            return;
        }

        var atrProxy = Math.Max(SpreadValue * 3m, CurrentTradePrice * 0.004m);
        StopLossPrice = Math.Max(0m, LimitPrice - atrProxy);
        TakeProfitPrice = LimitPrice + (atrProxy * 2m);
        AddLog($"ATR preset applied. Stop {StopLossPrice:N2}, target {TakeProfitPrice:N2}.");
    }

    private void ApplyRiskRewardPreset()
    {
        if (LimitPrice <= 0)
        {
            return;
        }

        var stopDistance = StopLossPrice > 0 && StopLossPrice < LimitPrice
            ? LimitPrice - StopLossPrice
            : Math.Max(SpreadValue * 4m, LimitPrice * 0.003m);

        StopLossPrice = LimitPrice - stopDistance;
        TakeProfitPrice = LimitPrice + (stopDistance * 2m);
        AddLog($"2R preset applied. Stop {StopLossPrice:N2}, target {TakeProfitPrice:N2}.");
    }

    private void ApplyScalpPreset(string? preset)
    {
        var normalizedPreset = NormalizeScalpPreset(preset);
        SelectedScalpPreset = normalizedPreset;

        if (!string.Equals(_selectedTradingProfile, "Scalp", StringComparison.Ordinal))
        {
            this.RaiseAndSetIfChanged(ref _selectedTradingProfile, "Scalp");
            this.RaisePropertyChanged(nameof(IsScalpProfile));
        }

        var (timeframe, leverage, sizingPercent, slippagePercent, stopBps, takeProfitBps) = normalizedPreset switch
        {
            "Tight" => ("1M", 4, 5m, 0.05m, 12m, 20m),
            "Aggro" => ("1M", 8, 10m, 0.18m, 28m, 45m),
            _ => ("1M", 6, 7.5m, 0.10m, 18m, 30m)
        };

        SelectedTradeTimeframe = timeframe;
        SelectedMarket?.ApplyTimeframe(timeframe);
        _focusLatestCandlesOnNextRefresh = true;
        RaiseTimeframeStateChanged();
        _ = RefreshSelectedCandlesAsync();

        if (IsManualFuturesMode)
        {
            ManualFuturesLeverage = leverage;
        }

        WalletVM.GlobalPositionSizingPercent = sizingPercent;
        SlippageTolerancePercent = slippagePercent;
        SelectedOrderType = "Market";
        ApplyScalpProtectionPreset(stopBps, takeProfitBps);
        this.RaisePropertyChanged(nameof(TradingProfileSummary));
        this.RaisePropertyChanged(nameof(ScalpPresetTargetLabel));
        AddLog($"Scalp preset {normalizedPreset} applied: {timeframe}, {leverage}x, size {sizingPercent:0.##}%, TP {takeProfitBps}bps, SL {stopBps}bps.");
    }

    private void ApplyScalpProtectionPreset(decimal stopBps, decimal takeProfitBps)
    {
        var referencePrice = LimitPrice > 0 ? LimitPrice : CurrentTradePrice;
        if (referencePrice <= 0)
        {
            return;
        }

        var stopRatio = stopBps / 10000m;
        var takeProfitRatio = takeProfitBps / 10000m;
        var isSellBias = string.Equals(SelectedOrderSide, "SELL", StringComparison.OrdinalIgnoreCase);

        if (IsManualFuturesMode && isSellBias)
        {
            TakeProfitPrice = referencePrice * (1m - takeProfitRatio);
            StopLossPrice = referencePrice * (1m + stopRatio);
            return;
        }

        TakeProfitPrice = referencePrice * (1m + takeProfitRatio);
        StopLossPrice = referencePrice * (1m - stopRatio);
    }

    private async Task RefreshAllOrderBooksAsync()
    {
        foreach (var market in Markets)
        {
            await RefreshOrderBookAsync(market);
        }
    }

    private async Task RefreshSelectedOrderBookAsync()
    {
        if (SelectedMarket is null)
        {
            return;
        }

        var targetMarket = SelectedMarket;
        await RefreshOrderBookAsync(targetMarket);
        if (IsManualFuturesMode)
        {
            await RefreshManualAccountStateAsync();
        }
    }

    private async Task RefreshSelectedCandlesAsync()
    {
        if (SelectedMarket is null)
        {
            return;
        }

        var targetSymbol = SelectedMarket.Symbol;
        var targetMarket = SelectedMarket;
        var useFutures = IsManualFuturesMode;
        var requestedTimeframe = SelectedTradeTimeframe;
        var candleLimit = GetCandleLimit(requestedTimeframe);

        await _candleRefreshLock.WaitAsync();
        try
        {
            IReadOnlyList<DexOhlcvPoint> candles;
            try
            {
                candles = useFutures
                    ? await ActiveFuturesGateway.GetCandlesAsync(targetSymbol, requestedTimeframe, candleLimit)
                    : await _gateway.GetCandlesAsync(targetSymbol, requestedTimeframe, candleLimit);
            }
            catch (Exception ex) when (useFutures)
            {
                AddLog($"Futures candles unavailable for {targetSymbol}, using spot display fallback: {ex.Message}");
                candles = await _gateway.GetCandlesAsync(targetSymbol, requestedTimeframe, candleLimit);
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedMarket is null ||
                    !string.Equals(SelectedMarket.Symbol, targetSymbol, StringComparison.OrdinalIgnoreCase) ||
                    !ReferenceEquals(SelectedMarket, targetMarket) ||
                    IsManualFuturesMode != useFutures ||
                    !string.Equals(SelectedTradeTimeframe, requestedTimeframe, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                TradingCandles.Clear();
                foreach (var candle in candles)
                {
                    TradingCandles.Add(candle);
                }
                RefreshQuickBacktestSnapshot();

                var latestClose = TradingCandles.LastOrDefault()?.Close ?? 0m;
                if (latestClose > 0 &&
                    (CurrentMarketData is null || CurrentMarketData.LastPrice <= 0))
                {
                    CurrentMarketData = new MarketData
                    {
                        Symbol = targetSymbol,
                        LastPrice = latestClose,
                        BestBid = SelectedMarket.BestBid,
                        BestAsk = SelectedMarket.BestAsk,
                        Timestamp = DateTime.UtcNow
                    };
                }

                if (_focusLatestCandlesOnNextRefresh)
                {
                    ChartResetViewVersion++;
                    _focusLatestCandlesOnNextRefresh = false;
                }

                RaiseTradingStateChanged();
                RaiseTimeframeStateChanged();
            });
        }
        catch (Exception ex)
        {
            AddLog($"Candle refresh failed for {targetSymbol}: {ex.Message}");
        }
        finally
        {
            _candleRefreshLock.Release();
        }
    }

    private async Task RefreshOrderBookAsync(CexMarketItemViewModel market)
    {
        var useFutures = IsManualFuturesMode;
        var gateway = useFutures ? (IExchangeGateway)_futuresGateway : _gateway;

        await _orderBookRefreshLock.WaitAsync();
        try
        {
            OrderBook orderBook;
            try
            {
                orderBook = await gateway.GetOrderBookAsync(market.Symbol, depth: 50);
                if (useFutures && !HasUsableOrderBook(orderBook))
                {
                    AddLog($"Futures order book for {market.Symbol} returned no depth, using spot display fallback.");
                    orderBook = await _gateway.GetOrderBookAsync(market.Symbol, depth: 50);
                }
            }
            catch (Exception ex) when (useFutures)
            {
                AddLog($"Futures order book unavailable for {market.Symbol}, using spot display fallback: {ex.Message}");
                orderBook = await _gateway.GetOrderBookAsync(market.Symbol, depth: 50);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsManualFuturesMode != useFutures)
                {
                    return;
                }

                market.UpdateOrderBook(orderBook);
                if (ReferenceEquals(SelectedMarket, market))
                {
                    if (CurrentMarketData is null || CurrentMarketData.LastPrice <= 0)
                    {
                        var derivedPrice = orderBook.Bids.FirstOrDefault()?.Price
                            ?? orderBook.Asks.FirstOrDefault()?.Price
                            ?? 0m;

                        if (derivedPrice > 0)
                        {
                            CurrentMarketData = new MarketData
                            {
                                Symbol = market.Symbol,
                                LastPrice = derivedPrice,
                                BestBid = orderBook.Bids.FirstOrDefault()?.Price ?? 0m,
                                BestAsk = orderBook.Asks.FirstOrDefault()?.Price ?? 0m,
                                Timestamp = DateTime.UtcNow
                            };
                        }
                    }

                    RebuildTradingLadder();
                    UpdateSelectedPriceHighlights();
                    RaiseTradingStateChanged();
                    SeedTicketDefaults();
                }
            });
        }
        catch (Exception ex)
        {
            AddLog($"Order book refresh failed for {market.Symbol}: {ex.Message}");
        }
        finally
        {
            _orderBookRefreshLock.Release();
        }
    }

    private async Task EvaluateWorkingOrdersAsync()
    {
        var bestBid   = SelectedMarket?.BestBid > 0 ? SelectedMarket!.BestBid : CurrentTradePrice;
        var bestAsk   = SelectedMarket?.BestAsk > 0 ? SelectedMarket!.BestAsk : CurrentTradePrice;
        var lastPrice = CurrentTradePrice;

        // ── Advanced Trailing Stop tick ───────────────────────────────────────
        if (AdvancedTrailingVM.IsArmed && lastPrice > 0)
        {
            var candleSnap = TradingCandles.ToList();
            AdvancedTrailingVM.OnPriceTick(lastPrice, candleSnap);
        }

        if (SelectedMarket is null || WorkingOrders.Count == 0)
            return;

        var triggered = WorkingOrders
            .Where(order => order.Symbol == SelectedTradingSymbol &&
                            order.ShouldTrigger(bestBid, bestAsk, lastPrice, PositionQuantity))
            .ToList();

        foreach (var order in triggered)
            await ExecuteWorkingOrderAsync(order);
    }

    private async Task ExecuteWorkingOrderAsync(WorkingOrderViewModel order)
    {
        if (!WorkingOrders.Contains(order))
        {
            return;
        }

        WorkingOrders.Remove(order);
        this.RaisePropertyChanged(nameof(WorkingOrdersCountLabel));

        switch (order.Kind)
        {
            case WorkingOrderKind.LimitBuy:
            {
                if (!WalletVM.TryApproveLiveExecution("CEX working buy order", out var buyExecutionReason))
                {
                    AddLog(buyExecutionReason);
                    break;
                }

                var result = await PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide.Buy, order.Quantity);
                await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Buy, order.TriggerPrice > 0 ? order.TriggerPrice : CurrentTradePrice, result.Quantity);
                AddLog($"BUY LIMIT filled at {order.TriggerPrice:N2}.");
                break;
            }
            case WorkingOrderKind.LimitSell:
            {
                if (!WalletVM.TryApproveLiveExecution("CEX working sell order", out var sellExecutionReason))
                {
                    AddLog(sellExecutionReason);
                    break;
                }

                var quantity = Math.Min(order.Quantity, PositionQuantity > 0 ? PositionQuantity : order.Quantity);
                if (quantity <= 0)
                {
                    AddLog("SELL LIMIT removed because there is no position to reduce.");
                    break;
                }

                var result = await PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, quantity, reduceOnly: IsManualFuturesMode);
                await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, order.TriggerPrice > 0 ? order.TriggerPrice : CurrentTradePrice, result.Quantity);
                AddLog($"SELL LIMIT filled at {order.TriggerPrice:N2}.");
                break;
            }
            case WorkingOrderKind.TakeProfit:
            {
                if (!WalletVM.TryApproveLiveExecution("CEX take-profit order", out var tpExecutionReason))
                {
                    AddLog(tpExecutionReason);
                    break;
                }

                var quantity = Math.Min(order.Quantity, PositionQuantity);
                if (quantity <= 0)
                {
                    AddLog("Take-profit removed because the position is already flat.");
                    break;
                }

                var result = await PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, quantity, reduceOnly: IsManualFuturesMode);
                await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, order.TriggerPrice > 0 ? order.TriggerPrice : CurrentTradePrice, result.Quantity);
                AddLog($"TAKE PROFIT triggered at {order.TriggerPrice:N2}.");
                break;
            }
            case WorkingOrderKind.StopLoss:
            {
                if (!WalletVM.TryApproveLiveExecution("CEX stop-loss order", out var slExecutionReason))
                {
                    AddLog(slExecutionReason);
                    break;
                }

                var quantity = Math.Min(order.Quantity, PositionQuantity);
                if (quantity <= 0)
                {
                    AddLog("Stop-loss removed because the position is already flat.");
                    break;
                }

                var result = await PlaceCexMarketOrderAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, quantity, reduceOnly: IsManualFuturesMode);
                await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, order.TriggerPrice > 0 ? order.TriggerPrice : CurrentTradePrice, result.Quantity);
                AddLog($"STOP LOSS triggered at {order.TriggerPrice:N2}.");
                break;
            }
        }
    }

    private void AddLog(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddLog(message), DispatcherPriority.Background);
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogMessages += $"{timestamp} - {message}\n";
        RecentActivityFeed.Insert(0, CreateActivityRow(timestamp, message));

        while (RecentActivityFeed.Count > 8)
        {
            RecentActivityFeed.RemoveAt(RecentActivityFeed.Count - 1);
        }

        this.RaisePropertyChanged(nameof(HasRecentActivityFeed));
        this.RaisePropertyChanged(nameof(ShowRecentActivityPlaceholder));
        this.RaisePropertyChanged(nameof(AnalyticsExecutionSummary));
    }

    private void ApplyFilledBuy(decimal executionPrice, decimal quantity)
    {
        if (PositionQuantity < 0)
        {
            var shortSize = Math.Abs(PositionQuantity);
            var coverQuantity = Math.Min(quantity, shortSize);
            if (AverageEntryPrice > 0)
            {
                RealizedPnl += (AverageEntryPrice - executionPrice) * coverQuantity;
            }

            PositionQuantity += coverQuantity;
            var remainder = quantity - coverQuantity;
            if (PositionQuantity == 0)
            {
                AverageEntryPrice = 0m;
                RemoveProtectionOrders();
                if (IsManualFuturesMode)
                {
                    _ = CancelExchangeManagedProtectionOrdersAsync();
                }
            }

            if (remainder > 0)
            {
                PositionQuantity = remainder;
                AverageEntryPrice = executionPrice;
            }
        }
        else
        {
            var totalPositionCost = (AverageEntryPrice * PositionQuantity) + (executionPrice * quantity);
            PositionQuantity += quantity;
            AverageEntryPrice = PositionQuantity > 0 ? totalPositionCost / PositionQuantity : 0m;
        }

        AvailableBalanceUsdt = Math.Max(0m, AvailableBalanceUsdt - (executionPrice * quantity));
        AddFill(OrderSide.Buy, executionPrice, quantity, "FILLED");
        RefreshPositionRows();
        RaiseTradingStateChanged();
    }

    private void ApplyFilledSell(decimal executionPrice, decimal quantity)
    {
        if (PositionQuantity > 0)
        {
            var closeQuantity = Math.Min(quantity, PositionQuantity);
            if (AverageEntryPrice > 0)
            {
                RealizedPnl += (executionPrice - AverageEntryPrice) * closeQuantity;
            }

            PositionQuantity -= closeQuantity;
            var remainder = quantity - closeQuantity;
            if (PositionQuantity == 0)
            {
                AverageEntryPrice = 0m;
                RemoveProtectionOrders();
                if (IsManualFuturesMode)
                {
                    _ = CancelExchangeManagedProtectionOrdersAsync();
                }
            }

            if (remainder > 0)
            {
                PositionQuantity = -remainder;
                AverageEntryPrice = executionPrice;
            }
        }
        else
        {
            var currentShortSize = Math.Abs(PositionQuantity);
            var totalShortCost = (AverageEntryPrice * currentShortSize) + (executionPrice * quantity);
            PositionQuantity -= quantity;
            AverageEntryPrice = PositionQuantity < 0 ? totalShortCost / Math.Abs(PositionQuantity) : 0m;
        }

        AvailableBalanceUsdt += executionPrice * quantity;
        AddFill(OrderSide.Sell, executionPrice, quantity, "FILLED");
        RefreshPositionRows();
        RaiseTradingStateChanged();
    }

    private void SelectTradeTimeframe(string timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            return;
        }

        SelectedTradeTimeframe = timeframe;
        SelectedMarket?.ApplyTimeframe(timeframe);
        _focusLatestCandlesOnNextRefresh = true;
        RaiseTimeframeStateChanged();
        _ = RefreshSelectedCandlesAsync();
        AddLog($"Trading timeframe focus switched to {timeframe}.");
    }

    private void SelectTradingVenue(string venue)
    {
        SelectedTradingVenue = string.Equals(venue, "DEX", StringComparison.OrdinalIgnoreCase)
            ? TradingVenueMode.Dex
            : TradingVenueMode.Cex;

        RaiseTradingStateChanged();
        RaiseTimeframeStateChanged();
        AddLog($"Trading venue switched to {SelectedTradingVenue}.");
        if (SelectedTradingVenue == TradingVenueMode.Cex)
        {
            StartManualTradingModeRefresh();
        }
    }

    private bool ShouldUseSpotDisplayFallback(MarketData spotData)
    {
        if (!IsManualFuturesMode || SelectedMarket is null)
        {
            return false;
        }

        if (!string.Equals(SelectedMarket.Symbol, spotData.Symbol, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CurrentMarketData is null ||
               CurrentMarketData.LastPrice <= 0 ||
               CurrentMarketData.BestBid <= 0 ||
               CurrentMarketData.BestAsk <= 0;
    }

    private void EnsureSelectedMarketDisplaySnapshot()
    {
        if (SelectedMarket is null)
        {
            return;
        }

        var fallbackPrice = SelectedMarket.LastPrice > 0
            ? SelectedMarket.LastPrice
            : TradingCandles.LastOrDefault()?.Close ?? CurrentTradePrice;

        if (fallbackPrice <= 0)
        {
            return;
        }

        CurrentMarketData = new MarketData
        {
            Symbol = SelectedMarket.Symbol,
            LastPrice = fallbackPrice,
            BestBid = SelectedMarket.BestBid > 0 ? SelectedMarket.BestBid : fallbackPrice,
            BestAsk = SelectedMarket.BestAsk > 0 ? SelectedMarket.BestAsk : fallbackPrice,
            Timestamp = DateTime.UtcNow
        };
    }

    private static bool HasUsableOrderBook(OrderBook orderBook)
    {
        var bestBid = orderBook.Bids.FirstOrDefault();
        var bestAsk = orderBook.Asks.FirstOrDefault();
        return bestBid is not null && bestAsk is not null &&
               bestBid.Price > 0 && bestAsk.Price > 0 &&
               bestBid.Quantity > 0 && bestAsk.Quantity > 0;
    }

    private void SelectChartTool(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return;
        }

        SelectedChartTool = tool;
        SelectedChartToolPhase = ChartToolPhase.None;
        RaiseTimeframeStateChanged();
        AddLog($"Chart tool switched to {tool}.");
    }

    private void ClearChartDrawings()
    {
        ChartClearDrawingsVersion++;
        SelectedChartToolPhase = ChartToolPhase.None;
        AddLog("Chart drawings cleared.");
    }

    private void ResetChartView()
    {
        ChartResetViewVersion++;
        AddLog("Chart view reset.");
    }

    private void ToggleVwap()
    {
        ShowChartVwap = !ShowChartVwap;
        this.RaisePropertyChanged(nameof(ChartVwapBackground));
        this.RaisePropertyChanged(nameof(ChartVwapForeground));
        AddLog($"VWAP overlay {(ShowChartVwap ? "enabled" : "disabled")}.");
    }

    private void ToggleVolumeProfile()
    {
        ShowChartVolumeProfile = !ShowChartVolumeProfile;
        this.RaisePropertyChanged(nameof(ChartVolumeProfileBackground));
        this.RaisePropertyChanged(nameof(ChartVolumeProfileForeground));
        AddLog($"Volume Profile {(ShowChartVolumeProfile ? "enabled" : "disabled")}.");
    }

    private void RaiseTradingStateChanged()
    {
        RaiseCexActionStateChanged();
        this.RaisePropertyChanged(nameof(IsCexTradingMode));
        this.RaisePropertyChanged(nameof(IsDexTradingMode));
        this.RaisePropertyChanged(nameof(IsManualFuturesMode));
        this.RaisePropertyChanged(nameof(CexMarketModeSummary));
        this.RaisePropertyChanged(nameof(CexVenueBackground));
        this.RaisePropertyChanged(nameof(DexVenueBackground));
        this.RaisePropertyChanged(nameof(CexVenueForeground));
        this.RaisePropertyChanged(nameof(DexVenueForeground));
        this.RaisePropertyChanged(nameof(ActiveTradingSymbol));
        this.RaisePropertyChanged(nameof(ActiveTradingTitle));
        this.RaisePropertyChanged(nameof(TradingTerminalSummary));
        this.RaisePropertyChanged(nameof(ActiveTradingCandles));
        this.RaisePropertyChanged(nameof(BaseAssetSymbol));
        this.RaisePropertyChanged(nameof(CurrentTradePrice));
        this.RaisePropertyChanged(nameof(TradeNotional));
        this.RaisePropertyChanged(nameof(TradingChartHeader));
        this.RaisePropertyChanged(nameof(TradeNotionalLabel));
        this.RaisePropertyChanged(nameof(BidDepthTotal));
        this.RaisePropertyChanged(nameof(AskDepthTotal));
        this.RaisePropertyChanged(nameof(BidDepthLabel));
        this.RaisePropertyChanged(nameof(AskDepthLabel));
        this.RaisePropertyChanged(nameof(DepthBalanceLabel));
        this.RaisePropertyChanged(nameof(LatestTradingCandle));
        this.RaisePropertyChanged(nameof(SpreadValue));
        this.RaisePropertyChanged(nameof(SpreadLabel));
        this.RaisePropertyChanged(nameof(SpreadPercent));
        this.RaisePropertyChanged(nameof(SpreadPercentLabel));
        this.RaisePropertyChanged(nameof(ChartHighLabel));
        this.RaisePropertyChanged(nameof(ChartLowLabel));
        this.RaisePropertyChanged(nameof(ChartVolumeLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesMarginModeLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesLeverageLabel));
        this.RaisePropertyChanged(nameof(PreferredBookSideLabel));
        this.RaisePropertyChanged(nameof(PreferredBookSideBrush));
        this.RaisePropertyChanged(nameof(LadderModeLabel));
        this.RaisePropertyChanged(nameof(LadderModeBrush));
        this.RaisePropertyChanged(nameof(LadderOffsetLabel));
        this.RaisePropertyChanged(nameof(PortfolioExposureLabel));
        this.RaisePropertyChanged(nameof(FeedStatusLabel));
        this.RaisePropertyChanged(nameof(CurrentOpenExposureUsdt));
        this.RaisePropertyChanged(nameof(CurrentOpenExposureLabel));
        this.RaisePropertyChanged(nameof(CurrentDailyLossLabel));
        this.RaisePropertyChanged(nameof(PrimaryOrderButtonText));
        this.RaisePropertyChanged(nameof(PrimaryOrderButtonHint));
        this.RaisePropertyChanged(nameof(EstimatedTradingFee));
        this.RaisePropertyChanged(nameof(EstimatedTradingFeeLabel));
        this.RaisePropertyChanged(nameof(EstimatedNetworkFeeUsdt));
        this.RaisePropertyChanged(nameof(EstimatedNetworkFeeLabel));
        this.RaisePropertyChanged(nameof(EstimatedTotalCostLabel));
        this.RaisePropertyChanged(nameof(AccountEquityUsdt));
        this.RaisePropertyChanged(nameof(AccountEquityLabel));
        this.RaisePropertyChanged(nameof(SessionPnlLabel));
        this.RaisePropertyChanged(nameof(SessionPnlBrush));
        this.RaisePropertyChanged(nameof(SessionPnlPercent));
        this.RaisePropertyChanged(nameof(SessionPnlPercentLabel));
        this.RaisePropertyChanged(nameof(StrategyEntryPrice));
        this.RaisePropertyChanged(nameof(StrategyStopPrice));
        this.RaisePropertyChanged(nameof(StrategyTargetPrice));
        this.RaisePropertyChanged(nameof(StrategyTargetTwoPrice));
        this.RaisePropertyChanged(nameof(AiConfidencePercent));
        this.RaisePropertyChanged(nameof(AiConfidenceLabel));
        this.RaisePropertyChanged(nameof(AiOutlookLabel));
        this.RaisePropertyChanged(nameof(AiOutlookBrush));
        this.RaisePropertyChanged(nameof(AiUpdatedLabel));
        this.RaisePropertyChanged(nameof(AiEntryRangeLabel));
        this.RaisePropertyChanged(nameof(AiStopLossDisplay));
        this.RaisePropertyChanged(nameof(AiTakeProfitOneDisplay));
        this.RaisePropertyChanged(nameof(AiTakeProfitTwoDisplay));
        this.RaisePropertyChanged(nameof(AiRiskRewardLabel));
        this.RaisePropertyChanged(nameof(AiValidityLabel));
        this.RaisePropertyChanged(nameof(AiPositionSizeLabel));
        this.RaisePropertyChanged(nameof(AiDirectionLabel));
        this.RaisePropertyChanged(nameof(AiDirectionBrush));
        this.RaisePropertyChanged(nameof(AiWarningPrimary));
        this.RaisePropertyChanged(nameof(AiWarningSecondary));
        this.RaisePropertyChanged(nameof(AiWarningTertiary));
        RaiseAiContextSelectionStateChanged();
        RefreshPositionRows();
        UpdateTradeIdea();
        RefreshSignalRows();
        RaiseMarketExplorerStateChanged();
        RefreshAiSignalStudioContext();
    }

    private void RaiseTradingModeStateChanged()
    {
        RaiseCexActionStateChanged();
        this.RaisePropertyChanged(nameof(IsManualFuturesMode));
        this.RaisePropertyChanged(nameof(TradingTerminalSummary));
        this.RaisePropertyChanged(nameof(CexMarketModeSummary));
        this.RaisePropertyChanged(nameof(CurrentTradePrice));
        this.RaisePropertyChanged(nameof(CurrentFuturesMarginModeLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesLeverageLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesMarkPriceLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesLiquidationLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesEstimatedMarginLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesRoeLabel));
        this.RaisePropertyChanged(nameof(FuturesPrivateApiStatusLabel));
        this.RaisePropertyChanged(nameof(FuturesPrivateApiStatusBrush));
    }

    private void RaiseMarketExplorerStateChanged()
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
        this.RaisePropertyChanged(nameof(AnalyticsMarketSummary));
    }

    private string GetCexExecutionGuardReason(string routeLabel)
    {
        var executionReason = WalletVM.GetExecutionGuardBlockReason(routeLabel);
        if (!string.IsNullOrWhiteSpace(executionReason))
        {
            return executionReason;
        }

        if (IsManualFuturesMode && !IsFuturesPrivateApiReady)
        {
            return "Binance futures private API credentials are required for this live action.";
        }

        if (CurrentTradePrice <= 0m)
        {
            return "Live market data is not ready yet.";
        }

        return string.Empty;
    }

    private string GetCexMarketBuyBlockedReason()
    {
        if (TradeQuantity <= 0m)
        {
            return "Set trade size above zero.";
        }

        var guardReason = GetCexExecutionGuardReason("CEX market buy");
        if (!string.IsNullOrWhiteSpace(guardReason))
        {
            return guardReason;
        }

        if (IsManualFuturesMode &&
            !WalletVM.TryApproveUsdRisk(TradeNotional, CurrentOpenExposureUsdt, RealizedPnl, out var riskReason))
        {
            return riskReason;
        }

        return string.Empty;
    }

    private string GetCexMarketSellBlockedReason()
    {
        if (TradeQuantity <= 0m)
        {
            return "Set trade size above zero.";
        }

        if (!IsManualFuturesMode && PositionQuantity <= 0m)
        {
            return "There is no open spot position to sell.";
        }

        var guardReason = GetCexExecutionGuardReason("CEX market sell");
        if (!string.IsNullOrWhiteSpace(guardReason))
        {
            return guardReason;
        }

        if (IsManualFuturesMode &&
            !WalletVM.TryApproveUsdRisk(TradeNotional, CurrentOpenExposureUsdt, RealizedPnl, out var riskReason))
        {
            return riskReason;
        }

        return string.Empty;
    }

    private string GetCexBuyLimitBlockedReason()
    {
        if (TradeQuantity <= 0m)
        {
            return "Set trade size above zero.";
        }

        if (LimitPrice <= 0m)
        {
            return "Set a limit price above zero.";
        }

        var guardReason = GetCexExecutionGuardReason("CEX buy limit");
        if (!string.IsNullOrWhiteSpace(guardReason))
        {
            return guardReason;
        }

        return WalletVM.TryApproveUsdRisk(TradeQuantity * LimitPrice, CurrentOpenExposureUsdt, RealizedPnl, out var riskReason)
            ? string.Empty
            : riskReason;
    }

    private string GetCexSellLimitBlockedReason()
    {
        if (TradeQuantity <= 0m)
        {
            return "Set trade size above zero.";
        }

        if (LimitPrice <= 0m)
        {
            return "Set a limit price above zero.";
        }

        if (!IsManualFuturesMode && PositionQuantity <= 0m)
        {
            return "There is no open spot position to sell.";
        }

        return GetCexExecutionGuardReason("CEX sell limit");
    }

    private string GetCexTakeProfitBlockedReason()
    {
        if (PositionQuantity == 0m)
        {
            return "Open a position before arming take-profit.";
        }

        if (TakeProfitPrice <= 0m)
        {
            return "Set a take-profit price above zero.";
        }

        return GetCexExecutionGuardReason("CEX take-profit");
    }

    private string GetCexStopLossBlockedReason()
    {
        if (PositionQuantity == 0m)
        {
            return "Open a position before arming stop-loss.";
        }

        if (StopLossPrice <= 0m)
        {
            return "Set a stop-loss price above zero.";
        }

        return GetCexExecutionGuardReason("CEX stop-loss");
    }

    private string GetCexCloseBlockedReason()
    {
        if (PositionQuantity == 0m)
        {
            return "There is no open position to close.";
        }

        return GetCexExecutionGuardReason("CEX close position");
    }

    private string GetCexReverseBlockedReason()
    {
        if (TradeQuantity <= 0m)
        {
            return "Set trade size above zero.";
        }

        if (PositionQuantity == 0m)
        {
            return "Open a position before reversing it.";
        }

        var guardReason = GetCexExecutionGuardReason("CEX reverse position");
        if (!string.IsNullOrWhiteSpace(guardReason))
        {
            return guardReason;
        }

        if (IsManualFuturesMode &&
            !WalletVM.TryApproveUsdRisk(TradeNotional, CurrentOpenExposureUsdt, RealizedPnl, out var riskReason))
        {
            return riskReason;
        }

        return string.Empty;
    }

    private string GetPrimaryOrderBlockedReason()
    {
        var isSell = string.Equals(SelectedOrderSide, "SELL", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(SelectedOrderType, "Market", StringComparison.OrdinalIgnoreCase))
        {
            return isSell ? CexMarketSellBlockedReason : CexMarketBuyBlockedReason;
        }

        return isSell ? CexSellLimitBlockedReason : CexBuyLimitBlockedReason;
    }

    private void RaiseCexActionStateChanged()
    {
        this.RaisePropertyChanged(nameof(CanExecuteCexMarketBuy));
        this.RaisePropertyChanged(nameof(CanExecuteCexMarketSell));
        this.RaisePropertyChanged(nameof(CanExecuteCexBuyLimit));
        this.RaisePropertyChanged(nameof(CanExecuteCexSellLimit));
        this.RaisePropertyChanged(nameof(CanExecuteCexTakeProfit));
        this.RaisePropertyChanged(nameof(CanExecuteCexStopLoss));
        this.RaisePropertyChanged(nameof(CanExecuteCexClose));
        this.RaisePropertyChanged(nameof(CanExecuteCexReverse));
        this.RaisePropertyChanged(nameof(CanPlacePrimaryOrder));
        this.RaisePropertyChanged(nameof(CexMarketBuyBlockedReason));
        this.RaisePropertyChanged(nameof(CexMarketSellBlockedReason));
        this.RaisePropertyChanged(nameof(CexBuyLimitBlockedReason));
        this.RaisePropertyChanged(nameof(CexSellLimitBlockedReason));
        this.RaisePropertyChanged(nameof(CexTakeProfitBlockedReason));
        this.RaisePropertyChanged(nameof(CexStopLossBlockedReason));
        this.RaisePropertyChanged(nameof(CexCloseBlockedReason));
        this.RaisePropertyChanged(nameof(CexReverseBlockedReason));
        this.RaisePropertyChanged(nameof(PrimaryOrderBlockedReason));
        this.RaisePropertyChanged(nameof(TradingGuardStatusLabel));
        this.RaisePropertyChanged(nameof(TradingGuardStatusBrush));
        this.RaisePropertyChanged(nameof(TradingGuardSummary));
        this.RaisePropertyChanged(nameof(TradingGuardDetail));
    }

    private void RaisePositionStateChanged()
    {
        RaiseCexActionStateChanged();
        this.RaisePropertyChanged(nameof(HasOpenManualPosition));
        this.RaisePropertyChanged(nameof(PositionStatusLabel));
        this.RaisePropertyChanged(nameof(EntryPriceLabel));
        this.RaisePropertyChanged(nameof(UnrealizedPnl));
        this.RaisePropertyChanged(nameof(UnrealizedPnlLabel));
        this.RaisePropertyChanged(nameof(ExchangeUnrealizedPnlLabel));
        this.RaisePropertyChanged(nameof(FlatStatusLabel));
        this.RaisePropertyChanged(nameof(EntryCompactLabel));
        this.RaisePropertyChanged(nameof(PnlCompactLabel));
        this.RaisePropertyChanged(nameof(PnlCompactBrush));
        this.RaisePropertyChanged(nameof(CurrentFuturesMarkPriceLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesLiquidationLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesMarginModeLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesLeverageLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesEstimatedMargin));
        this.RaisePropertyChanged(nameof(CurrentFuturesEstimatedMarginLabel));
        this.RaisePropertyChanged(nameof(CurrentFuturesRoePercent));
        this.RaisePropertyChanged(nameof(CurrentFuturesRoeLabel));
        this.RaisePropertyChanged(nameof(AccountEquityUsdt));
        this.RaisePropertyChanged(nameof(AccountEquityLabel));
        this.RaisePropertyChanged(nameof(SessionPnlLabel));
        this.RaisePropertyChanged(nameof(SessionPnlBrush));
        this.RaisePropertyChanged(nameof(SessionPnlPercent));
        this.RaisePropertyChanged(nameof(SessionPnlPercentLabel));
        this.RaisePropertyChanged(nameof(AiWarningSecondary));
    }

    private void RaiseShellNavigationStateChanged()
    {
        this.RaisePropertyChanged(nameof(DashboardNavBackground));
        this.RaisePropertyChanged(nameof(TradingNavBackground));
        this.RaisePropertyChanged(nameof(PortfolioNavBackground));
        this.RaisePropertyChanged(nameof(SignalsNavBackground));
        this.RaisePropertyChanged(nameof(LogsNavBackground));
        this.RaisePropertyChanged(nameof(DashboardNavForeground));
        this.RaisePropertyChanged(nameof(TradingNavForeground));
        this.RaisePropertyChanged(nameof(PortfolioNavForeground));
        this.RaisePropertyChanged(nameof(SignalsNavForeground));
        this.RaisePropertyChanged(nameof(LogsNavForeground));
        this.RaisePropertyChanged(nameof(SniperNavBackground));
        this.RaisePropertyChanged(nameof(SniperNavForeground));
        this.RaisePropertyChanged(nameof(MarketsNavBackground));
        this.RaisePropertyChanged(nameof(MarketsNavForeground));
        this.RaisePropertyChanged(nameof(RiskNavBackground));
        this.RaisePropertyChanged(nameof(RiskNavForeground));
        this.RaisePropertyChanged(nameof(BacktestNavBackground));
        this.RaisePropertyChanged(nameof(BacktestNavForeground));
        this.RaisePropertyChanged(nameof(BotsNavBackground));
        this.RaisePropertyChanged(nameof(BotsNavForeground));
        this.RaisePropertyChanged(nameof(AnalyticsNavBackground));
        this.RaisePropertyChanged(nameof(AnalyticsNavForeground));
        this.RaisePropertyChanged(nameof(SettingsNavBackground));
        this.RaisePropertyChanged(nameof(SettingsNavForeground));
        this.RaisePropertyChanged(nameof(HelpNavBackground));
        this.RaisePropertyChanged(nameof(HelpNavForeground));
        this.RaisePropertyChanged(nameof(LogoutNavBackground));
        this.RaisePropertyChanged(nameof(LogoutNavForeground));
        this.RaisePropertyChanged(nameof(IsRiskSectionVisible));
        this.RaisePropertyChanged(nameof(IsBacktestSectionVisible));
        this.RaisePropertyChanged(nameof(IsBotsSectionVisible));
        this.RaisePropertyChanged(nameof(IsAlertsSectionVisible));
        this.RaisePropertyChanged(nameof(AlertsNavBackground));
        this.RaisePropertyChanged(nameof(AlertsNavForeground));
        this.RaisePropertyChanged(nameof(IsAnalyticsSectionVisible));
        this.RaisePropertyChanged(nameof(IsWhaleTrackerSectionVisible));
        this.RaisePropertyChanged(nameof(WhaleNavBackground));
        this.RaisePropertyChanged(nameof(WhaleNavForeground));
        this.RaisePropertyChanged(nameof(IsTapeSectionVisible));
        this.RaisePropertyChanged(nameof(TapeNavBackground));
        this.RaisePropertyChanged(nameof(TapeNavForeground));
        this.RaisePropertyChanged(nameof(IsFundingRateSectionVisible));
        this.RaisePropertyChanged(nameof(FundingNavBackground));
        this.RaisePropertyChanged(nameof(FundingNavForeground));
        this.RaisePropertyChanged(nameof(IsArbSectionVisible));
        this.RaisePropertyChanged(nameof(ArbNavBackground));
        this.RaisePropertyChanged(nameof(ArbNavForeground));
        this.RaisePropertyChanged(nameof(IsRouterSectionVisible));
        this.RaisePropertyChanged(nameof(RouterNavBackground));
        this.RaisePropertyChanged(nameof(RouterNavForeground));
        this.RaisePropertyChanged(nameof(IsScannerSectionVisible));
        this.RaisePropertyChanged(nameof(ScannerNavBackground));
        this.RaisePropertyChanged(nameof(ScannerNavForeground));
        this.RaisePropertyChanged(nameof(IsLiquidationSectionVisible));
        this.RaisePropertyChanged(nameof(LiquidationNavBackground));
        this.RaisePropertyChanged(nameof(LiquidationNavForeground));
        this.RaisePropertyChanged(nameof(IsRulesSectionVisible));
        this.RaisePropertyChanged(nameof(RulesNavBackground));
        this.RaisePropertyChanged(nameof(RulesNavForeground));
        this.RaisePropertyChanged(nameof(IsJournalSectionVisible));
        this.RaisePropertyChanged(nameof(JournalNavBackground));
        this.RaisePropertyChanged(nameof(JournalNavForeground));
        this.RaisePropertyChanged(nameof(IsGasSectionVisible));
        this.RaisePropertyChanged(nameof(GasNavBackground));
        this.RaisePropertyChanged(nameof(GasNavForeground));
        this.RaisePropertyChanged(nameof(IsPositionsSectionVisible));
        this.RaisePropertyChanged(nameof(PositionsNavBackground));
        this.RaisePropertyChanged(nameof(PositionsNavForeground));
        this.RaisePropertyChanged(nameof(IsNewsSectionVisible));
        this.RaisePropertyChanged(nameof(NewsNavBackground));
        this.RaisePropertyChanged(nameof(NewsNavForeground));
        this.RaisePropertyChanged(nameof(IsOnChainSectionVisible));
        this.RaisePropertyChanged(nameof(OnChainNavBackground));
        this.RaisePropertyChanged(nameof(OnChainNavForeground));
        this.RaisePropertyChanged(nameof(IsCopySectionVisible));
        this.RaisePropertyChanged(nameof(CopyNavBackground));
        this.RaisePropertyChanged(nameof(CopyNavForeground));
        this.RaisePropertyChanged(nameof(IsStatArbSectionVisible));
        this.RaisePropertyChanged(nameof(StatArbNavBackground));
        this.RaisePropertyChanged(nameof(StatArbNavForeground));
        this.RaisePropertyChanged(nameof(IsSettingsSectionVisible));
        this.RaisePropertyChanged(nameof(IsHelpSectionVisible));
        this.RaisePropertyChanged(nameof(IsLogoutSectionVisible));
        this.RaisePropertyChanged(nameof(IsPlaceholderSectionVisible));
        this.RaisePropertyChanged(nameof(CurrentSectionTitle));
        this.RaisePropertyChanged(nameof(CurrentSectionDescription));
        this.RaisePropertyChanged(nameof(CurrentSectionRoadmap));
        this.RaisePropertyChanged(nameof(RiskRuntimeStatusLabel));
        this.RaisePropertyChanged(nameof(RiskRuntimeStatusBrush));
        this.RaisePropertyChanged(nameof(RiskRuntimeSummary));
        this.RaisePropertyChanged(nameof(RiskSniperGuardSummary));
        this.RaisePropertyChanged(nameof(RiskCexExposureSummary));
        RefreshRiskBudgetDisplay();
        this.RaisePropertyChanged(nameof(BotsRuntimeStatusLabel));
        this.RaisePropertyChanged(nameof(BotsRuntimeStatusBrush));
        this.RaisePropertyChanged(nameof(AnalyticsExecutionSummary));
        this.RaisePropertyChanged(nameof(AnalyticsMarketSummary));
        this.RaisePropertyChanged(nameof(AnalyticsSniperSummary));
        this.RaisePropertyChanged(nameof(SettingsConnectivitySummary));
        this.RaisePropertyChanged(nameof(HelpQuickStartSummary));
        this.RaisePropertyChanged(nameof(HelpSafetySummary));
        this.RaisePropertyChanged(nameof(LogoutStatusLabel));
    }

    // Fired on a Telegram reactor thread for each new channel/group message.
    private void OnTelegramChannelMessage(Services.TelegramChannelMessage msg)
    {
        var signal = Services.TelegramSignalParser.Parse(msg.Text);
        if (!signal.IsValid) return; // not a trading call — ignore noise

        TelegramSignalVM.AddIncomingChannelSignal(signal, msg.ChannelTitle);

        var source = string.IsNullOrWhiteSpace(msg.ChannelTitle) ? "Telegram" : msg.ChannelTitle;
        var summary = $"{signal.Side} {signal.Symbol} ({source})";
        Dispatcher.UIThread.Post(() =>
        {
            ShowToast($"📡 Signal: {summary}");
            AddLog($"[TG SIGNAL] {summary} — {signal.RawText.Replace('\n', ' ')}");
        });
    }

    private void OnRealizedTradeRecorded(TradeRecord record)
    {
        // Only today's realized losses count against the daily-loss budget; historical
        // imports (which bypass RecordTrade) and profits are ignored here.
        if (record.PnlUsd >= 0m || record.ClosedAtUtc.Date != DateTime.UtcNow.Date)
        {
            return;
        }

        _riskManager.RecordLoss(Math.Abs(record.PnlUsd));
        Dispatcher.UIThread.Post(RefreshRiskBudgetDisplay);
    }

    private void ApplyRiskLimits()
    {
        _riskManager.UpdateLimits(_riskLimitPositionInput, _riskLimitDailyLossInput);
        // UpdateLimits ignores non-positive values; mirror the effective limits back so
        // the inputs never show a value that wasn't actually applied.
        RiskLimitPositionInput = _riskManager.MaxPositionSizeUsd;
        RiskLimitDailyLossInput = _riskManager.MaxDailyLossUsd;
        RefreshRiskBudgetDisplay();
    }

    private void ResetDailyRisk()
    {
        _riskManager.ResetDailyLoss();
        _lastRiskBudgetLevel = RiskManager.RiskBudgetLevel.Ok;
        RefreshRiskBudgetDisplay();
        AddLog("[RISK] Daily loss budget reset.");
    }

    private void RefreshRiskBudgetDisplay()
    {
        this.RaisePropertyChanged(nameof(RiskBudgetPositionCapLabel));
        this.RaisePropertyChanged(nameof(RiskBudgetDailyLossLabel));
        this.RaisePropertyChanged(nameof(RiskBudgetRemainingLabel));
        this.RaisePropertyChanged(nameof(RiskBudgetBlockReason));
        this.RaisePropertyChanged(nameof(RiskBudgetBlockBrush));
        this.RaisePropertyChanged(nameof(RiskBudgetWarningLabel));
        this.RaisePropertyChanged(nameof(RiskBudgetWarningBrush));
        this.RaisePropertyChanged(nameof(RiskBlockHistoryLines));
        this.RaisePropertyChanged(nameof(HasRiskBlockHistory));
        this.RaisePropertyChanged(nameof(RiskDailyLossSparkline));
        this.RaisePropertyChanged(nameof(HasRiskDailyLossSparkline));

        // Toast only when the budget level worsens (Ok→Caution→Critical), never on every tick.
        var level = _riskManager.GetBudgetSnapshot().Level;
        if (level > _lastRiskBudgetLevel)
        {
            if (level == RiskManager.RiskBudgetLevel.Critical)
            {
                ShowToast("🚨 Risk: critical — near or over the daily loss limit.");
            }
            else if (level == RiskManager.RiskBudgetLevel.Caution)
            {
                ShowToast("⚠️ Risk: caution — over half the daily loss budget used.");
            }
        }

        _lastRiskBudgetLevel = level;
    }

    private void RefreshQuickBacktestSnapshot()
    {
        _quickBacktestSnapshot = BuildQuickBacktestSnapshot();
        this.RaisePropertyChanged(nameof(BacktestStatusLabel));
        this.RaisePropertyChanged(nameof(BacktestStatusBrush));
        this.RaisePropertyChanged(nameof(BacktestWindowLabel));
        this.RaisePropertyChanged(nameof(BacktestTradeCountLabel));
        this.RaisePropertyChanged(nameof(BacktestWinRateLabel));
        this.RaisePropertyChanged(nameof(BacktestNetReturnLabel));
        this.RaisePropertyChanged(nameof(BacktestDrawdownLabel));
        this.RaisePropertyChanged(nameof(BacktestBestTradeLabel));
        this.RaisePropertyChanged(nameof(BacktestWorstTradeLabel));
        this.RaisePropertyChanged(nameof(BacktestLastSignalLabel));
        this.RaisePropertyChanged(nameof(BacktestBiasLabel));
        this.RaisePropertyChanged(nameof(BacktestNarrative));
    }

    private QuickBacktestSnapshot BuildQuickBacktestSnapshot()
    {
        if (TradingCandles.Count < 30)
        {
            return QuickBacktestSnapshot.Empty with
            {
                WindowLabel = TradingCandles.Count == 0
                    ? "Load a trading chart to simulate the rule set."
                    : $"Only {TradingCandles.Count} candles loaded. Need at least 30."
            };
        }

        const int fastPeriod = 9;
        const int slowPeriod = 21;
        var closes = TradingCandles
            .Select(static candle => candle.Close)
            .Where(static close => close > 0m)
            .ToArray();

        if (closes.Length < slowPeriod)
        {
            return QuickBacktestSnapshot.Empty with { WindowLabel = "Not enough valid closing prices for simulation." };
        }

        var tradeReturns = new List<decimal>();
        var equity = 100m;
        var peakEquity = equity;
        var maxDrawdown = 0m;
        decimal? entryPrice = null;
        var lastSignal = "Hold";

        for (var index = slowPeriod; index < closes.Length; index++)
        {
            var fastNow = closes[(index - fastPeriod + 1)..(index + 1)].Average();
            var slowNow = closes[(index - slowPeriod + 1)..(index + 1)].Average();
            var fastPrev = closes[(index - fastPeriod)..index].Average();
            var slowPrev = closes[(index - slowPeriod)..index].Average();
            var price = closes[index];
            var crossedUp = fastPrev <= slowPrev && fastNow > slowNow;
            var crossedDown = fastPrev >= slowPrev && fastNow < slowNow;

            if (entryPrice is null && crossedUp)
            {
                entryPrice = price;
                lastSignal = "Long";
                continue;
            }

            if (entryPrice is not null && crossedDown)
            {
                var tradeReturn = entryPrice.Value <= 0m ? 0m : ((price - entryPrice.Value) / entryPrice.Value) * 100m;
                tradeReturns.Add(tradeReturn);
                equity *= 1m + (tradeReturn / 100m);
                peakEquity = Math.Max(peakEquity, equity);
                if (peakEquity > 0m)
                {
                    maxDrawdown = Math.Max(maxDrawdown, ((peakEquity - equity) / peakEquity) * 100m);
                }

                entryPrice = null;
                lastSignal = "Flat";
            }
        }

        if (entryPrice is not null)
        {
            var finalPrice = closes[^1];
            var tradeReturn = entryPrice.Value <= 0m ? 0m : ((finalPrice - entryPrice.Value) / entryPrice.Value) * 100m;
            tradeReturns.Add(tradeReturn);
            equity *= 1m + (tradeReturn / 100m);
            peakEquity = Math.Max(peakEquity, equity);
            if (peakEquity > 0m)
            {
                maxDrawdown = Math.Max(maxDrawdown, ((peakEquity - equity) / peakEquity) * 100m);
            }
            lastSignal = "Long";
        }

        if (tradeReturns.Count == 0)
        {
            return QuickBacktestSnapshot.Empty with
            {
                IsReady = true,
                WindowLabel = $"{TradingCandles.Count} candles | {SelectedTradeTimeframe}",
                LastSignal = lastSignal,
                BiasLabel = lastSignal == "Long" ? "Trend-following bias" : "No valid crossover exits yet",
                Narrative = "The loaded chart has enough candles, but the rule set has not completed a full crossover trade yet."
            };
        }

        var wins = tradeReturns.Count(static trade => trade > 0m);
        var winRate = (decimal)wins / tradeReturns.Count * 100m;
        var netReturn = equity - 100m;
        var averageReturn = tradeReturns.Average();
        var bestTrade = tradeReturns.Max();
        var worstTrade = tradeReturns.Min();
        var bias = averageReturn >= 0m ? "Momentum positive" : "Momentum defensive";

        return new QuickBacktestSnapshot(
            true,
            $"{TradingCandles.Count} candles | {SelectedTradeTimeframe}",
            tradeReturns.Count,
            winRate,
            netReturn,
            maxDrawdown,
            bestTrade,
            worstTrade,
            lastSignal,
            bias,
            $"Quick MA(9/21) simulation on {SelectedTradingSymbol} produced {tradeReturns.Count} closed trades with {winRate:0.#}% win rate and {netReturn:+0.##;-0.##;0}% net return.");
    }

    private bool IsWorkspaceSection(string sectionKey) =>
        string.Equals(SelectedShellSection, sectionKey, StringComparison.OrdinalIgnoreCase);

    private string GetCurrentWorkspaceTitle() => SelectedShellSection switch
    {
        "risk"     => "Risk Command Center",
        "backtest" => "Quick Backtest Lab",
        "bots"     => "Rule Bot Console",
        "whale"    => "Whale Tracker",
        "analytics" => "Execution Analytics",
        "settings" => "Workspace Settings",
        "help"     => "Operator Guide",
        "logout"   => "Safe Logout",
        _ => GetSectionDefinition(SelectedShellSection).Title
    };

    private string GetCurrentWorkspaceDescription() => SelectedShellSection switch
    {
        "risk"     => "Unified limits from wallet, manual trading and sniper protections in one operational view.",
        "backtest" => "A fast local scenario engine built on the currently loaded candle set, no AI layer involved.",
        "bots"     => "Rule-based bot controls for spot and futures with direct runtime status and guardrails.",
        "whale"    => "On-chain monitoring of large transfers (> $500K) on ETH, BSC and Solana with labeled wallet alerts.",
        "analytics" => "Session breadth, execution counts and sniper outcome statistics across the active workspace.",
        "settings" => "Global execution mode, quote asset, sizing and wallet connectivity controls.",
        "help"     => "A compact operator playbook for launching, trading, monitoring and shutting down safely.",
        "logout"   => "Return the terminal to a safe idle state before you leave the session.",
        _ => GetSectionDefinition(SelectedShellSection).Description
    };

    private string GetCurrentWorkspaceRoadmap() => SelectedShellSection switch
    {
        "risk" => "Track the live risk budget, confirm paper/live mode, and inspect sniper safety before opening new exposure.",
        "backtest" => "Use the currently loaded candles to stress the built-in rule set and compare return, drawdown and last bias.",
        "bots"  => "Configure the rule bot, start or stop it, and verify whether futures credentials are ready before enabling execution.",
        "whale" => "Start tracking, review recent large on-chain transfers, check labeled wallet activity and snipe a buy in one click.",
        "analytics" => "Review live execution counts, market breadth and combined sniper outcomes without leaving the shell workspace.",
        "settings" => "Adjust wallet-wide execution settings, quote routing, sizing presets and connectivity from one place.",
        "help" => "Follow the quick start path, safety checklist and shutdown steps when onboarding or rechecking the terminal.",
        "logout" => "One action disarms sniper, stops the rule bot, switches back to paper mode and disconnects the wallet.",
        _ => GetSectionDefinition(SelectedShellSection).Roadmap
    };

    private void RaiseOrderTicketStateChanged()
    {
        RaiseCexActionStateChanged();
        this.RaisePropertyChanged(nameof(IsLimitOrderType));
        this.RaisePropertyChanged(nameof(BuySideBackground));
        this.RaisePropertyChanged(nameof(SellSideBackground));
        this.RaisePropertyChanged(nameof(BuySideForeground));
        this.RaisePropertyChanged(nameof(SellSideForeground));
        this.RaisePropertyChanged(nameof(PrimaryOrderButtonText));
        this.RaisePropertyChanged(nameof(PrimaryOrderButtonHint));
        this.RaisePropertyChanged(nameof(EstimatedTradingFee));
        this.RaisePropertyChanged(nameof(EstimatedTradingFeeLabel));
        this.RaisePropertyChanged(nameof(EstimatedNetworkFeeUsdt));
        this.RaisePropertyChanged(nameof(EstimatedNetworkFeeLabel));
        this.RaisePropertyChanged(nameof(EstimatedTotalCostLabel));
        this.RaisePropertyChanged(nameof(StrategyEntryPrice));
        this.RaisePropertyChanged(nameof(StrategyStopPrice));
        this.RaisePropertyChanged(nameof(StrategyTargetPrice));
        this.RaisePropertyChanged(nameof(StrategyTargetTwoPrice));
        this.RaisePropertyChanged(nameof(AiEntryRangeLabel));
        this.RaisePropertyChanged(nameof(AiStopLossDisplay));
        this.RaisePropertyChanged(nameof(AiTakeProfitOneDisplay));
        this.RaisePropertyChanged(nameof(AiTakeProfitTwoDisplay));
        this.RaisePropertyChanged(nameof(AiRiskRewardLabel));
        this.RaisePropertyChanged(nameof(AiValidityLabel));
        this.RaisePropertyChanged(nameof(AiDirectionLabel));
        this.RaisePropertyChanged(nameof(AiDirectionBrush));
        this.RaisePropertyChanged(nameof(AiWarningTertiary));
    }

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

        return SelectedMarketSortMode switch
        {
            "Spread" => query.OrderBy(market => market.SpreadPercent <= 0 ? decimal.MaxValue : market.SpreadPercent)
                             .ThenByDescending(market => market.ActivityScore),
            "Updated" => query.OrderByDescending(market => market.LastUpdated)
                              .ThenByDescending(market => market.ActivityScore),
            "Alphabetical" => query.OrderBy(market => market.BaseAssetSymbol),
            "Price" => query.OrderByDescending(market => market.LastPrice),
            _ => query.OrderByDescending(market => market.ActivityScore)
                      .ThenByDescending(market => Math.Abs(market.ChangePercent))
                      .ThenBy(market => market.SpreadPercent <= 0 ? decimal.MaxValue : market.SpreadPercent)
        };
    }

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
        try { ok = await _gateway.AddSymbolAsync(symbol); }
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
        RaiseMarketExplorerStateChanged();
        SelectedMarket = market;
        NewMarketSymbol = "";
        MarketsStatus = $"{symbol} добавлен.";
    }

    private void RemoveCustomMarket(CexMarketItemViewModel market)
    {
        if (market is null) return;
        if (!_customMarketSymbols.Contains(market.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            MarketsStatus = "Стандартные монеты удалять нельзя.";
            return;
        }

        market.PropertyChanged -= OnMarketItemPropertyChanged;
        Markets.Remove(market);
        _customMarketSymbols.RemoveAll(s => string.Equals(s, market.Symbol, StringComparison.OrdinalIgnoreCase));
        SaveCustomMarketSymbols();
        if (ReferenceEquals(SelectedMarket, market)) SelectedMarket = Markets.FirstOrDefault();
        RefreshMarketExplorerCollections();
        RaiseMarketExplorerStateChanged();
        MarketsStatus = $"{market.Symbol} удалён (вернётся после перезапуска только если снова добавить).";
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
        }
        finally
        {
            _isRefreshingMarketExplorerCollections = false;
        }
    }

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

    private string GetShellNavBackground(int tabIndex) =>
        SelectedTabIndex == tabIndex && !IsPlaceholderSectionVisible ? "#12293A" : "Transparent";

    private string GetShellNavForeground(int tabIndex) =>
        SelectedTabIndex == tabIndex && !IsPlaceholderSectionVisible ? "#21E6C1" : "#7A96AF";

    private string GetShellSectionBackground(string sectionKey) =>
        string.Equals(_selectedShellSection, sectionKey, StringComparison.OrdinalIgnoreCase) ? "#12293A" : "Transparent";

    private string GetShellSectionForeground(string sectionKey) =>
        string.Equals(_selectedShellSection, sectionKey, StringComparison.OrdinalIgnoreCase) ? "#21E6C1" : "#7A96AF";

    private static string NormalizeSectionKey(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "0" or "dashboard" => "dashboard",
        "1" or "markets" => "markets",
        "2" or "trading" => "trading",
        "3" or "portfolio" => "portfolio",
        "4" or "ai-signals" or "signals" => "ai-signals",
        "5" or "sniper" or "sniper-coins" => "sniper",
        "6" or "dex" => "dex",
        "7" or "logs" => "logs",
        "risk" => "risk",
        "backtest" => "backtest",
        "bots" => "bots",
        "analytics" => "analytics",
        "whale" or "whales" or "whale-tracker" => "whale",
        "tape" or "live-tape" or "livetape" or "trades" or "time-and-sales" => "tape",
        "funding" or "funding-rate" or "fundingrate" or "fundingrates" => "funding",
        "liquidation" or "liq" or "liq-map" or "liquidations" => "liquidation",
        "rules" or "composite" or "composite-bot" or "rule-bot" => "rules",
        "arb" or "arbitrage" or "cross-arb" or "cross-exchange-arb" or "cross-exchange" => "arb",
        "scanner" or "market-scanner" or "marketscanner" => "scanner",
        "router" or "best-execution" or "best-execution-router" or "execution-router" => "router",
        "journal" or "trade-journal" or "tradelog" => "journal",
        "gas" or "gas-monitor" or "fees" or "gas-fees" => "gas",
        "positions" or "all-positions" or "allpositions" or "open-positions" => "positions",
        "news" or "news-feed" or "newsfeed" or "sentiment" => "news",
        "onchain" or "on-chain" or "metrics" or "glassnode" or "mvrv" or "nupl" => "onchain",
        "copy" or "copy-trading" or "copytrading" or "copy-trade" => "copy",
        "statarb" or "stat-arb" or "pairs" or "pairs-trading" or "pairstrading" => "statarb",
        "settings" => "settings",
        "help" => "help",
        "logout" => "logout",
        _ => "dashboard"
    };

    private static ShellSectionDefinition GetSectionDefinition(string sectionKey) => sectionKey switch
    {
        "dashboard" => new(0, false, "Dashboard", string.Empty, string.Empty),
        "markets" => new(1, false, "Markets", string.Empty, string.Empty),
        "trading" => new(2, false, "Trading", string.Empty, string.Empty),
        "portfolio" => new(3, false, "Portfolio", string.Empty, string.Empty),
        "ai-signals" => new(4, false, "AI Signals", string.Empty, string.Empty),
        "sniper" => new(5, false, "Sniper", string.Empty, string.Empty),
        "dex" => new(6, false, "DEX", string.Empty, string.Empty),
        "logs" => new(7, false, "Logs", string.Empty, string.Empty),
        "risk" => new(7, true, "Risk", "Control center for limits, execution mode and protective rules.", "Shows the risk budget, execution guard and a summary of manual trading and sniper."),
        "backtest" => new(7, true, "Backtest", "A fast local scenario run on already loaded candles.", "Computes the rule-based strategy result, win rate, drawdown and current bias."),
        "bots" => new(7, true, "Bots", "Start/stop console for the rule-based bot on spot and futures.", "Gives direct access to symbol, size, risk cap, margin mode, bias and leverage."),
        "whale" => new(7, true, "Whale Tracker", "Monitoring of large on-chain transfers and known wallet activity.", "Shows transfers > $500K on ETH/BSC/Solana, labeled addresses and Sniper entry."),
        "tape" => new(7, true, "Live Tape", "Live public-trade tape: every participant's anonymous fills on a symbol.", "Polls the Binance public recent-trades feed, highlights large prints and shows buy/sell pressure. No identity is exposed by the exchange."),
        "funding" => new(7, true, "Funding Rates", "Real-time funding rate table for all Binance USD-M perpetuals.", "Alerts on extreme values (>0.1% / <-0.1%), chart history and best long/short arbitrage picks."),
        "liquidation" => new(7, true, "Liq Heatmap", "Liquidation map — clusters of stops and forced closures.", "Shows levels where longs/shorts get liquidated. Source: CoinGlass API or Binance OB estimation."),
        "rules"   => new(7, true, "Composite Bot", "Visual Condition → Action rule builder, no code.", "Build rules: RSI < 30 AND Volume > SMA×1.5 → DCA Buy. The engine evaluates all rules every 10 seconds."),
        "arb"     => new(7, true, "Cross-Exchange Arbitrage", "Monitors price discrepancies of one asset across exchanges.", "Scans Binance/Bybit/OKX for selected symbols, highlights spreads above the threshold and supports one-click execution of an order pair."),
        "scanner" => new(7, true, "Market Scanner", "Finds momentum setups: top movers, new ATHs, breakouts and volatility expansion.", "Scans USDT perpetuals and the spot market, ranks by % change, RSI, volume Z-score and volatility expansion. Click a result to open Trading focused on that pair."),
        "router"  => new(7, true, "Best Execution Router", "Best-execution router: computes effective price including order book and fees.", "Compares Binance/Bybit/OKX for the same symbol at a given size, shows the net price including slippage and fees and picks the optimal exchange."),
        "journal"    => new(7, true, "Trade Journal",    "Trade journal with notes, tags and stats per entry style.",     "Add notes to trades, apply tags (Scalp, Swing, BotSignal…) and export to CSV for tax reporting."),
        "gas"        => new(7, true, "Gas Monitor",      "Monitors transaction costs on Ethereum, BSC and Solana network load.",     "Alerts when gas drops — the ideal moment for large DEX swaps. Updates every 30 seconds."),
        "positions"  => new(7, true, "All Positions",    "A single table of all open futures positions across exchanges.",             "Total unrealized PnL, an emergency «Close all» button, and sorting by PnL%, size or exchange."),
        "news"       => new(7, true, "News Feed",        "RSS news aggregator (CoinTelegraph, CoinDesk, Decrypt, etc.) with automatic sentiment classification.", "Bullish / bearish / neutral news. Filter by coin. Alerts on important events."),
        "onchain"    => new(7, true, "On-Chain",         "Key BTC and ETH on-chain metrics via the CoinMetrics Community API (free, no key).",      "MVRV ratio, NUPL (≈ 1−1/MVRV), Exchange Net Flow — warnings about market overheating and accumulation."),
        "copy"       => new(7, true, "Copy Trading",    "Publishes every bot trade to /api/copy-trades — followers mirror in real time.",         "Leader mode: all closed AI Bot / Grid Bot trades are written to JSON. Follower: polls the leader and scales qty by ScaleRatio."),
        "statarb"    => new(7, true, "Stat Arb",        "Pairs trading: trades the z-score of the log spread of two cointegrated assets.",              "Opens a neutral position when |z| > EntryZ (BTC / ETH by default) and closes when |z| < ExitZ. Hedge ratio is computed from candle history."),
        "analytics"  => new(7, true, "Analytics", "Summary of execution, market breadth and sniper module outcomes.", "Collects session PnL, execution counts, breadth and combined trade stats."),
        "settings" => new(7, true, "Settings", "A single panel for global execution parameters and the wallet session.", "Controls quote asset, sizing, provider, network and execution mode."),
        "help" => new(7, true, "Help", "A concise operator guide to the terminal workflow.", "Contains a quick start, safety notes and the basic steps for safe operation."),
        "logout" => new(7, true, "Logout", "Safely ends the session and returns the terminal to idle state.", "Switches to paper mode, stops the bot, disarms the sniper and disconnects the wallet session."),
        _ => new(0, false, "Dashboard", string.Empty, string.Empty)
    };

    private string GetOrderSideBackground(string side) =>
        string.Equals(SelectedOrderSide, side, StringComparison.OrdinalIgnoreCase)
            ? side == "SELL" ? "#402125" : "#16372F"
            : "#101821";

    private string GetOrderSideForeground(string side) =>
        string.Equals(SelectedOrderSide, side, StringComparison.OrdinalIgnoreCase)
            ? side == "SELL" ? "#FF857B" : "#42F5B1"
            : "#8FA3B8";

    private static ActivityFeedRowViewModel CreateActivityRow(string timestamp, string message)
    {
        var status = "Info";
        var brush = "#8FA3B8";
        var normalized = message.ToUpperInvariant();

        if (normalized.Contains("FILLED"))
        {
            status = "Filled";
            brush = "#3DDC84";
        }
        else if (normalized.Contains("CANCEL"))
        {
            status = "Canceled";
            brush = "#F4B860";
        }
        else if (normalized.Contains("PLACED") || normalized.Contains("ARMED"))
        {
            status = "New";
            brush = "#21E6C1";
        }
        else if (normalized.Contains("FAILED") || normalized.Contains("REJECTED"))
        {
            status = "Risk";
            brush = "#FF6B6B";
        }

        return new ActivityFeedRowViewModel(timestamp, message, status, brush);
    }

    private void RaiseTimeframeStateChanged()
    {
        this.RaisePropertyChanged(nameof(TradingChartHeader));
        this.RaisePropertyChanged(nameof(ChartRangeLabel));
        this.RaisePropertyChanged(nameof(ChartInteractionHint));
        this.RaisePropertyChanged(nameof(Timeframe1MBackground));
        this.RaisePropertyChanged(nameof(Timeframe5MBackground));
        this.RaisePropertyChanged(nameof(Timeframe15MBackground));
        this.RaisePropertyChanged(nameof(Timeframe1HBackground));
        this.RaisePropertyChanged(nameof(Timeframe4HBackground));
        this.RaisePropertyChanged(nameof(Timeframe1DBackground));
        this.RaisePropertyChanged(nameof(Timeframe1WBackground));
        this.RaisePropertyChanged(nameof(Timeframe1MNBackground));
        this.RaisePropertyChanged(nameof(TimeframeAllBackground));
        this.RaisePropertyChanged(nameof(Timeframe1MForeground));
        this.RaisePropertyChanged(nameof(Timeframe5MForeground));
        this.RaisePropertyChanged(nameof(Timeframe15MForeground));
        this.RaisePropertyChanged(nameof(Timeframe1HForeground));
        this.RaisePropertyChanged(nameof(Timeframe4HForeground));
        this.RaisePropertyChanged(nameof(Timeframe1DForeground));
        this.RaisePropertyChanged(nameof(Timeframe1WForeground));
        this.RaisePropertyChanged(nameof(Timeframe1MNForeground));
        this.RaisePropertyChanged(nameof(TimeframeAllForeground));
        this.RaisePropertyChanged(nameof(ChartCursorBackground));
        this.RaisePropertyChanged(nameof(ChartTrendBackground));
        this.RaisePropertyChanged(nameof(ChartHorizontalBackground));
        this.RaisePropertyChanged(nameof(ChartRectangleBackground));
        this.RaisePropertyChanged(nameof(ChartChannelBackground));
        this.RaisePropertyChanged(nameof(ChartEraseBackground));
        this.RaisePropertyChanged(nameof(ChartCursorForeground));
        this.RaisePropertyChanged(nameof(ChartTrendForeground));
        this.RaisePropertyChanged(nameof(ChartHorizontalForeground));
        this.RaisePropertyChanged(nameof(ChartRectangleForeground));
        this.RaisePropertyChanged(nameof(ChartChannelForeground));
        this.RaisePropertyChanged(nameof(ChartEraseForeground));
    }

    private string GetTimeframeBackground(string timeframe) =>
        string.Equals(SelectedTradeTimeframe, timeframe, StringComparison.OrdinalIgnoreCase) ? "#17373B" : "#0F1721";

    private string GetTimeframeForeground(string timeframe) =>
        string.Equals(SelectedTradeTimeframe, timeframe, StringComparison.OrdinalIgnoreCase) ? "#F4F7FB" : "#8FA3B8";

    private string GetChartToolBackground(string tool) =>
        string.Equals(SelectedChartTool, tool, StringComparison.OrdinalIgnoreCase) ? "#17373B" : "#0F1721";

    private string GetChartToolForeground(string tool) =>
        string.Equals(SelectedChartTool, tool, StringComparison.OrdinalIgnoreCase) ? "#F4F7FB" : "#8FA3B8";

    private static string NormalizeScalpPreset(string? preset) =>
        preset?.Trim().ToUpperInvariant() switch
        {
            "TIGHT" => "Tight",
            "AGGRO" => "Aggro",
            _ => "Standard"
        };

    private static string GetScalpPresetSummary(string preset) => NormalizeScalpPreset(preset) switch
    {
        "Tight" => "1M · 4x · 5% size · TP 0.20% · SL 0.12%",
        "Aggro" => "1M · 8x · 10% size · TP 0.45% · SL 0.28%",
        _ => "1M · 6x · 7.5% size · TP 0.30% · SL 0.18%"
    };

    private static int GetCandleLimit(string timeframe) => timeframe switch
    {
        "1M" => 90,
        "5M" => 120,
        "15M" => 140,
        "1H" => 160,
        "4H" => 180,
        "1D" => 200,
        "1W" => 120,
        "1MN" => 120,
        "ALL" => 1000,
        _ => 120
    };

    private void UpdateTradeIdea()
    {
        if (SelectedMarket is null || CurrentTradePrice <= 0)
        {
            TradeIdeaTitle = "Waiting for live market context";
            TradeIdeaSummary = "Connect to a symbol to see a suggested entry zone, stop and target.";
            SuggestedEntryLabel = "--";
            SuggestedStopLabel = "--";
            SuggestedTargetLabel = "--";
            return;
        }

        var bestBid = SelectedMarket.BestBid > 0 ? SelectedMarket.BestBid : CurrentTradePrice;
        var bestAsk = SelectedMarket.BestAsk > 0 ? SelectedMarket.BestAsk : CurrentTradePrice;
        var entry = SelectedMarket.IsPositiveTrend ? bestAsk : bestBid;
        var stop = entry * 0.992m;
        var target = entry * 1.014m;
        var imbalance = BidDepthTotal - AskDepthTotal;

        TradeIdeaTitle = SelectedMarket.IsPositiveTrend
            ? "Momentum setup"
            : "Range / pullback setup";

        TradeIdeaSummary = SelectedMarket.IsPositiveTrend
            ? imbalance >= 0
                ? "Bids hold up better than asks. Bias stays long while the inside market remains firm."
                : "Price trend is still positive, but ask liquidity is heavier. Better to wait for a cleaner lift."
            : imbalance >= 0
                ? "Order book shows dip buying. A cautious reclaim entry near the bid makes more sense than chasing."
                : "Book pressure is skewed to the ask side. Keep size smaller or wait for support to rebuild.";

        SuggestedEntryLabel = $"{entry:N2}";
        SuggestedStopLabel = $"{stop:N2}";
        SuggestedTargetLabel = $"{target:N2}";
    }

    private void InitializeAiSignalStudio()
    {
        var contextAsset = EffectiveAiContextSymbol;
        var contextTitle = EffectiveAiContextTitle;

        RebuildAiPromptPresetOptions();
        RebuildAiSavedPromptPresets();

        if (string.IsNullOrWhiteSpace(AiPromptPresetAsset))
        {
            AiPromptPresetAsset = contextAsset;
        }

        AiAssistantQuickPrompts.Clear();
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel("entry", TranslateUi("Explain setup"), TranslateUi("Explain the current setup, market structure, and what matters most right now.")));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel("risk", TranslateUi("Risk check"), TranslateUi("Run a risk check on the active setup and tell me what can invalidate it.")));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel("dex", TranslateUi("DEX route"), TranslateUi("Explain the current DEX route, quote asset, and what can block execution.")));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel("sniper", TranslateUi("Sniper pulse"), TranslateUi("Summarize sniper readiness, open slots, and whether the flow is hot or defensive.")));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel("visual", TranslateUi("Visual read"), TranslateUi("Walk me through the selected visual and what it means for crypto execution.")));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel("entry", TranslateUi("Next action"), TranslateUi("Give me the cleanest next action for this terminal state.")));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "entry",
            TranslateUi("Entry / Exit"),
            _localization.IsRussian
                ? $"Подсчитай, когда лучше заходить и выходить по {contextAsset}. Дай сценарий входа, инвалидацию, TP1, TP2 и условие, при котором в сделку лучше не входить."
                : $"Calculate the best entry and exit plan for {contextAsset}. Give the entry trigger, invalidation, TP1, TP2, and the condition where the trade should be skipped."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "entry",
            TranslateUi("Buy now or wait"),
            _localization.IsRussian
                ? $"Скажи по {contextAsset}, лучше входить сейчас или ждать. Сравни немедленный вход против входа от отката и назови более чистый вариант."
                : $"Tell me whether {contextAsset} should be bought now or waited on. Compare an immediate entry versus a pullback entry and name the cleaner option."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "entry",
            TranslateUi("Scalp map"),
            _localization.IsRussian
                ? $"Собери короткий scalp-план по {contextAsset}: точка входа, быстрый выход, стоп, отмена идеи и что должно подтвердить импульс."
                : $"Build a short scalp map for {contextAsset}: entry, quick exit, stop, invalidation, and what confirms momentum."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "exit",
            TranslateUi("Stop / Target"),
            _localization.IsRussian
                ? $"Рассчитай для {contextAsset} разумный стоп и цели. Объясни, где стоп будет логичным по структуре, а где он будет слишком узким или слишком широким."
                : $"Calculate a sensible stop and target structure for {contextAsset}. Explain where the stop is structurally valid and where it is too tight or too wide."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "risk",
            TranslateUi("Position plan"),
            _localization.IsRussian
                ? $"Собери план позиции по {contextAsset}: размер, риск, сценарий добора, сценарий частичной фиксации и главный блокер для входа."
                : $"Build a position plan for {contextAsset}: size, risk, add-on scenario, partial take-profit scenario, and the main blocker before entry."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "risk",
            TranslateUi("Coin review"),
            _localization.IsRussian
                ? $"Сделай полный разбор монеты {contextTitle}: стоит ли вообще её торговать сейчас, какие риски самые важные и какой сценарий входа выглядит самым чистым."
                : $"Do a full review of {contextTitle}: whether it is worth trading now, which risks matter most, and which entry scenario looks the cleanest."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "exit",
            TranslateUi("Take profit ladder"),
            _localization.IsRussian
                ? $"Построй по {contextAsset} лесенку фиксации: где закрыть первую часть, где вторую, где перевести стоп в безубыток и когда лучше забрать всё."
                : $"Build a take-profit ladder for {contextAsset}: where to trim the first part, the second part, move the stop to breakeven, and fully exit."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "risk",
            TranslateUi("Invalidation check"),
            _localization.IsRussian
                ? $"Покажи по {contextAsset}, что именно сломает текущую идею. Назови ценовую инвалидацию, риск по размеру и признаки, что вход уже поздний."
                : $"Show what invalidates the current idea on {contextAsset}. Name the price invalidation, sizing risk, and the signs that the entry is already late."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "dex",
            TranslateUi("DEX entry safety"),
            _localization.IsRussian
                ? $"Проверь безопасно ли входить в {contextTitle} через DEX сейчас: маршрут, котируемый актив, ликвидность, блокеры исполнения и красные флаги."
                : $"Check whether entering {contextTitle} on DEX is safe right now: route, quote asset, liquidity, execution blockers, and red flags."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "dex",
            TranslateUi("Route blocker"),
            _localization.IsRussian
                ? $"Разбери, что сейчас больше всего мешает DEX-исполнению по {contextTitle}, и что надо поменять, чтобы маршрут стал чище."
                : $"Break down what is blocking DEX execution most for {contextTitle} right now and what should change to make the route cleaner."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "sniper",
            TranslateUi("Fresh pair check"),
            _localization.IsRussian
                ? $"Сделай свежую проверку новой пары: стоит ли вообще следить за ней сейчас, какие риски перегрева и когда её лучше пропустить."
                : $"Run a fresh new-pair check: whether it is worth watching now, the overheating risks, and when it is better to skip it."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "sniper",
            TranslateUi("Slot pressure"),
            _localization.IsRussian
                ? $"Оцени давление по слотам sniper-модуля и скажи, стоит ли добавлять ещё одну позицию или лучше сохранять избирательность."
                : $"Assess sniper slot pressure and tell me whether it makes sense to add one more position or stay selective."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "visual",
            TranslateUi("Chart read"),
            _localization.IsRussian
                ? $"Разбери текущий визуал как карту рынка: где импульс сильный, где слабость, где зона решения и где идея теряет смысл."
                : $"Read the current visual as a market map: where momentum is strong, where weakness appears, where the decision zone is, and where the idea breaks."));
        AiAssistantQuickPrompts.Add(new AiAssistantQuickPromptViewModel(
            "visual",
            TranslateUi("Visual risk map"),
            _localization.IsRussian
                ? $"По текущему визуалу покажи, где самый опасный вход, где лучший вход и где риск становится непропорциональным."
                : $"Using the current visual, show the most dangerous entry, the best entry, and where risk becomes disproportionate."));
        AiAssistantKnowledgeTopics.Clear();
        AiAssistantKnowledgeTopics.Add(new AiKnowledgeTopicViewModel(TranslateUi("Slippage"), TranslateUi("Execution"), TranslateUi("What is slippage and how should I think about it in this terminal?"), TranslateUi("Slippage is the gap between the quoted price and the actual fill. In this desk it matters most when spread widens, size is too large for available depth, or DEX liquidity is thin. If spread is elevated or the route looks fragile, prefer smaller size, staged entries, or a deeper limit instead of chasing market execution.")));
        AiAssistantKnowledgeTopics.Add(new AiKnowledgeTopicViewModel(TranslateUi("Limit vs Market"), TranslateUi("Execution"), TranslateUi("When should I use a limit order instead of a market order?"), TranslateUi("Use market when speed matters more than precision and the spread is healthy. Use limit when the spread is wide, when you want a defined entry zone, or when you are sizing into thinner liquidity. In this terminal the assistant already watches spread, route readiness, and execution guards, so a blocked or defensive state usually argues for patience rather than a market sweep.")));
        AiAssistantKnowledgeTopics.Add(new AiKnowledgeTopicViewModel(TranslateUi("Stop Loss"), TranslateUi("Risk"), TranslateUi("How should I place a stop loss on a crypto setup?"), TranslateUi("A good stop should invalidate the trade idea, not just sit at an arbitrary percentage. Put it beyond the structure that would prove your premise wrong, then back-solve size from the allowed loss. If the stop has to be too wide for your risk budget, reduce size or skip the trade.")));
        AiAssistantKnowledgeTopics.Add(new AiKnowledgeTopicViewModel(TranslateUi("DEX Safety"), TranslateUi("DEX"), TranslateUi("What should I verify before trading a fresh DEX token?"), TranslateUi("Check route readiness, quote asset compatibility, wallet network match, liquidity quality, and whether execution is still paper-only. For fresh pairs also watch holder concentration, suspicious taxes, and whether sniper flow already looks overcrowded. A clean route alone is not enough if the token itself is structurally dangerous.")));
        AiAssistantKnowledgeTopics.Add(new AiKnowledgeTopicViewModel(TranslateUi("New Pair Filter"), TranslateUi("Sniper"), TranslateUi("What makes a new pair worth watching instead of chasing?"), TranslateUi("The best new-pair setups are not just fast, they are readable. You want healthy liquidity, manageable spread, a believable route, controlled slot pressure, and no obvious risk alarms. If the session is already stretched or consecutive losses are stacking, the right move is often to stay selective rather than force action.")));

        RefreshAiSignalStudioContext();
        ClearAiAssistantConversation();
        this.RaisePropertyChanged(nameof(AiQuickPromptCountLabel));
        this.RaisePropertyChanged(nameof(AiKnowledgeCountLabel));
        this.RaisePropertyChanged(nameof(AiEntryPrompts));
        this.RaisePropertyChanged(nameof(AiExitPrompts));
        this.RaisePropertyChanged(nameof(AiRiskPrompts));
        this.RaisePropertyChanged(nameof(AiDexPrompts));
        this.RaisePropertyChanged(nameof(AiSniperPrompts));
        this.RaisePropertyChanged(nameof(AiVisualPrompts));
        this.RaisePropertyChanged(nameof(AiPromptPresetSummary));
        this.RaisePropertyChanged(nameof(AiPromptPresetPreview));
        this.RaisePropertyChanged(nameof(AiSavedPresetCountLabel));
        this.RaisePropertyChanged(nameof(AiCustomPresetCountLabel));
    }

    private void RebuildAiPromptPresetOptions()
    {
        RefillLocalizedOptions(
            AiPromptTradeStyleOptions,
            [TranslateUi("Spot Entry"), TranslateUi("Swing Entry"), TranslateUi("Scalp"), TranslateUi("Breakout"), TranslateUi("Pullback"), TranslateUi("DEX Buy"), TranslateUi("Sniper Review")],
            currentValue: ref _selectedAiPromptTradeStyle);
        RefillLocalizedOptions(
            AiPromptHorizonOptions,
            [TranslateUi("Scalp"), TranslateUi("Intraday"), TranslateUi("1-2 Days"), TranslateUi("Swing"), TranslateUi("Event Driven")],
            currentValue: ref _selectedAiPromptHorizon);
        RefillLocalizedOptions(
            AiPromptRiskProfileOptions,
            [TranslateUi("Conservative"), TranslateUi("Balanced"), TranslateUi("Aggressive")],
            currentValue: ref _selectedAiPromptRiskProfile);
        RefillLocalizedOptions(
            AiPromptFocusOptions,
            [TranslateUi("Entry Timing"), TranslateUi("Exit Timing"), TranslateUi("Risk Map"), TranslateUi("Setup Quality"), TranslateUi("DEX Safety"), TranslateUi("Sniper Readiness")],
            currentValue: ref _selectedAiPromptFocus);

        this.RaisePropertyChanged(nameof(SelectedAiPromptTradeStyle));
        this.RaisePropertyChanged(nameof(SelectedAiPromptHorizon));
        this.RaisePropertyChanged(nameof(SelectedAiPromptRiskProfile));
        this.RaisePropertyChanged(nameof(SelectedAiPromptFocus));
    }

    private static void RefillLocalizedOptions(ObservableCollection<string> target, IReadOnlyList<string> values, ref string currentValue)
    {
        var currentIndex = target.Count > 0 ? Math.Max(0, target.IndexOf(currentValue)) : 0;
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }

        if (target.Count == 0)
        {
            currentValue = string.Empty;
            return;
        }

        currentValue = currentIndex >= 0 && currentIndex < target.Count
            ? target[currentIndex]
            : target[0];
    }

    private void RebuildAiSavedPromptPresets()
    {
        var defaultAsset = EffectiveAiContextVenue == TradingVenueMode.Dex
            ? EffectiveAiContextSymbol
            : (string.Equals(SelectedTradingSymbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase) ? "BTCUSDT" : SelectedTradingSymbol);

        AiSavedPromptPresets.Clear();
        AiSavedPromptPresets.Add(new AiPromptPresetViewModel(
            TranslateUi("Scalp BTC"),
            TranslateUi("Fast BTC intraday scalp with timing and invalidation focus."),
            "BTCUSDT",
            TranslateUi("Scalp"),
            TranslateUi("Scalp"),
            TranslateUi("Balanced"),
            TranslateUi("Entry Timing")));
        AiSavedPromptPresets.Add(new AiPromptPresetViewModel(
            TranslateUi("Swing ETH"),
            TranslateUi("Higher-timeframe ETH setup with clean entries and exits."),
            "ETHUSDT",
            TranslateUi("Swing Entry"),
            TranslateUi("Swing"),
            TranslateUi("Conservative"),
            TranslateUi("Setup Quality")));
        AiSavedPromptPresets.Add(new AiPromptPresetViewModel(
            TranslateUi("DEX Degen"),
            TranslateUi("Aggressive DEX token review with safety and route focus."),
            EffectiveAiContextVenue == TradingVenueMode.Dex ? defaultAsset : "DEX",
            TranslateUi("DEX Buy"),
            TranslateUi("Intraday"),
            TranslateUi("Aggressive"),
            TranslateUi("DEX Safety")));
        AiSavedPromptPresets.Add(new AiPromptPresetViewModel(
            TranslateUi("Sniper Filter"),
            TranslateUi("Selective new-pair screening with slot pressure awareness."),
            defaultAsset,
            TranslateUi("Sniper Review"),
            TranslateUi("Event Driven"),
            TranslateUi("Balanced"),
            TranslateUi("Sniper Readiness")));
        AiSavedPromptPresets.Add(new AiPromptPresetViewModel(
            TranslateUi("Risk Review"),
            TranslateUi("Position sizing, invalidation, and exit ladder review."),
            defaultAsset,
            TranslateUi("Spot Entry"),
            TranslateUi("Intraday"),
            TranslateUi("Conservative"),
            TranslateUi("Risk Map")));
    }

    private void LoadAiCustomPromptPresetsFromDisk()
    {
        try
        {
            if (!File.Exists(AiCustomPresetsStoragePath))
            {
                return;
            }

            var snapshot = AtomicJsonFile.Read<List<AiPromptPresetStorageItem>>(AiCustomPresetsStoragePath, AiPresetJsonOptions) ?? [];
            AiCustomPromptPresets.Clear();
            foreach (var item in snapshot)
            {
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                AiCustomPromptPresets.Add(new AiPromptPresetViewModel(
                    item.Name,
                    BuildCustomPresetSummary(
                        item.Asset,
                        LocalizeAiPromptTradeStyle(item.TradeStyle),
                        LocalizeAiPromptHorizon(item.Horizon),
                        LocalizeAiPromptRiskProfile(item.RiskProfile),
                        LocalizeAiPromptFocus(item.Focus)),
                    item.Asset,
                    LocalizeAiPromptTradeStyle(item.TradeStyle),
                    LocalizeAiPromptHorizon(item.Horizon),
                    LocalizeAiPromptRiskProfile(item.RiskProfile),
                    LocalizeAiPromptFocus(item.Focus)));
            }
        }
        catch
        {
            AtomicJsonFile.BackupCorruptFile(AiCustomPresetsStoragePath);
            AiCustomPromptPresets.Clear();
        }
    }

    private void PersistAiCustomPromptPresets()
    {
        var snapshot = AiCustomPromptPresets
            .Select(item => new AiPromptPresetStorageItem(
                item.Label,
                item.Asset,
                NormalizeAiPromptTradeStyleKey(item.TradeStyle),
                NormalizeAiPromptHorizonKey(item.Horizon),
                NormalizeAiPromptRiskProfileKey(item.RiskProfile),
                NormalizeAiPromptFocusKey(item.Focus)))
            .ToList();

        AtomicJsonFile.Write(AiCustomPresetsStoragePath, snapshot, AiPresetJsonOptions);
    }

    private void SaveAiCustomPromptPreset()
    {
        var name = AiCustomPresetName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var existing = AiCustomPromptPresets.FirstOrDefault(item => string.Equals(item.Label, name, StringComparison.OrdinalIgnoreCase));
        var preset = new AiPromptPresetViewModel(
            name,
            BuildCustomPresetSummary(AiPromptPresetResolvedAsset, SelectedAiPromptTradeStyle, SelectedAiPromptHorizon, SelectedAiPromptRiskProfile, SelectedAiPromptFocus),
            AiPromptPresetResolvedAsset,
            SelectedAiPromptTradeStyle,
            SelectedAiPromptHorizon,
            SelectedAiPromptRiskProfile,
            SelectedAiPromptFocus);

        if (existing is null)
        {
            AiCustomPromptPresets.Insert(0, preset);
        }
        else
        {
            var index = AiCustomPromptPresets.IndexOf(existing);
            AiCustomPromptPresets[index] = preset;
        }

        PersistAiCustomPromptPresets();
        AiCustomPresetName = string.Empty;
        AiAssistantStatusLabel = TranslateUi("MY PRESET SAVED");
        AiAssistantStatusBrush = "#21E6C1";
        this.RaisePropertyChanged(nameof(AiCustomPresetCountLabel));
        RaiseAiConversationStateChanged();
    }

    private void DeleteAiCustomPromptPreset(AiPromptPresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        var existing = AiCustomPromptPresets.FirstOrDefault(item => string.Equals(item.Label, preset.Label, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        AiCustomPromptPresets.Remove(existing);
        PersistAiCustomPromptPresets();
        AiAssistantStatusLabel = TranslateUi("MY PRESET DELETED");
        AiAssistantStatusBrush = "#F4B860";
        this.RaisePropertyChanged(nameof(AiCustomPresetCountLabel));
        RaiseAiConversationStateChanged();
    }

    private string BuildCustomPresetSummary(string asset, string tradeStyle, string horizon, string riskProfile, string focus) =>
        _localization.IsRussian
            ? $"{asset} · {tradeStyle} · {horizon} · {riskProfile} · {focus}"
            : $"{asset} · {tradeStyle} · {horizon} · {riskProfile} · {focus}";

    private string LocalizeAiPromptTradeStyle(string key) => key switch
    {
        "spot_entry" => TranslateUi("Spot Entry"),
        "swing_entry" => TranslateUi("Swing Entry"),
        "scalp" => TranslateUi("Scalp"),
        "breakout" => TranslateUi("Breakout"),
        "pullback" => TranslateUi("Pullback"),
        "dex_buy" => TranslateUi("DEX Buy"),
        "sniper_review" => TranslateUi("Sniper Review"),
        _ => key
    };

    private string LocalizeAiPromptHorizon(string key) => key switch
    {
        "scalp" => TranslateUi("Scalp"),
        "intraday" => TranslateUi("Intraday"),
        "1_2_days" => TranslateUi("1-2 Days"),
        "swing" => TranslateUi("Swing"),
        "event_driven" => TranslateUi("Event Driven"),
        _ => key
    };

    private string LocalizeAiPromptRiskProfile(string key) => key switch
    {
        "conservative" => TranslateUi("Conservative"),
        "balanced" => TranslateUi("Balanced"),
        "aggressive" => TranslateUi("Aggressive"),
        _ => key
    };

    private string LocalizeAiPromptFocus(string key) => key switch
    {
        "entry_timing" => TranslateUi("Entry Timing"),
        "exit_timing" => TranslateUi("Exit Timing"),
        "risk_map" => TranslateUi("Risk Map"),
        "setup_quality" => TranslateUi("Setup Quality"),
        "dex_safety" => TranslateUi("DEX Safety"),
        "sniper_readiness" => TranslateUi("Sniper Readiness"),
        _ => key
    };

    private string NormalizeAiPromptTradeStyleKey(string value)
    {
        if (string.Equals(value, TranslateUi("Spot Entry"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Spot Entry", StringComparison.OrdinalIgnoreCase)) return "spot_entry";
        if (string.Equals(value, TranslateUi("Swing Entry"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Swing Entry", StringComparison.OrdinalIgnoreCase)) return "swing_entry";
        if (string.Equals(value, TranslateUi("Scalp"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Scalp", StringComparison.OrdinalIgnoreCase)) return "scalp";
        if (string.Equals(value, TranslateUi("Breakout"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Breakout", StringComparison.OrdinalIgnoreCase)) return "breakout";
        if (string.Equals(value, TranslateUi("Pullback"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Pullback", StringComparison.OrdinalIgnoreCase)) return "pullback";
        if (string.Equals(value, TranslateUi("DEX Buy"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "DEX Buy", StringComparison.OrdinalIgnoreCase)) return "dex_buy";
        if (string.Equals(value, TranslateUi("Sniper Review"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Sniper Review", StringComparison.OrdinalIgnoreCase)) return "sniper_review";
        return value;
    }

    private string NormalizeAiPromptHorizonKey(string value)
    {
        if (string.Equals(value, TranslateUi("Scalp"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Scalp", StringComparison.OrdinalIgnoreCase)) return "scalp";
        if (string.Equals(value, TranslateUi("Intraday"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Intraday", StringComparison.OrdinalIgnoreCase)) return "intraday";
        if (string.Equals(value, TranslateUi("1-2 Days"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "1-2 Days", StringComparison.OrdinalIgnoreCase)) return "1_2_days";
        if (string.Equals(value, TranslateUi("Swing"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Swing", StringComparison.OrdinalIgnoreCase)) return "swing";
        if (string.Equals(value, TranslateUi("Event Driven"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Event Driven", StringComparison.OrdinalIgnoreCase)) return "event_driven";
        return value;
    }

    private string NormalizeAiPromptRiskProfileKey(string value)
    {
        if (string.Equals(value, TranslateUi("Conservative"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Conservative", StringComparison.OrdinalIgnoreCase)) return "conservative";
        if (string.Equals(value, TranslateUi("Balanced"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Balanced", StringComparison.OrdinalIgnoreCase)) return "balanced";
        if (string.Equals(value, TranslateUi("Aggressive"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Aggressive", StringComparison.OrdinalIgnoreCase)) return "aggressive";
        return value;
    }

    private string NormalizeAiPromptFocusKey(string value)
    {
        if (string.Equals(value, TranslateUi("Entry Timing"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Entry Timing", StringComparison.OrdinalIgnoreCase)) return "entry_timing";
        if (string.Equals(value, TranslateUi("Exit Timing"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Exit Timing", StringComparison.OrdinalIgnoreCase)) return "exit_timing";
        if (string.Equals(value, TranslateUi("Risk Map"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Risk Map", StringComparison.OrdinalIgnoreCase)) return "risk_map";
        if (string.Equals(value, TranslateUi("Setup Quality"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Setup Quality", StringComparison.OrdinalIgnoreCase)) return "setup_quality";
        if (string.Equals(value, TranslateUi("DEX Safety"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "DEX Safety", StringComparison.OrdinalIgnoreCase)) return "dex_safety";
        if (string.Equals(value, TranslateUi("Sniper Readiness"), StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Sniper Readiness", StringComparison.OrdinalIgnoreCase)) return "sniper_readiness";
        return value;
    }

    private void RefreshAiSignalStudioContext()
    {
        AiAssistantContextCards.Clear();
        if (AiIncludeMarketContext)
        {
            AiAssistantContextCards.Add(new AiContextCardViewModel(
                _localization.IsRussian ? "РЫНОК" : "MARKET",
                EffectiveAiContextSymbol,
                EffectiveAiContextVenue == TradingVenueMode.Dex
                    ? DexTradingVM.ChartStatusMessage
                    : (_localization.IsRussian ? $"{AiOutlookLabel} · Спред {SpreadPercentLabel}" : $"{AiOutlookLabel} · Spread {SpreadPercentLabel}")));
            AiAssistantContextCards.Add(new AiContextCardViewModel(
                _localization.IsRussian ? "СЕТАП" : "SETUP",
                EffectiveAiContextVenue == TradingVenueMode.Dex ? DexTradingVM.SelectedTokenTitle : AiEntryRangeLabel,
                EffectiveAiContextVenue == TradingVenueMode.Dex
                    ? DexTradingVM.GlobalQuoteSummary
                    : (_localization.IsRussian ? $"SL {AiStopLossDisplay} · TP {AiTakeProfitOneDisplay}" : $"SL {AiStopLossDisplay} · TP {AiTakeProfitOneDisplay}")));
        }

        if (AiIncludeRiskContext)
        {
            AiAssistantContextCards.Add(new AiContextCardViewModel(_localization.IsRussian ? "РИСК" : "RISK", WalletVM.GlobalRiskCapLabel, WalletVM.GlobalRiskSummary));
        }

        AiAssistantContextCards.Add(new AiContextCardViewModel(_localization.IsRussian ? "МАРШРУТ" : "ROUTE", WalletVM.GlobalExecutionModeLabel, WalletVM.RouteReadinessSummary));

        if (AiIncludeDexContext)
        {
            AiAssistantContextCards.Add(new AiContextCardViewModel("DEX", DexTradingVM.SelectedTokenTitle, DexTradingVM.DexGuardSummary));
        }

        if (AiIncludeSniperContext)
        {
            AiAssistantContextCards.Add(new AiContextCardViewModel(
                "SNIPER",
                SniperVM.PresetSummary,
                _localization.IsRussian
                    ? $"Открыто {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions} · Побед {SniperVM.CombinedWinRateLabel}"
                    : $"Open {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions} · Win {SniperVM.CombinedWinRateLabel}"));
        }

        var selectedKey = SelectedAiVisual?.Key;
        var visuals = BuildAiVisualCards();
        AiAssistantVisuals.Clear();
        foreach (var visual in visuals)
        {
            AiAssistantVisuals.Add(visual);
        }

        SelectedAiVisual = AiAssistantVisuals.FirstOrDefault(item => string.Equals(item.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            ?? AiAssistantVisuals.FirstOrDefault();

        AiAssistantStatusBrush = WalletVM.GlobalPaperOnlyMode ? "#F4B860" : "#21E6C1";
        AiAssistantStatusLabel = TranslateUi(WalletVM.GlobalPaperOnlyMode ? "LOCAL DESK MODE" : "LIVE-AWARE COPILOT");
        this.RaisePropertyChanged(nameof(AiWorkspaceHeadline));
        this.RaisePropertyChanged(nameof(AiWorkspaceSubheadline));
        this.RaisePropertyChanged(nameof(AiConversationCountLabel));
        this.RaisePropertyChanged(nameof(AiVisualCountLabel));
        this.RaisePropertyChanged(nameof(AiAssistantScopeLabel));
        this.RaisePropertyChanged(nameof(AiAssistantModeSummary));
    }

    private TradingVenueMode EffectiveAiContextVenue => SelectedAiContextVenueMode switch
    {
        AiContextVenueMode.Cex => TradingVenueMode.Cex,
        AiContextVenueMode.Dex => TradingVenueMode.Dex,
        _ => SelectedTradingVenue
    };

    private string EffectiveAiTradingSummary => EffectiveAiContextVenue == TradingVenueMode.Dex
        ? DexTradingVM.WalletTradeStatus
        : TradingTerminalSummary;

    private void SetAiContextToggle(ref bool field, bool value, string propertyName, string backgroundProperty, string foregroundProperty)
    {
        if (field == value)
        {
            return;
        }

        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        this.RaisePropertyChanged(backgroundProperty);
        this.RaisePropertyChanged(foregroundProperty);
        this.RaisePropertyChanged(nameof(AiContextSourcesSummary));
        RefreshAiSignalStudioContext();
    }

    private void SelectAiContextVenueMode(string mode)
    {
        SelectedAiContextVenueMode = mode?.ToUpperInvariant() switch
        {
            "CEX" => AiContextVenueMode.Cex,
            "DEX" => AiContextVenueMode.Dex,
            _ => AiContextVenueMode.Auto
        };

        AiAssistantStatusLabel = TranslateUi("CONTEXT UPDATED");
        AiAssistantStatusBrush = "#6FDBFF";
        RaiseAiConversationStateChanged();
    }

    private void ToggleAiContextSource(string source)
    {
        switch (source?.ToUpperInvariant())
        {
            case "MARKET":
                AiIncludeMarketContext = !AiIncludeMarketContext;
                break;
            case "RISK":
                AiIncludeRiskContext = !AiIncludeRiskContext;
                break;
            case "DEX":
                AiIncludeDexContext = !AiIncludeDexContext;
                break;
            case "SNIPER":
                AiIncludeSniperContext = !AiIncludeSniperContext;
                break;
            case "VISUAL":
                AiIncludeVisualContext = !AiIncludeVisualContext;
                break;
        }

        AiAssistantStatusLabel = TranslateUi("CONTEXT UPDATED");
        AiAssistantStatusBrush = "#6FDBFF";
        RaiseAiConversationStateChanged();
    }

    private void GenerateAiPresetPrompt()
    {
        AiAssistantDraftPrompt = BuildAiPresetPromptText();
        AiAssistantStatusLabel = TranslateUi("PROMPT BUILT");
        AiAssistantStatusBrush = "#21E6C1";
        RaiseAiConversationStateChanged();
    }

    private void LoadAiSavedPromptPreset(AiPromptPresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        AiPromptPresetAsset = preset.Asset;
        SelectedAiPromptTradeStyle = preset.TradeStyle;
        SelectedAiPromptHorizon = preset.Horizon;
        SelectedAiPromptRiskProfile = preset.RiskProfile;
        SelectedAiPromptFocus = preset.Focus;
        AiAssistantStatusLabel = TranslateUi("PRESET LOADED");
        AiAssistantStatusBrush = "#6FDBFF";
        RaiseAiConversationStateChanged();
    }

    private void BuildAiSavedPromptPreset(AiPromptPresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        LoadAiSavedPromptPreset(preset);
        GenerateAiPresetPrompt();
    }

    private void SendAiSavedPromptPreset(AiPromptPresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        LoadAiSavedPromptPreset(preset);
        GenerateAiPresetPrompt();
        SendAiAssistantPrompt();
        AiAssistantStatusLabel = TranslateUi("PRESET SENT");
        AiAssistantStatusBrush = "#21E6C1";
        RaiseAiConversationStateChanged();
    }

    private string BuildAiPresetPromptText()
    {
        var asset = AiPromptPresetResolvedAsset;
        var style = SelectedAiPromptTradeStyle?.ToLowerInvariant() ?? "";
        var horizon = SelectedAiPromptHorizon?.ToLowerInvariant() ?? "";
        var focus = SelectedAiPromptFocus?.ToLowerInvariant() ?? "";
        var risk = SelectedAiPromptRiskProfile?.ToLowerInvariant() ?? "";

        if (_localization.IsRussian)
        {
            return $"Собери {style} план по {asset} на горизонт {horizon}. " +
                   $"Фокус: {focus}. Профиль риска: {risk}. " +
                   $"Дай конкретно: стоит ли входить сейчас, где лучший вход, где инвалидация, где частичный и полный выход, и какой главный риск делает идею слабой.";
        }

        return $"Build a {style} plan for {asset} on a {horizon} horizon. " +
               $"Focus on {focus} with a {risk} risk profile. " +
               $"Tell me whether entering now makes sense, where the best entry is, where invalidation sits, where partial and full exits belong, and what risk weakens the idea most.";
    }

    private void RaiseAiContextSelectionStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsAiContextAutoMode));
        this.RaisePropertyChanged(nameof(IsAiContextCexMode));
        this.RaisePropertyChanged(nameof(IsAiContextDexMode));
        this.RaisePropertyChanged(nameof(IsAiEffectiveDexContext));
        this.RaisePropertyChanged(nameof(AiContextAutoBackground));
        this.RaisePropertyChanged(nameof(AiContextCexBackground));
        this.RaisePropertyChanged(nameof(AiContextDexBackground));
        this.RaisePropertyChanged(nameof(AiContextAutoForeground));
        this.RaisePropertyChanged(nameof(AiContextCexForeground));
        this.RaisePropertyChanged(nameof(AiContextDexForeground));
        this.RaisePropertyChanged(nameof(AiContextVenueLabel));
        this.RaisePropertyChanged(nameof(AiContextVenueSummary));
        this.RaisePropertyChanged(nameof(EffectiveAiContextVenueLabel));
        this.RaisePropertyChanged(nameof(EffectiveAiContextTitle));
        this.RaisePropertyChanged(nameof(EffectiveAiContextSymbol));
        this.RaisePropertyChanged(nameof(AiAssistantScopeLabel));
        this.RaisePropertyChanged(nameof(AiAssistantModeSummary));
        this.RaisePropertyChanged(nameof(AiWorkspaceHeadline));
        this.RaisePropertyChanged(nameof(AiWorkspaceSubheadline));
    }

    private IReadOnlyList<AiVisualCardViewModel> BuildAiVisualCards()
    {
        return
        [
            new AiVisualCardViewModel(
                "market",
                TranslateUi("Market Structure"),
                _localization.IsRussian
                    ? $"Снимок тренда для {SelectedTradingSymbol}. Используйте его, чтобы понимать смещение, спред и место, где импульс может сломаться."
                    : $"Trend snapshot for {SelectedTradingSymbol}. Use it to frame bias, spread, and where momentum can fail.",
                $"{AiOutlookLabel} · RR {AiRiskRewardLabel}",
                LoadAiStudioBitmap("market-structure.png")),
            new AiVisualCardViewModel(
                "risk",
                TranslateUi("Risk Map"),
                TranslateUi("Read open exposure, daily loss budget, and whether the active ticket is oversized for the current guardrail."),
                WalletVM.GlobalRiskCapLabel,
                LoadAiStudioBitmap("risk-map.png")),
            new AiVisualCardViewModel(
                "dex",
                TranslateUi("DEX Route Flow"),
                _localization.IsRussian
                    ? $"Показывает путь от кошелька через котируемый актив к токену {DexTradingVM.SelectedTokenTitle}. Полезно для проверки блокеров маршрута и режима котировки."
                    : $"Shows wallet-to-quote-to-token flow for {DexTradingVM.SelectedTokenTitle}. Useful when checking route blockers and quote mode.",
                DexTradingVM.DexGuardSummary,
                LoadAiStudioBitmap("dex-flow.png")),
            new AiVisualCardViewModel(
                "sniper",
                TranslateUi("Sniper Heat"),
                TranslateUi("Visual scan mode for new-pair flow. Track readiness, slot pressure, and whether the session should stay selective."),
                _localization.IsRussian
                    ? $"Открыто {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions} · Результат {SniperVM.NetClosedPnlLabel}"
                    : $"Open {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions} · Net {SniperVM.NetClosedPnlLabel}",
                LoadAiStudioBitmap("sniper-heat.png"))
        ];
    }

    private static Bitmap LoadAiStudioBitmap(string fileName)
    {
        if (AiStudioBitmapCache.TryGetValue(fileName, out var cached))
        {
            return cached;
        }

        var bitmap = new Bitmap(AssetLoader.Open(new Uri($"avares://CryptoAITerminal.TerminalUI/Assets/AiStudio/{fileName}")));
        AiStudioBitmapCache[fileName] = bitmap;
        return bitmap;
    }

    private void ClearAiAssistantConversation()
    {
        AiAssistantMessages.Clear();
        AddAiAssistantMessage(false, TranslateUi("Desk Copilot"), TranslateUi("I read the live terminal state and answer from your local market, risk, DEX, and sniper context. Ask for setup, invalidation, route, or visual breakdowns."), TranslateUi("Local runtime context"));
        AiAssistantDraftPrompt = string.Empty;
        AiAssistantStatusLabel = TranslateUi("SESSION RESET");
        AiAssistantStatusBrush = "#8FA3B8";
        RaiseAiConversationStateChanged();
    }

    private void InjectAiMarketContext()
    {
        AddAiAssistantMessage(false, TranslateUi("Market Sync"), BuildAiAssistantResponse(TranslateUi("context refresh")), $"{TranslateUi("Synced")} {DateTime.Now:HH:mm:ss}");
        AiAssistantStatusLabel = TranslateUi("MARKET CONTEXT SYNCED");
        AiAssistantStatusBrush = "#21E6C1";
        RaiseAiConversationStateChanged();
    }

    private void UseAiAssistantQuickPrompt(AiAssistantQuickPromptViewModel prompt)
    {
        if (prompt is null)
        {
            return;
        }

        AiAssistantDraftPrompt = prompt.Prompt;
        SendAiAssistantPrompt();
    }

    private void ExplainAiKnowledgeTopic(AiKnowledgeTopicViewModel topic)
    {
        if (topic is null)
        {
            return;
        }

        AddAiAssistantMessage(true, TranslateUi("You"), topic.Question, $"{DateTime.Now:HH:mm:ss} · {TranslateUi("knowledge topic")}");
        AddAiAssistantMessage(false, $"{TranslateUi("Desk Copilot")} · {topic.Category}", topic.Answer, $"{DateTime.Now:HH:mm:ss} · {TranslateUi("offline knowledge base")}");
        AiAssistantStatusLabel = $"{TranslateUi("TOPIC")}: {topic.Label.ToUpperInvariant()}";
        AiAssistantStatusBrush = "#6FDBFF";
        RaiseAiConversationStateChanged();
    }

    private void SelectAiVisual(AiVisualCardViewModel visual)
    {
        if (visual is null)
        {
            return;
        }

        SelectedAiVisual = visual;
        AiAssistantStatusLabel = $"{TranslateUi("VISUAL")}: {visual.Title.ToUpperInvariant()}";
        AiAssistantStatusBrush = "#6FDBFF";
    }

    private void AnalyzeSelectedAiVisual()
    {
        if (SelectedAiVisual is null)
        {
            return;
        }

        AddAiAssistantMessage(
            false,
            TranslateUi("Visual Analysis"),
            BuildAiVisualAnalysisNarrative(SelectedAiVisual),
            $"{DateTime.Now:HH:mm:ss} · {TranslateUi("selected visual")}");
        AiAssistantStatusLabel = TranslateUi("VISUAL ANALYSIS READY");
        AiAssistantStatusBrush = "#6FDBFF";
        RaiseAiConversationStateChanged();
    }

    private void BuildAiTradePlan()
    {
        var plan = new StringBuilder();
        plan.AppendLine(_localization.IsRussian ? $"План по {EffectiveAiContextSymbol}" : $"{EffectiveAiContextSymbol} trade plan");
        plan.AppendLine();
        if (EffectiveAiContextVenue == TradingVenueMode.Dex)
        {
            plan.AppendLine(_localization.IsRussian
                ? $"DEX-контекст: {DexTradingVM.SelectedTokenTitle} | Котировка: {DexTradingVM.GlobalQuoteSummary}"
                : $"DEX context: {DexTradingVM.SelectedTokenTitle} | Quote: {DexTradingVM.GlobalQuoteSummary}");
            plan.AppendLine(_localization.IsRussian
                ? $"Маршрут токена: {DexTradingVM.DexGuardSummary}"
                : $"Token route: {DexTradingVM.DexGuardSummary}");
            plan.AppendLine(_localization.IsRussian
                ? $"Статус графика: {DexTradingVM.ChartStatusMessage}"
                : $"Chart status: {DexTradingVM.ChartStatusMessage}");
        }
        else
        {
            plan.AppendLine(_localization.IsRussian ? $"Смещение: {AiOutlookLabel} | Направление: {SelectedOrderSide} | Уверенность: {AiConfidenceLabel}" : $"Bias: {AiOutlookLabel} | Direction: {SelectedOrderSide} | Confidence: {AiConfidenceLabel}");
            plan.AppendLine(_localization.IsRussian ? $"Зона входа: {AiEntryRangeLabel}" : $"Entry band: {AiEntryRangeLabel}");
            plan.AppendLine(_localization.IsRussian ? $"Инвалидация: {AiStopLossDisplay}" : $"Invalidation: {AiStopLossDisplay}");
            plan.AppendLine(_localization.IsRussian ? $"Цель 1: {AiTakeProfitOneDisplay} | Цель 2: {AiTakeProfitTwoDisplay}" : $"Target 1: {AiTakeProfitOneDisplay} | Target 2: {AiTakeProfitTwoDisplay}");
        }
        if (AiIncludeRiskContext)
        {
            plan.AppendLine(_localization.IsRussian ? $"Риск/доходность: {AiRiskRewardLabel}" : $"Risk/reward: {AiRiskRewardLabel}");
        }
        plan.AppendLine(_localization.IsRussian ? $"Режим исполнения: {WalletVM.GlobalExecutionModeLabel}" : $"Execution mode: {WalletVM.GlobalExecutionModeLabel}");
        plan.AppendLine(_localization.IsRussian ? $"Маршрут: {WalletVM.RouteReadinessSummary}" : $"Route: {WalletVM.RouteReadinessSummary}");
        if (AiIncludeDexContext)
        {
            plan.AppendLine(_localization.IsRussian ? $"DEX заметка: {DexTradingVM.DexGuardSummary}" : $"DEX note: {DexTradingVM.DexGuardSummary}");
        }
        if (AiIncludeSniperContext)
        {
            plan.AppendLine(_localization.IsRussian ? $"Sniper заметка: {SniperVM.PresetSummary} | Открыто {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions}" : $"Sniper note: {SniperVM.PresetSummary} | Open {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions}");
        }
        plan.AppendLine(_localization.IsRussian ? $"Главный блокер: {(CanPlacePrimaryOrder ? "нет" : PrimaryOrderBlockedReason)}" : $"Primary blocker: {(CanPlacePrimaryOrder ? "none" : PrimaryOrderBlockedReason)}");

        AddAiAssistantMessage(false, TranslateUi("Trade Plan"), plan.ToString().Trim(), $"{DateTime.Now:HH:mm:ss} · {TranslateUi("structured plan")}");
        AiAssistantStatusLabel = TranslateUi("TRADE PLAN READY");
        AiAssistantStatusBrush = "#21E6C1";
        RaiseAiConversationStateChanged();
    }

    private void SendAiAssistantPrompt()
    {
        var prompt = AiAssistantDraftPrompt?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        AddAiAssistantMessage(true, TranslateUi("You"), prompt, $"{DateTime.Now:HH:mm:ss} · {TranslateUi("desk query")}");
        var response = BuildAiAssistantResponse(prompt);
        AddAiAssistantMessage(false, TranslateUi("Desk Copilot"), response, $"{DateTime.Now:HH:mm:ss} · {TranslateUi("local crypto context")}");
        AiAssistantDraftPrompt = string.Empty;
        AiAssistantStatusLabel = TranslateUi("ANSWER READY");
        AiAssistantStatusBrush = "#21E6C1";
        RaiseAiConversationStateChanged();
    }

    private string BuildAiVisualAnalysisNarrative(AiVisualCardViewModel visual)
    {
        var body = new StringBuilder();
        body.AppendLine($"{visual.Title}: {visual.Caption}");
        body.AppendLine();
        body.AppendLine(_localization.IsRussian ? $"Закреплённая метрика: {visual.Metric}" : $"Pinned metric: {visual.Metric}");

        if (string.Equals(visual.Key, "market", StringComparison.OrdinalIgnoreCase))
        {
            body.AppendLine(_localization.IsRussian
                ? $"Считайте этот рыночный визуал картой смещения для {EffectiveAiContextSymbol}. Текущий взгляд: {AiOutlookLabel}, спред: {SpreadPercentLabel}, торговая идея: '{TradeIdeaTitle}'."
                : $"Read the market image as a bias board for {EffectiveAiContextSymbol}. Current outlook is {AiOutlookLabel}, spread is {SpreadPercentLabel}, and the trade idea is '{TradeIdeaTitle}'.");
        }
        else if (string.Equals(visual.Key, "risk", StringComparison.OrdinalIgnoreCase))
        {
            body.AppendLine(_localization.IsRussian
                ? $"Этот визуал лучше использовать как проверку защитных ограничений. Глобальный лимит: {WalletVM.GlobalRiskCapLabel}, сводка терминала: {WalletVM.GlobalRiskSummary}."
                : $"This image is best used as a guardrail check. Global cap is {WalletVM.GlobalRiskCapLabel} and the desk summary is {WalletVM.GlobalRiskSummary}.");
        }
        else if (string.Equals(visual.Key, "dex", StringComparison.OrdinalIgnoreCase))
        {
            body.AppendLine(_localization.IsRussian
                ? $"Этот маршрутный визуал отражает активный DEX-путь для {DexTradingVM.SelectedTokenTitle}. Текущая сводка маршрута: {DexTradingVM.DexGuardSummary}."
                : $"This route image mirrors the active DEX path for {DexTradingVM.SelectedTokenTitle}. Current route summary: {DexTradingVM.DexGuardSummary}.");
        }
        else if (string.Equals(visual.Key, "sniper", StringComparison.OrdinalIgnoreCase))
        {
            body.AppendLine(_localization.IsRussian
                ? $"Этот визуал показывает интенсивность потока новых пар. Текущее состояние sniper-модуля: {SniperVM.PresetSummary}, открытые слоты {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions}, результат {SniperVM.NetClosedPnlLabel}."
                : $"This visual frames new-pair intensity. Current sniper state: {SniperVM.PresetSummary}, open slots {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions}, net {SniperVM.NetClosedPnlLabel}.");
        }

        body.AppendLine();
        body.AppendLine(CanPlacePrimaryOrder
            ? (_localization.IsRussian ? "Исполнение сейчас достаточно чистое, чтобы действовать при подтверждении входа." : "Execution is currently clear enough to act if the entry confirms.")
            : (_localization.IsRussian ? $"Исполнение всё ещё заблокировано: {PrimaryOrderBlockedReason}" : $"Execution is still gated: {PrimaryOrderBlockedReason}"));

        return body.ToString().Trim();
    }

    private string BuildAiAssistantResponse(string prompt)
    {
        var text = prompt.Trim().ToLowerInvariant();
        var lines = new List<string>();
        var wantsRisk = text.Contains("risk") || text.Contains("stop") || text.Contains("loss") || text.Contains("sl");
        var wantsDex = text.Contains("dex") || text.Contains("route") || text.Contains("swap") || text.Contains("quote");
        var wantsSniper = text.Contains("sniper") || text.Contains("pair") || text.Contains("meme") || text.Contains("launch");
        var wantsVisual = text.Contains("visual") || text.Contains("photo") || text.Contains("image") || text.Contains("chart");
        var wantsNext = text.Contains("next") || text.Contains("action") || text.Contains("do now") || text.Contains("что делать");
        var wantsConcept = text.Contains("what is") || text.Contains("что такое") || text.Contains("how does") || text.Contains("как работает");

        var knowledgeTopic = ResolveAiKnowledgeTopic(text);
        if (knowledgeTopic is not null && wantsConcept)
        {
            lines.Add($"{knowledgeTopic.Label}: {knowledgeTopic.Answer}");
        }

        if (AiIncludeMarketContext)
        {
            if (EffectiveAiContextVenue == TradingVenueMode.Dex)
            {
                lines.Add(_localization.IsRussian
                    ? $"{EffectiveAiContextSymbol}: {DexTradingVM.ChartStatusMessage}"
                    : $"{EffectiveAiContextSymbol}: {DexTradingVM.ChartStatusMessage}");
                lines.Add(_localization.IsRussian
                    ? $"DEX-контекст: {DexTradingVM.SelectedTokenTitle} | Котируемый актив {WalletVM.GlobalQuoteAssetSymbol} | Маршрут {DexTradingVM.DexGuardSummary}."
                    : $"DEX context: {DexTradingVM.SelectedTokenTitle} | Quote asset {WalletVM.GlobalQuoteAssetSymbol} | Route {DexTradingVM.DexGuardSummary}.");
            }
            else
            {
                lines.Add(_localization.IsRussian
                    ? $"{EffectiveAiContextSymbol}: {TradeIdeaTitle}. {TradeIdeaSummary}"
                    : $"{EffectiveAiContextSymbol}: {TradeIdeaTitle}. {TradeIdeaSummary}");
                lines.Add(_localization.IsRussian
                    ? $"Вход {AiEntryRangeLabel} | Стоп {AiStopLossDisplay} | TP1 {AiTakeProfitOneDisplay} | RR {AiRiskRewardLabel}."
                    : $"Entry {AiEntryRangeLabel} | Stop {AiStopLossDisplay} | TP1 {AiTakeProfitOneDisplay} | RR {AiRiskRewardLabel}.");
            }
        }

        if (wantsRisk && AiIncludeRiskContext)
        {
            lines.Add(_localization.IsRussian
                ? $"Риск-ограничение: {WalletVM.GlobalRiskCapLabel}. {WalletVM.GlobalRiskSummary}. Нагрузка заявки: {PortfolioExposureLabel}."
                : $"Risk gate: {WalletVM.GlobalRiskCapLabel}. {WalletVM.GlobalRiskSummary}. Ticket load {PortfolioExposureLabel}.");
            lines.Add(TradeNotional <= 0m
                ? (_localization.IsRussian ? "Живая заявка ещё не взведена, поэтому риск пока теоретический, пока не зафиксированы размер и цена." : "No live ticket is armed yet, so risk is still theoretical until size and price are locked in.")
                : (_localization.IsRussian ? $"Текущий номинал заявки: {TradeNotional:N2} USDT, ожидаемые комиссии: {EstimatedTradingFeeLabel}, сеть: {EstimatedNetworkFeeLabel}." : $"Current ticket notional is {TradeNotional:N2} USDT with est. fees {EstimatedTradingFeeLabel} and network {EstimatedNetworkFeeLabel}."));
        }

        if (wantsDex && AiIncludeDexContext)
        {
            lines.Add(_localization.IsRussian
                ? $"DEX-маршрут: {DexTradingVM.DexGuardSummary}. {WalletVM.RouteReadinessSummary}"
                : $"DEX route: {DexTradingVM.DexGuardSummary}. {WalletVM.RouteReadinessSummary}");
            lines.Add(_localization.IsRussian
                ? $"Выбранный токен: {DexTradingVM.SelectedTokenTitle}. Статус графика: {DexTradingVM.ChartStatusMessage}"
                : $"Selected token: {DexTradingVM.SelectedTokenTitle}. Chart status: {DexTradingVM.ChartStatusMessage}");
        }

        if (wantsSniper && AiIncludeSniperContext)
        {
            lines.Add(_localization.IsRussian
                ? $"Sniper: {SniperVM.PresetSummary}. Открыто слотов {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions}, винрейт {SniperVM.CombinedWinRateLabel}, результат {SniperVM.NetClosedPnlLabel}."
                : $"Sniper: {SniperVM.PresetSummary}. Open {SniperVM.OpenPositionCount}/{SniperVM.MaxSimultaneousPositions} slots, win rate {SniperVM.CombinedWinRateLabel}, net {SniperVM.NetClosedPnlLabel}.");
            lines.Add(_localization.IsRussian ? $"Комментарий по потоку: {SniperVM.StatusMessage}" : $"Flow note: {SniperVM.StatusMessage}");
        }

        if (wantsVisual && AiIncludeVisualContext)
        {
            lines.Add(_localization.IsRussian
                ? $"Визуал '{AiSelectedVisualTitle}': {AiSelectedVisualCaption}"
                : $"Visual '{AiSelectedVisualTitle}': {AiSelectedVisualCaption}");
            lines.Add(_localization.IsRussian ? $"Закреплённая метрика: {AiSelectedVisualMetric}" : $"Pinned metric: {AiSelectedVisualMetric}");
        }

        if (wantsNext || (!wantsRisk && !wantsDex && !wantsSniper && !wantsVisual))
        {
            var nextAction = CanPlacePrimaryOrder
                ? (_localization.IsRussian ? $"Чистое следующее действие: сохраняйте смещение {SelectedOrderSide}, следите за зоной входа и входите только если лента удерживается внутри запланированной зоны." : $"Clean next step: keep {SelectedOrderSide} bias, watch the entry band, and only trigger once the tape stays inside the planned zone.")
                : (_localization.IsRussian ? $"Чистое следующее действие: сначала уберите блокер. Сейчас терминал останавливает исполнение по причине: {PrimaryOrderBlockedReason}" : $"Clean next step: remove the blocker first. Right now the terminal is stopping execution because: {PrimaryOrderBlockedReason}");
            lines.Add(nextAction);
            lines.Add(_localization.IsRussian
                ? $"Режим исполнения: {WalletVM.GlobalExecutionModeLabel}. {WalletVM.GlobalExecutionSummary}"
                : $"Execution mode is {WalletVM.GlobalExecutionModeLabel}. {WalletVM.GlobalExecutionSummary}");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, lines.Distinct());
    }

    private AiKnowledgeTopicViewModel? ResolveAiKnowledgeTopic(string normalizedPrompt)
    {
        if (normalizedPrompt.Contains("slippage") || normalizedPrompt.Contains("проскаль"))
        {
            return AiAssistantKnowledgeTopics.FirstOrDefault(topic =>
                string.Equals(topic.Label, "Slippage", StringComparison.OrdinalIgnoreCase) ||
                topic.Label.Contains("Проскаль", StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedPrompt.Contains("limit") || normalizedPrompt.Contains("market order") || normalizedPrompt.Contains("рыноч"))
        {
            return AiAssistantKnowledgeTopics.FirstOrDefault(topic =>
                string.Equals(topic.Label, "Limit vs Market", StringComparison.OrdinalIgnoreCase) ||
                topic.Label.Contains("Лимит", StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedPrompt.Contains("stop") || normalizedPrompt.Contains("stop loss") || normalizedPrompt.Contains("стоп"))
        {
            return AiAssistantKnowledgeTopics.FirstOrDefault(topic =>
                string.Equals(topic.Label, "Stop Loss", StringComparison.OrdinalIgnoreCase) ||
                topic.Label.Contains("Стоп", StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedPrompt.Contains("dex safety") || normalizedPrompt.Contains("rug") || normalizedPrompt.Contains("honeypot") || normalizedPrompt.Contains("liq"))
        {
            return AiAssistantKnowledgeTopics.FirstOrDefault(topic =>
                string.Equals(topic.Label, "DEX Safety", StringComparison.OrdinalIgnoreCase) ||
                topic.Label.Contains("Безопас", StringComparison.OrdinalIgnoreCase));
        }

        if (normalizedPrompt.Contains("new pair") || normalizedPrompt.Contains("fresh pair") || normalizedPrompt.Contains("sniper"))
        {
            return AiAssistantKnowledgeTopics.FirstOrDefault(topic =>
                string.Equals(topic.Label, "New Pair Filter", StringComparison.OrdinalIgnoreCase) ||
                topic.Label.Contains("новых пар", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private void AddAiAssistantMessage(bool isUser, string title, string body, string meta)
    {
        AiAssistantMessages.Add(new AiAssistantMessageViewModel(isUser, title, body, meta));
        while (AiAssistantMessages.Count > 18)
        {
            AiAssistantMessages.RemoveAt(0);
        }
    }

    private void RaiseAiConversationStateChanged()
    {
        this.RaisePropertyChanged(nameof(CanSendAiAssistantPrompt));
        this.RaisePropertyChanged(nameof(AiConversationCountLabel));
    }

    private string TranslateUi(string text) => _localization.Translate(text);

    private void OnLocalizationLanguageChanged(object? sender, EventArgs e)
    {
        LoadAiCustomPromptPresetsFromDisk();
        InitializeAiSignalStudio();
        RaiseAiContextSelectionStateChanged();
        RaiseAiConversationStateChanged();
        this.RaisePropertyChanged(nameof(AiWorkspaceSubheadline));
        this.RaisePropertyChanged(nameof(AiAssistantComposerPlaceholder));
        this.RaisePropertyChanged(nameof(AiEngineAvailabilityLabel));
        this.RaisePropertyChanged(nameof(AiEngineAvailabilitySummary));
        this.RaisePropertyChanged(nameof(AiAttachmentStatusLabel));
        this.RaisePropertyChanged(nameof(AiAttachmentStatusSummary));
        this.RaisePropertyChanged(nameof(AiKnowledgeSummary));
        this.RaisePropertyChanged(nameof(AiSelectedVisualTitle));
        this.RaisePropertyChanged(nameof(AiSelectedVisualCaption));
        this.RaisePropertyChanged(nameof(AiConversationCountLabel));
        this.RaisePropertyChanged(nameof(AiVisualCountLabel));
        this.RaisePropertyChanged(nameof(AiQuickPromptCountLabel));
        this.RaisePropertyChanged(nameof(AiKnowledgeCountLabel));
        this.RaisePropertyChanged(nameof(AiContextSourcesSummary));
        this.RaisePropertyChanged(nameof(AiCustomPresetCountLabel));
    }

    private void RebuildTradingLadder()
    {
        LadderLevels.Clear();
        if (SelectedMarket is null)
        {
            return;
        }

        var bestBid = SelectedMarket.BidLevels.FirstOrDefault()?.Price ?? 0m;
        var bestAsk = SelectedMarket.AskLevels.FirstOrDefault()?.Price ?? 0m;
        var maxBidQuantity = Math.Max(1m, SelectedMarket.BidLevels.DefaultIfEmpty().Max(level => level?.Quantity ?? 0m));
        var maxAskQuantity = Math.Max(1m, SelectedMarket.AskLevels.DefaultIfEmpty().Max(level => level?.Quantity ?? 0m));
        var currentPrice = CurrentTradePrice > 0 ? CurrentTradePrice : (bestBid > 0 ? bestBid : bestAsk);
        var entryPrice = AverageEntryPrice;
        var priceStep = DetermineLadderStep(SelectedMarket.BidLevels.Select(level => level.Price)
            .Concat(SelectedMarket.AskLevels.Select(level => level.Price))
            .OrderByDescending(price => price)
            .ToList(), currentPrice);
        var centerPrice = RoundToStep(currentPrice > 0 ? currentPrice : (bestBid > 0 ? bestBid : bestAsk), priceStep)
            + (_ladderManualOffsetTicks * priceStep);
        const int visibleRows = 31;
        var halfRows = visibleRows / 2;
        var bidLookup = SelectedMarket.BidLevels
            .GroupBy(level => RoundToStep(level.Price, priceStep))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        var askLookup = SelectedMarket.AskLevels
            .GroupBy(level => RoundToStep(level.Price, priceStep))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

        var rows = Enumerable.Range(0, visibleRows)
            .Select(index =>
            {
                var rowPrice = centerPrice + ((halfRows - index) * priceStep);
                bidLookup.TryGetValue(rowPrice, out var bidQuantity);
                askLookup.TryGetValue(rowPrice, out var askQuantity);
                return new TradeLadderLevelViewModel(
                    rowPrice,
                    bidQuantity,
                    askQuantity,
                    rowPrice == RoundToStep(bestBid, priceStep),
                    rowPrice == RoundToStep(bestAsk, priceStep),
                    maxBidQuantity,
                    maxAskQuantity,
                    currentPrice,
                    entryPrice);
            })
            .ToList();

        foreach (var row in rows)
        {
            LadderLevels.Add(row);
        }
    }

    private static decimal DetermineLadderStep(IReadOnlyList<decimal> prices, decimal referencePrice)
    {
        var positiveDiff = prices
            .Zip(prices.Skip(1), (left, right) => Math.Abs(left - right))
            .Where(diff => diff > 0)
            .DefaultIfEmpty(referencePrice >= 1000m ? 2m : referencePrice >= 100m ? 0.1m : 0.01m)
            .Min();

        if (positiveDiff >= 1m)
        {
            return Math.Max(1m, Math.Round(positiveDiff, 0));
        }

        if (positiveDiff >= 0.1m)
        {
            return Math.Round(positiveDiff, 1);
        }

        if (positiveDiff >= 0.01m)
        {
            return Math.Round(positiveDiff, 2);
        }

        return Math.Max(0.0001m, Math.Round(positiveDiff, 4));
    }

    private static decimal RoundToStep(decimal price, decimal step)
    {
        if (step <= 0)
        {
            return price;
        }

        return Math.Round(price / step, MidpointRounding.AwayFromZero) * step;
    }

    private void SeedTicketDefaults()
    {
        if (CurrentTradePrice <= 0 || LimitPrice > 0 || TakeProfitPrice > 0 || StopLossPrice > 0)
        {
            return;
        }

        LimitPrice = CurrentTradePrice;
        TakeProfitPrice = CurrentTradePrice * 1.012m;
        StopLossPrice = CurrentTradePrice * 0.992m;
    }

    private void RemoveProtectionOrders()
    {
        var staleOrders = WorkingOrders.Where(order => order.Kind is WorkingOrderKind.TakeProfit or WorkingOrderKind.StopLoss).ToList();
        foreach (var order in staleOrders)
        {
            WorkingOrders.Remove(order);
        }

        if (staleOrders.Count > 0)
        {
            RaiseWorkingOrdersCollectionChanged();
        }
    }

    private void AddFill(OrderSide side, decimal price, decimal quantity, string status)
    {
        RecentFills.Insert(0, new TradeFillViewModel(SelectedTradingSymbol, side, price, quantity, status));
        while (RecentFills.Count > 24)
        {
            RecentFills.RemoveAt(RecentFills.Count - 1);
        }

        this.RaisePropertyChanged(nameof(RecentFillsCountLabel));
        this.RaisePropertyChanged(nameof(HasRecentFills));
        this.RaisePropertyChanged(nameof(ShowRecentFillsPlaceholder));
        this.RaisePropertyChanged(nameof(AnalyticsExecutionSummary));
    }

    private void RefreshPositionRows()
    {
        PositionRows.Clear();
        if (PositionQuantity == 0)
        {
            this.RaisePropertyChanged(nameof(PositionsCountLabel));
            this.RaisePropertyChanged(nameof(HasPositionRows));
            this.RaisePropertyChanged(nameof(ShowPositionRowsPlaceholder));
            this.RaisePropertyChanged(nameof(AnalyticsExecutionSummary));
            return;
        }

        PositionRows.Add(new PositionRowViewModel(
            SelectedTradingSymbol,
            PositionQuantity,
            AverageEntryPrice,
            CurrentTradePrice,
            UnrealizedPnl,
            RealizedPnl));
        this.RaisePropertyChanged(nameof(PositionsCountLabel));
        this.RaisePropertyChanged(nameof(HasPositionRows));
        this.RaisePropertyChanged(nameof(ShowPositionRowsPlaceholder));
        this.RaisePropertyChanged(nameof(AnalyticsExecutionSummary));
    }

    private void RefreshSignalRows()
    {
        SignalRows.Clear();
        if (SelectedMarket is null || CurrentTradePrice <= 0)
        {
            this.RaisePropertyChanged(nameof(SignalsCountLabel));
            this.RaisePropertyChanged(nameof(HasSignalRows));
            this.RaisePropertyChanged(nameof(ShowSignalRowsPlaceholder));
            this.RaisePropertyChanged(nameof(AnalyticsExecutionSummary));
            return;
        }

        SignalRows.Add(new SignalRowViewModel(
            SelectedTradingSymbol,
            TradeIdeaTitle,
            SuggestedEntryLabel,
            SuggestedTargetLabel,
            SuggestedStopLabel,
            SelectedMarket.IsPositiveTrend ? "ACTIVE" : "WAIT"));

        this.RaisePropertyChanged(nameof(SignalsCountLabel));
        this.RaisePropertyChanged(nameof(HasSignalRows));
        this.RaisePropertyChanged(nameof(ShowSignalRowsPlaceholder));
        this.RaisePropertyChanged(nameof(AnalyticsExecutionSummary));
    }

    private void RaiseWorkingOrdersCollectionChanged()
    {
        this.RaisePropertyChanged(nameof(WorkingOrdersCountLabel));
        this.RaisePropertyChanged(nameof(HasWorkingOrders));
        this.RaisePropertyChanged(nameof(ShowWorkingOrdersPlaceholder));
        this.RaisePropertyChanged(nameof(AnalyticsExecutionSummary));
    }

    private void UpdateSelectedPriceHighlights()
    {
        if (SelectedMarket is not null)
        {
            foreach (var level in SelectedMarket.BidLevels)
            {
                level.IsSelected = _selectedLadderPrice > 0 && level.Price == _selectedLadderPrice;
            }

            foreach (var level in SelectedMarket.AskLevels)
            {
                level.IsSelected = _selectedLadderPrice > 0 && level.Price == _selectedLadderPrice;
            }
        }

        foreach (var level in LadderLevels)
        {
            level.IsSelected = _selectedLadderPrice > 0 && level.Price == _selectedLadderPrice;
        }
    }

    /// <summary>
    /// Called when the user clicks Snipe on a whale alert.
    /// Navigates to the Sniper section so the operator can place the order.
    /// </summary>
    private void NavigateToSniperSymbol(string symbol)
    {
        SelectMainTab("sniper");
        AddLog($"[WHALE SNIPE] Navigated to Sniper for {symbol}");
    }

    private WebApiSnapshotWriter.SnapshotPayload BuildWebApiSnapshot(PnlDashboardService pnlService)
    {
        var payload = new WebApiSnapshotWriter.SnapshotPayload();

        try
        {
            // Open futures positions across all gateways
            if (AllPositionsVM?.Rows is { } rows)
            {
                foreach (var r in rows)
                {
                    payload.Positions.Add(new WebApiSnapshotWriter.PositionDto
                    {
                        Source        = r.Exchange,
                        Symbol        = r.Symbol,
                        Side          = r.Side,
                        Quantity      = r.Size,
                        EntryPrice    = r.EntryPrice,
                        MarkPrice     = r.MarkPrice,
                        UnrealizedPnl = r.UnrealizedPnl,
                        LeverageOrOne = Math.Max(1m, r.Leverage),
                    });
                }
            }

            // Sniper candidates: очередь готовых пар + открытые позиции
            foreach (var c in SniperVM.AcceptedPairs)
                payload.Candidates.Add(WebApiSnapshotWriter.FromCandidate(c));
            foreach (var c in SniperVM.OpenPositions)
                payload.Candidates.Add(WebApiSnapshotWriter.FromCandidate(c));

            // PnL summary — суммарно по всем источникам.
            var all = pnlService.GetAll();
            var metrics = pnlService.ComputeMetrics(all);
            var todayCount = 0;
            var today = DateTime.UtcNow.Date;
            foreach (var t in all) if (t.ClosedAtUtc.Date == today) todayCount++;

            payload.Pnl.RealizedPnlUsd   = metrics.TotalPnlUsd;
            payload.Pnl.UnrealizedPnlUsd = payload.Positions.Sum(p => p.UnrealizedPnl);
            payload.Pnl.TradesToday      = todayCount;
            payload.Pnl.OpenPositions    = payload.Positions.Count + SniperVM.OpenPositions.Count;
            payload.Pnl.WinRatePercent   = metrics.WinRate;

            // Balances — читаем из BalanceRefresher (кэш обновляется отдельно раз в 60 сек).
            if (_balanceRefresher is { } refresher)
            {
                foreach (var b in refresher.CurrentBalances)
                {
                    payload.Balances.Add(new WebApiSnapshotWriter.BalanceDto
                    {
                        Exchange = b.Exchange,
                        Market   = b.Market,
                        Asset    = b.Asset,
                        Amount   = b.Amount,
                    });
                }
            }
        }
        catch
        {
            // payload остаётся пустым — writer перезапишет файл на следующем тике.
        }

        return payload;
    }

    public void Dispose()
    {
        _orderBookTimer.Stop();
        _manualModeRefreshCts?.Cancel();
        _manualModeRefreshCts?.Dispose();
        WhaleTrackerVM?.Dispose();
        FundingRateVM?.Dispose();
        GasMonitorVM?.Dispose();
        _gasMonitorService?.Dispose();
        NewsFeedVM?.Dispose();
        _newsFeedService?.Dispose();
        OnChainVM?.Dispose();
        _onChainService?.Dispose();
        _webApiSnapshotWriter?.Dispose();
        _webApiQueueProcessor?.Dispose();
        _balanceRefresher?.Dispose();
        FundingArbitrageVM?.Dispose();
        CrossExchangeArbVM?.Dispose();
        CopyTradingVM?.Dispose();
        StatArbVM?.Dispose();
        BestExecutionVM?.Dispose();
        ScannerVM?.Dispose();
        LiquidationHeatmapVM?.Dispose();
        SentimentVM?.Dispose();
        DexTrendingVM?.Dispose();
        PortfolioRebalanceVM?.Dispose();
        _marketDataSubscription.Dispose();
        _futuresMarketDataSubscription.Dispose();
        _futuresAccountStateSubscription.Dispose();
        _futuresOrderUpdateSubscription.Dispose();
        _futuresTradeUpdateSubscription.Dispose();
        foreach (var subscription in _commandErrorSubscriptions)
        {
            subscription.Dispose();
        }
        foreach (var market in Markets)
        {
            market.PropertyChanged -= OnMarketItemPropertyChanged;
        }
        _localization.LanguageChanged -= OnLocalizationLanguageChanged;
        WalletVM.PropertyChanged -= OnWalletWorkspacePropertyChanged;
        if (AIBotVM != null) AIBotVM.PropertyChanged -= OnAIBotPropertyChanged;
        DexTradingVM.PropertyChanged -= OnDexTradingPropertyChanged;
        if (AIBotVM != null)
        {
            // Дождаться, чтобы TP/SL ордера успели сняться с биржи (макс 5 сек).
            try { AIBotVM.StopBotAsync().Wait(TimeSpan.FromSeconds(5)); }
            catch { /* swallow during shutdown */ }
        }
        AiTraderVM?.ShutdownStop();
        DexTradingVM.Dispose();
        SniperVM.Dispose();
        WalletVM.Dispose();
        _ = _gateway.DisconnectAsync();
        if (_futuresGatewaysMap is not null)
        {
            foreach (var gw in _futuresGatewaysMap.Values.Distinct())
                _ = gw.DisconnectAsync();
        }
        else
        {
            _ = _futuresGateway.DisconnectAsync();
        }
        _orderBookRefreshLock.Dispose();
        _candleRefreshLock.Dispose();
    }

    public enum ChartToolPhase
    {
        None,
        SecondPoint,
        ThirdPoint
    }

    private void OnDexTradingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DexTradingViewModel.SelectedToken)
            or nameof(DexTradingViewModel.SelectedTokenTitle)
            or nameof(DexTradingViewModel.SelectedTokenSubtitle)
            or nameof(DexTradingViewModel.SelectedChartRange)
            or nameof(DexTradingViewModel.ChartCandles)
            or nameof(DexTradingViewModel.ChartStatusMessage)
            or nameof(DexTradingViewModel.BuyAmountBnb)
            or nameof(DexTradingViewModel.SellAmountTokens))
        {
            RaiseTradingStateChanged();
            RaiseTimeframeStateChanged();
        }

        RefreshAiSignalStudioContext();
    }

    // ── Configuration Profile helpers ──────────────────────────────────────────

    private TradingProfile SnapshotCurrentSettings() => new()
    {
        // AI Bot
        BotExchange        = AIBotVM.SelectedExchange,
        BotMarketMode      = AIBotVM.SelectedMarketMode,
        BotSymbol          = AIBotVM.Symbol,
        BotQuantity        = AIBotVM.Quantity,
        BotMaxRiskPerTrade = AIBotVM.MaxRiskPerTrade,
        BotStrategy        = AIBotVM.SelectedStrategy,
        BotMaFastPeriod    = AIBotVM.MaFastPeriod,
        BotMaSlowPeriod    = AIBotVM.MaSlowPeriod,
        BotRsiPeriod       = AIBotVM.RsiPeriod,
        BotRsiOverbought   = AIBotVM.RsiOverbought,
        BotRsiOversold     = AIBotVM.RsiOversold,
        BotBbPeriod        = AIBotVM.BbPeriod,
        BotBbDeviation     = AIBotVM.BbDeviation,
        BotBreakoutPeriod  = AIBotVM.BreakoutPeriod,
        BotMacdFast        = AIBotVM.MacdFast,
        BotMacdSlow        = AIBotVM.MacdSlow,
        BotMacdSignal      = AIBotVM.MacdSignal,
        BotVwapBandPct     = AIBotVM.VwapBandPct,
        BotTpEnabled       = AIBotVM.TpEnabled,
        BotTpPercent       = AIBotVM.TpPercent,
        BotSlEnabled       = AIBotVM.SlEnabled,
        BotSlPercent       = AIBotVM.SlPercent,
        BotTrailingStop    = AIBotVM.TrailingStop,
        BotPartialTp       = AIBotVM.PartialTp,
        BotPartialTpClose  = AIBotVM.PartialTpClosePercent,
        BotPartialTp2Pct   = AIBotVM.PartialTp2Percent,
        BotFuturesLeverage = AIBotVM.FuturesLeverage,
        BotFuturesMargin   = AIBotVM.SelectedFuturesMarginMode,
        // DEX
        DexSlippagePercent = DexTradingVM.SlippagePercent,
        DexBuyAmount       = DexTradingVM.BuyAmountBnb,
        DexQuoteAsset      = DexTradingVM.SelectedQuoteAssetSymbol,
        // Grid
        GridSymbol         = GridBotVM.Symbol,
        GridLower          = GridBotVM.LowerPrice,
        GridUpper          = GridBotVM.UpperPrice,
        GridLevels         = GridBotVM.GridLevels,
        GridQtyPerLevel    = GridBotVM.QuantityPerGrid,
        // DCA
        DcaExchange        = DcaBotVM.SelectedExchange,
    };

    private void ApplyProfile(TradingProfile p)
    {
        // AI Bot
        AIBotVM.SelectedExchange        = p.BotExchange;
        AIBotVM.SelectedMarketMode      = p.BotMarketMode;
        AIBotVM.Symbol                  = p.BotSymbol;
        AIBotVM.Quantity                = p.BotQuantity;
        AIBotVM.MaxRiskPerTrade         = p.BotMaxRiskPerTrade;
        AIBotVM.SelectedStrategy        = p.BotStrategy;
        AIBotVM.MaFastPeriod            = p.BotMaFastPeriod;
        AIBotVM.MaSlowPeriod            = p.BotMaSlowPeriod;
        AIBotVM.RsiPeriod               = p.BotRsiPeriod;
        AIBotVM.RsiOverbought           = p.BotRsiOverbought;
        AIBotVM.RsiOversold             = p.BotRsiOversold;
        AIBotVM.BbPeriod                = p.BotBbPeriod;
        AIBotVM.BbDeviation             = p.BotBbDeviation;
        AIBotVM.BreakoutPeriod          = p.BotBreakoutPeriod;
        AIBotVM.MacdFast                = p.BotMacdFast;
        AIBotVM.MacdSlow                = p.BotMacdSlow;
        AIBotVM.MacdSignal              = p.BotMacdSignal;
        AIBotVM.VwapBandPct             = p.BotVwapBandPct;
        AIBotVM.TpEnabled               = p.BotTpEnabled;
        AIBotVM.TpPercent               = p.BotTpPercent;
        AIBotVM.SlEnabled               = p.BotSlEnabled;
        AIBotVM.SlPercent               = p.BotSlPercent;
        AIBotVM.TrailingStop            = p.BotTrailingStop;
        AIBotVM.PartialTp               = p.BotPartialTp;
        AIBotVM.PartialTpClosePercent   = p.BotPartialTpClose;
        AIBotVM.PartialTp2Percent       = p.BotPartialTp2Pct;
        AIBotVM.FuturesLeverage         = p.BotFuturesLeverage;
        AIBotVM.SelectedFuturesMarginMode = p.BotFuturesMargin;
        // DEX
        DexTradingVM.SlippagePercent          = p.DexSlippagePercent;
        DexTradingVM.BuyAmountBnb             = p.DexBuyAmount;
        if (!string.IsNullOrEmpty(p.DexQuoteAsset))
            DexTradingVM.SelectedQuoteAssetSymbol = p.DexQuoteAsset;
        // Grid
        GridBotVM.Symbol          = p.GridSymbol;
        GridBotVM.LowerPrice      = p.GridLower;
        GridBotVM.UpperPrice      = p.GridUpper;
        GridBotVM.GridLevels      = p.GridLevels;
        GridBotVM.QuantityPerGrid = p.GridQtyPerLevel;
        // DCA
        DcaBotVM.SelectedExchange = p.DcaExchange;
    }
}

public enum TradingVenueMode
{
    Cex,
    Dex
}

public enum AiContextVenueMode
{
    Auto,
    Cex,
    Dex
}

public readonly record struct ShellSectionDefinition(
    int TabIndex,
    bool IsPlaceholder,
    string Title,
    string Description,
    string Roadmap);

public readonly record struct QuickBacktestSnapshot(
    bool IsReady,
    string WindowLabel,
    int TradeCount,
    decimal WinRatePercent,
    decimal NetReturnPercent,
    decimal MaxDrawdownPercent,
    decimal BestTradePercent,
    decimal WorstTradePercent,
    string LastSignal,
    string BiasLabel,
    string Narrative)
{
    public static QuickBacktestSnapshot Empty =>
        new(
            false,
            "Load a trading chart to simulate the rule set.",
            0,
            0m,
            0m,
            0m,
            0m,
            0m,
            "Hold",
            "Insufficient data",
            "The quick backtest uses the currently loaded candle set, so it becomes available after the chart finishes loading.");
}

public sealed class TradeLadderLevelViewModel : ReactiveObject
{
    private bool _isSelected;

    public TradeLadderLevelViewModel(decimal price, decimal bidQuantity, decimal askQuantity, bool isBestBid, bool isBestAsk, decimal maxBidQuantity, decimal maxAskQuantity, decimal currentPrice, decimal entryPrice)
    {
        Price = price;
        BidQuantity = bidQuantity;
        AskQuantity = askQuantity;
        IsBestBid = isBestBid;
        IsBestAsk = isBestAsk;
        MaxBidQuantity = maxBidQuantity;
        MaxAskQuantity = maxAskQuantity;
        CurrentPrice = currentPrice;
        EntryPrice = entryPrice;
    }

    public decimal Price { get; }
    public decimal BidQuantity { get; }
    public decimal AskQuantity { get; }
    public bool IsBestBid { get; }
    public bool IsBestAsk { get; }
    public decimal MaxBidQuantity { get; }
    public decimal MaxAskQuantity { get; }
    public decimal CurrentPrice { get; }
    public decimal EntryPrice { get; }
    public string PriceLabel => Price > 0 ? $"{Price:N2}" : string.Empty;
    public string BidLabel => BidQuantity > 0 ? $"{BidQuantity:N0}" : string.Empty;
    public string AskLabel => AskQuantity > 0 ? $"{AskQuantity:N0}" : string.Empty;
    public string MarkerLabel => IsBestAsk ? "ASK" : IsBestBid ? "BID" : string.Empty;
    public double BidBarOpacity => 0.18d + (Math.Max(0d, Math.Min(1d, (double)(BidQuantity / MaxBidQuantity))) * 0.82d);
    public double AskBarOpacity => 0.18d + (Math.Max(0d, Math.Min(1d, (double)(AskQuantity / MaxAskQuantity))) * 0.82d);
    public bool IsCurrentPriceLevel => CurrentPrice > 0 && Math.Abs(Price - CurrentPrice) < 0.005m;
    public bool IsEntryPriceLevel => EntryPrice > 0 && Math.Abs(Price - EntryPrice) < 0.005m;
    public string FlatCellText => IsCurrentPriceLevel ? "■" : string.Empty;
    public string EntryCellText => IsEntryPriceLevel ? "■" : string.Empty;
    public string FlatCellBrush => IsCurrentPriceLevel ? "#E5E7EB" : "#8FA3B8";
    public string EntryCellBrush => IsEntryPriceLevel ? "#F4B860" : "#8FA3B8";
    public string BidBarBackground => BidQuantity > 0 ? "#284766" : "Transparent";
    public string AskBarBackground => AskQuantity > 0 ? "#5A2834" : "Transparent";
    public string PriceCellBackground => IsCurrentPriceLevel ? "#1B3652" : "#111A24";
    public string PriceAxisLeftBrush => IsBestBid ? "#3DDC84" : "#203244";
    public string PriceAxisRightBrush => IsBestAsk ? "#FF6B6B" : "#3A2630";
    public string CurrentPriceLabel => IsCurrentPriceLevel ? "LAST" : string.Empty;
    public string CurrentPriceBrush => IsCurrentPriceLevel ? "#6FDBFF" : "#8FA3B8";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(PriceBrush));
            this.RaisePropertyChanged(nameof(RowBackground));
            this.RaisePropertyChanged(nameof(PriceCellBackground));
        }
    }
    public string PriceBrush => IsSelected ? "#F4B860" : "#F4F7FB";
    public string RowBackground => IsSelected ? "#2B3646" : "#0F141B";
}

public enum WorkingOrderKind
{
    LimitBuy,
    LimitSell,
    TakeProfit,
    StopLoss
}

public sealed class WorkingOrderViewModel
{
    private Action? _cancel;

    private WorkingOrderViewModel(
        WorkingOrderKind kind,
        string symbol,
        decimal quantity,
        decimal triggerPrice,
        string timeInForce,
        string? exchangeOrderId = null,
        decimal filledQuantity = 0m,
        string statusLabel = "New",
        DateTime? createdAtLocal = null,
        bool reduceOnly = false,
        string exchangeType = "")
    {
        Id = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Kind = kind;
        Symbol = symbol;
        Quantity = quantity;
        TriggerPrice = triggerPrice;
        TimeInForce = timeInForce;
        ExchangeOrderId = exchangeOrderId;
        FilledQuantity = filledQuantity;
        ExplicitStatusLabel = statusLabel;
        CreatedAtLocal = createdAtLocal ?? DateTime.Now;
        ReduceOnly = reduceOnly;
        ExchangeType = exchangeType;
    }

    public string Id { get; }
    public WorkingOrderKind Kind { get; }
    public string Symbol { get; }
    public decimal Quantity { get; }
    public decimal TriggerPrice { get; }
    public string TimeInForce { get; }
    public string? ExchangeOrderId { get; }
    public decimal FilledQuantity { get; }
    public string ExplicitStatusLabel { get; }
    public bool ReduceOnly { get; }
    public string ExchangeType { get; }
    public bool IsExchangeManaged => !string.IsNullOrWhiteSpace(ExchangeOrderId);
    public DateTime CreatedAtLocal { get; }
    public string IdLabel => $"#{Id}";
    public string KindLabel => Kind switch
    {
        WorkingOrderKind.LimitBuy => "BUY LMT",
        WorkingOrderKind.LimitSell => "SELL LMT",
        WorkingOrderKind.TakeProfit => "TAKE PROFIT",
        WorkingOrderKind.StopLoss => "STOP LOSS",
        _ => Kind.ToString()
    };
    public string SideLabel => Kind == WorkingOrderKind.LimitBuy ? "BUY" : "SELL";
    public string TypeLabel => Kind switch
    {
        WorkingOrderKind.LimitBuy => "Limit",
        WorkingOrderKind.LimitSell => "Limit",
        WorkingOrderKind.TakeProfit => "Take Profit",
        WorkingOrderKind.StopLoss => "Stop Market",
        _ => Kind.ToString()
    };
    public string TriggerLabel => $"{TriggerPrice:N2}";
    public string QuantityLabel => $"{Quantity:0.0000}";
    public string FilledLabel => $"{FilledQuantity:0.0000}";
    public string AgeLabel => $"{Math.Max(0, (int)(DateTime.Now - CreatedAtLocal).TotalMinutes)}m";
    public string CreatedLabel => CreatedAtLocal.ToString("HH:mm:ss");
    public string StatusLabel => string.IsNullOrWhiteSpace(ExplicitStatusLabel) ? (IsExchangeManaged ? "Live" : "New") : ExplicitStatusLabel;
    public string StatusBrush => IsExchangeManaged ? "#21E6C1" : "#4FA9FF";
    public ReactiveCommand<Unit, Unit> CancelCommand => ReactiveCommand.Create(() => _cancel?.Invoke());
    public string SideBrush => Kind is WorkingOrderKind.LimitBuy ? "#3DDC84" : "#FF6B6B";

    public static WorkingOrderViewModel CreateLimit(OrderSide side, string symbol, decimal quantity, decimal price, string timeInForce) =>
        new(side == OrderSide.Buy ? WorkingOrderKind.LimitBuy : WorkingOrderKind.LimitSell, symbol, quantity, price, timeInForce);

    public static WorkingOrderViewModel CreateProtection(WorkingOrderKind kind, string symbol, decimal quantity, decimal price) =>
        new(kind, symbol, quantity, price, "GTC");

    public static WorkingOrderViewModel CreateExchangeProtection(WorkingOrderKind kind, string symbol, decimal quantity, decimal price, string exchangeOrderId, decimal filledQuantity = 0m, string statusLabel = "Live", DateTime? createdAtLocal = null, bool reduceOnly = true, string exchangeType = "") =>
        new(kind, symbol, quantity, price, "GTC", exchangeOrderId, filledQuantity, statusLabel, createdAtLocal, reduceOnly, exchangeType);

    public static WorkingOrderViewModel CreateExchangeLimit(OrderSide side, string symbol, decimal quantity, decimal price, string timeInForce, string exchangeOrderId, decimal filledQuantity = 0m, string statusLabel = "Live", DateTime? createdAtLocal = null, bool reduceOnly = false, string exchangeType = "") =>
        new(side == OrderSide.Buy ? WorkingOrderKind.LimitBuy : WorkingOrderKind.LimitSell, symbol, quantity, price, timeInForce, exchangeOrderId, filledQuantity, statusLabel, createdAtLocal, reduceOnly, exchangeType);

    public bool ShouldTrigger(decimal bestBid, decimal bestAsk, decimal lastPrice, decimal openPositionQuantity) =>
        IsExchangeManaged ? false :
        Kind switch
        {
            WorkingOrderKind.LimitBuy => bestAsk <= TriggerPrice || lastPrice <= TriggerPrice,
            WorkingOrderKind.LimitSell => (bestBid >= TriggerPrice || lastPrice >= TriggerPrice) && openPositionQuantity > 0,
            WorkingOrderKind.TakeProfit => (bestBid >= TriggerPrice || lastPrice >= TriggerPrice) && openPositionQuantity > 0,
            WorkingOrderKind.StopLoss => (bestBid <= TriggerPrice || lastPrice <= TriggerPrice) && openPositionQuantity > 0,
            _ => false
        };

    public void AttachCancel(Action cancel) => _cancel = cancel;
}

public sealed class TradeFillViewModel
{
    public TradeFillViewModel(string symbol, OrderSide side, decimal price, decimal quantity, string status)
    {
        Symbol = symbol;
        Side = side;
        Price = price;
        Quantity = quantity;
        Status = status;
        TimestampLocal = DateTime.Now;
    }

    public string Symbol { get; }
    public OrderSide Side { get; }
    public decimal Price { get; }
    public decimal Quantity { get; }
    public string Status { get; }
    public DateTime TimestampLocal { get; }
    public string SideLabel => Side == OrderSide.Buy ? "BUY" : "SELL";
    public string PriceLabel => $"{Price:N2}";
    public string QuantityLabel => $"{Quantity:0.0000}";
    public string TimeLabel => TimestampLocal.ToString("HH:mm:ss");
    public string SideBrush => Side == OrderSide.Buy ? "#3DDC84" : "#FF6B6B";
    public string StatusBrush => Status.Equals("FILLED", StringComparison.OrdinalIgnoreCase) ? "#21E6C1" : "#F4B860";
}

public sealed class PositionRowViewModel
{
    public PositionRowViewModel(string symbol, decimal quantity, decimal entryPrice, decimal markPrice, decimal unrealizedPnl, decimal realizedPnl)
    {
        Symbol = symbol;
        Quantity = quantity;
        EntryPrice = entryPrice;
        MarkPrice = markPrice;
        UnrealizedPnl = unrealizedPnl;
        RealizedPnl = realizedPnl;
    }

    public string Symbol { get; }
    public decimal Quantity { get; }
    public decimal EntryPrice { get; }
    public decimal MarkPrice { get; }
    public decimal UnrealizedPnl { get; }
    public decimal RealizedPnl { get; }
    public string QuantityLabel => Quantity > 0 ? $"LONG {Quantity:0.0000}" : $"SHORT {Math.Abs(Quantity):0.0000}";
    public string SizeLabel => $"{Math.Abs(Quantity):0.0000}";
    public string EntryPriceLabel => $"{EntryPrice:N2}";
    public string MarkPriceLabel => $"{MarkPrice:N2}";
    public string UnrealizedPnlLabel => $"{UnrealizedPnl:+0.00;-0.00;0.00}";
    public string RealizedPnlLabel => $"{RealizedPnl:+0.00;-0.00;0.00}";
    public decimal PnlPercent => EntryPrice <= 0 || Quantity == 0 ? 0m : ((MarkPrice - EntryPrice) / EntryPrice) * (Quantity > 0 ? 100m : -100m);
    public string PnlPercentLabel => $"{PnlPercent:+0.##;-0.##;0.##}%";
    public decimal MarginValue => Math.Abs(Quantity * EntryPrice) * 0.60m;
    public string MarginLabel => $"{MarginValue:N2}";
    public string RoeLabel => $"{PnlPercent:+0.##;-0.##;0.##}%";
    public string UnrealizedPnlBrush => UnrealizedPnl >= 0 ? "#3DDC84" : "#FF6B6B";
    public string RealizedPnlBrush => RealizedPnl >= 0 ? "#3DDC84" : "#FF6B6B";
}

public sealed class AiAssistantQuickPromptViewModel
{
    public AiAssistantQuickPromptViewModel(string categoryKey, string label, string prompt)
    {
        CategoryKey = categoryKey;
        Label = label;
        Prompt = prompt;
    }

    public string CategoryKey { get; }
    public string Label { get; }
    public string Prompt { get; }
}

public sealed class AiPromptPresetViewModel
{
    public AiPromptPresetViewModel(string label, string summary, string asset, string tradeStyle, string horizon, string riskProfile, string focus)
    {
        Label = label;
        Summary = summary;
        Asset = asset;
        TradeStyle = tradeStyle;
        Horizon = horizon;
        RiskProfile = riskProfile;
        Focus = focus;
    }

    public string Label { get; }
    public string Summary { get; }
    public string Asset { get; }
    public string TradeStyle { get; }
    public string Horizon { get; }
    public string RiskProfile { get; }
    public string Focus { get; }
}

public readonly record struct AiPromptPresetStorageItem(
    string Name,
    string Asset,
    string TradeStyle,
    string Horizon,
    string RiskProfile,
    string Focus);

public sealed class AiContextCardViewModel
{
    public AiContextCardViewModel(string overline, string value, string caption)
    {
        Overline = overline;
        Value = value;
        Caption = caption;
    }

    public string Overline { get; }
    public string Value { get; }
    public string Caption { get; }
}

public sealed class AiVisualCardViewModel : ReactiveObject
{
    private bool _isSelected;

    public AiVisualCardViewModel(string key, string title, string caption, string metric, Bitmap image)
    {
        Key = key;
        Title = title;
        Caption = caption;
        Metric = metric;
        Image = image;
    }

    public string Key { get; }
    public string Title { get; }
    public string Caption { get; }
    public string Metric { get; }
    public Bitmap Image { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(CardBorderBrush));
            this.RaisePropertyChanged(nameof(CardBackground));
            this.RaisePropertyChanged(nameof(MetricBrush));
        }
    }

    public string CardBorderBrush => IsSelected ? "#21E6C1" : "#25384A";
    public string CardBackground => IsSelected ? "#111D27" : "#0C141C";
    public string MetricBrush => IsSelected ? "#D8FFF8" : "#8FA3B8";
}

public sealed class AiKnowledgeTopicViewModel
{
    public AiKnowledgeTopicViewModel(string label, string category, string question, string answer)
    {
        Label = label;
        Category = category;
        Question = question;
        Answer = answer;
    }

    public string Label { get; }
    public string Category { get; }
    public string Question { get; }
    public string Answer { get; }
}

public sealed class AiAssistantMessageViewModel
{
    public AiAssistantMessageViewModel(bool isUser, string title, string body, string meta)
    {
        IsUser = isUser;
        Title = title;
        Body = body;
        Meta = meta;
    }

    public bool IsUser { get; }
    public string Title { get; }
    public string Body { get; }
    public string Meta { get; }
    public int ColumnIndex => IsUser ? 1 : 0;
    public HorizontalAlignment BubbleAlignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public string BubbleBackground => IsUser ? "#123A36" : "#0F1822";
    public string BubbleBorderBrush => IsUser ? "#21E6C1" : "#243749";
    public string TitleBrush => IsUser ? "#D7FFF6" : "#F4F7FB";
    public string MetaBrush => IsUser ? "#8BF0DD" : "#8FA3B8";
}
public sealed class SignalRowViewModel
{
    public SignalRowViewModel(string symbol, string setup, string entry, string target, string stop, string status)
    {
        Symbol = symbol;
        Setup = setup;
        Entry = entry;
        Target = target;
        Stop = stop;
        Status = status;
        TimestampLocal = DateTime.Now;
    }

    public string Symbol { get; }
    public string Setup { get; }
    public string Entry { get; }
    public string Target { get; }
    public string Stop { get; }
    public string Status { get; }
    public DateTime TimestampLocal { get; }
    public string TimeLabel => TimestampLocal.ToString("HH:mm:ss");
    public string StatusBrush => Status == "ACTIVE" ? "#3DDC84" : "#F4B860";
}

public sealed class ActivityFeedRowViewModel
{
    public ActivityFeedRowViewModel(string timeLabel, string message, string status, string statusBrush)
    {
        TimeLabel = timeLabel;
        Message = message;
        Status = status;
        StatusBrush = statusBrush;
    }

    public string TimeLabel { get; }
    public string Message { get; }
    public string Status { get; }
    public string StatusBrush { get; }
}

