using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Threading.Tasks;
using CryptoAITerminal.Gateway.DEX;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class WalletWorkspaceViewModel : ReactiveObject, IDisposable
{
    private static readonly IReadOnlyList<string> SharedGlobalQuoteAssetOptions = ["USDT", "USDC", "NATIVE"];
    private static readonly IReadOnlyList<string> SharedGlobalPositionSizingOptions = ["25", "50", "75", "100"];
    private static readonly IReadOnlyDictionary<string, WalletNetworkDefinition> NetworkDefinitions =
        new Dictionary<string, WalletNetworkDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["BSC"] = new("BSC", "BNB", "https://bsc-dataseed.binance.org/", "https://bscscan.com/address/", true),
            ["Ethereum"] = new("Ethereum", "ETH", "https://ethereum-rpc.publicnode.com", "https://etherscan.io/address/", true),
            ["Base"] = new("Base", "ETH", "https://mainnet.base.org", "https://basescan.org/address/", true),
            ["Solana"] = new("Solana", "SOL", string.Empty, "https://solscan.io/account/", true),
            ["Tron"] = new("Tron", "TRX", "https://api.trongrid.io/", "https://tronscan.org/#/address/", true),
            ["Polygon"] = new("Polygon", "POL", "https://polygon-rpc.com/", "https://polygonscan.com/address/", true),
            ["Arbitrum"] = new("Arbitrum", "ETH", "https://arb1.arbitrum.io/rpc", "https://arbiscan.io/address/", true)
        };

    private static readonly IReadOnlyDictionary<string, string> ProviderLaunchUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MetaMask"] = "https://metamask.io/download/",
            ["Trust Wallet"] = "https://trustwallet.com/download",
            ["Phantom"] = "https://phantom.app/download",
            ["TronLink"] = "https://www.tronlink.org/",
            ["Session Key"] = "https://bscscan.com/"
        };

    private readonly Action<string>? _log;
    private readonly string _storageFilePath;

    private string _selectedProvider = "MetaMask";
    private string _selectedNetwork = "BSC";
    private string _walletAddressInput = string.Empty;
    private string _privateKeyInput = string.Empty;
    private string _connectedAddress = string.Empty;
    private string _statusMessage = "Connect a wallet once, then use it across the whole terminal.";
    private string _solanaDiagnosticsSummary = "Solana diagnostics have not been run yet.";
    private string _tronDiagnosticsSummary = "Tron diagnostics have not been run yet.";
    private bool _isConnected;
    private bool _isReadOnly = true;
    private bool _isRunningSolanaDiagnostics;
    private bool _isRunningTronDiagnostics;
    private decimal _nativeBalance;
    private DateTime _lastSyncLocal = DateTime.MinValue;
    private IDexTradeGateway? _activeDexGateway;
    private string? _sessionPrivateKey;
    private SavedWalletViewModel? _selectedSavedWallet;
    private string _tronPaymentRecipient = string.Empty;
    private string _tronTrc20ContractAddress = TronTradeGateway.DefaultUsdtContractAddress;
    private string _tronTrc20Symbol = "USDT";
    private decimal _tronTrc20Amount = 1m;
    private decimal _tronFeeLimitTrx = 30m;
    private decimal _tronTrc20Balance;
    private string _tronPaymentStatus = "Arm a Tron wallet session to send TRC20 transfers.";
    private string _globalQuoteAssetSymbol = "USDT";
    private decimal _globalPositionSizingPercent = 25m;
    private decimal _globalMaxSpendPerTradeUsdt = 250m;
    private decimal _globalMaxDailyLossUsdt = 500m;
    private decimal _globalMaxOpenExposureUsdt = 1500m;
    private bool _globalPaperOnlyMode = true;

    public WalletWorkspaceViewModel(Action<string>? log = null)
    {
        _log = log;
        var storageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal");
        Directory.CreateDirectory(storageDirectory);
        _storageFilePath = Path.Combine(storageDirectory, "wallet-profiles.json");

        AvailableNetworks = new ObservableCollection<string>(NetworkDefinitions.Keys);
        Assets = new ObservableCollection<WalletAssetViewModel>();
        RecentActivity = new ObservableCollection<string>();
        SavedWallets = new ObservableCollection<SavedWalletViewModel>();
        SolanaDiagnosticSteps = new ObservableCollection<string>();
        TronDiagnosticSteps = new ObservableCollection<string>();

        SelectProviderCommand = ReactiveCommand.Create<string>(SelectProvider, outputScheduler: App.UiScheduler);
        ConnectWatchCommand = ReactiveCommand.CreateFromTask(ConnectWatchWalletAsync, outputScheduler: App.UiScheduler);
        ImportPrivateKeyCommand = ReactiveCommand.CreateFromTask(ImportPrivateKeyAsync, outputScheduler: App.UiScheduler);
        RefreshWalletCommand = ReactiveCommand.CreateFromTask(RefreshWalletAsync, outputScheduler: App.UiScheduler);
        RunSolanaDiagnosticsCommand = ReactiveCommand.CreateFromTask(RunSolanaDiagnosticsAsync, outputScheduler: App.UiScheduler);
        RunTronDiagnosticsCommand = ReactiveCommand.CreateFromTask(RunTronDiagnosticsAsync, outputScheduler: App.UiScheduler);
        DisconnectWalletCommand = ReactiveCommand.Create(DisconnectWallet, outputScheduler: App.UiScheduler);
        OpenProviderCommand = ReactiveCommand.CreateFromTask(OpenProviderAsync, outputScheduler: App.UiScheduler);
        LoadSavedWalletCommand = ReactiveCommand.CreateFromTask<SavedWalletViewModel>(LoadSavedWalletAsync, outputScheduler: App.UiScheduler);
        DeleteSavedWalletCommand = ReactiveCommand.Create<SavedWalletViewModel>(DeleteSavedWallet, outputScheduler: App.UiScheduler);
        SendTronPaymentCommand = ReactiveCommand.CreateFromTask(SendTronPaymentAsync, outputScheduler: App.UiScheduler);
        ApplyTronTokenPresetCommand = ReactiveCommand.CreateFromTask<string>(ApplyTronTokenPresetAsync, outputScheduler: App.UiScheduler);
        ApplyGlobalQuoteAssetCommand = ReactiveCommand.Create<string>(ApplyGlobalQuoteAsset, outputScheduler: App.UiScheduler);
        ApplyGlobalPositionSizingCommand = ReactiveCommand.Create<string>(ApplyGlobalPositionSizing, outputScheduler: App.UiScheduler);
        ApplyGlobalExecutionModeCommand = ReactiveCommand.Create<string>(ApplyGlobalExecutionMode, outputScheduler: App.UiScheduler);

        SeedPreviewCards();
        LoadSavedWalletsFromDisk();
        _ = TryBootstrapFromEnvironmentAsync();
    }

    public ObservableCollection<string> AvailableNetworks { get; }
    public ObservableCollection<WalletAssetViewModel> Assets { get; }
    public ObservableCollection<string> RecentActivity { get; }
    public ObservableCollection<SavedWalletViewModel> SavedWallets { get; }
    public ObservableCollection<string> SolanaDiagnosticSteps { get; }
    public ObservableCollection<string> TronDiagnosticSteps { get; }
    public IReadOnlyList<string> AvailableProviders { get; } = ["MetaMask", "Trust Wallet", "Phantom", "TronLink", "Session Key"];

    public ReactiveCommand<string, Unit> SelectProviderCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectWatchCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportPrivateKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshWalletCommand { get; }
    public ReactiveCommand<Unit, Unit> RunSolanaDiagnosticsCommand { get; }
    public ReactiveCommand<Unit, Unit> RunTronDiagnosticsCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectWalletCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenProviderCommand { get; }
    public ReactiveCommand<SavedWalletViewModel, Unit> LoadSavedWalletCommand { get; }
    public ReactiveCommand<SavedWalletViewModel, Unit> DeleteSavedWalletCommand { get; }
    public ReactiveCommand<Unit, Unit> SendTronPaymentCommand { get; }
    public ReactiveCommand<string, Unit> ApplyTronTokenPresetCommand { get; }
    public ReactiveCommand<string, Unit> ApplyGlobalQuoteAssetCommand { get; }
    public ReactiveCommand<string, Unit> ApplyGlobalPositionSizingCommand { get; }
    public ReactiveCommand<string, Unit> ApplyGlobalExecutionModeCommand { get; }

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedProvider, value);
            RaiseComputedWalletState();
        }
    }

    public string SelectedNetwork
    {
        get => _selectedNetwork;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNetwork, value);
            RaiseComputedWalletState();
        }
    }

    public string WalletAddressInput
    {
        get => _walletAddressInput;
        set => this.RaiseAndSetIfChanged(ref _walletAddressInput, value);
    }

    public string PrivateKeyInput
    {
        get => _privateKeyInput;
        set => this.RaiseAndSetIfChanged(ref _privateKeyInput, value);
    }

    public string ConnectedAddress
    {
        get => _connectedAddress;
        private set
        {
            this.RaiseAndSetIfChanged(ref _connectedAddress, value);
            RaiseComputedWalletState();
        }
    }

    public string TronPaymentRecipient
    {
        get => _tronPaymentRecipient;
        set => this.RaiseAndSetIfChanged(ref _tronPaymentRecipient, value);
    }

    public string TronTrc20ContractAddress
    {
        get => _tronTrc20ContractAddress;
        set
        {
            this.RaiseAndSetIfChanged(ref _tronTrc20ContractAddress, value);
            RaiseComputedWalletState();
        }
    }

    public string TronTrc20Symbol
    {
        get => _tronTrc20Symbol;
        set
        {
            this.RaiseAndSetIfChanged(ref _tronTrc20Symbol, value);
            this.RaisePropertyChanged(nameof(TronTrc20BalanceLabel));
            RaiseComputedWalletState();
        }
    }

    public decimal TronTrc20Amount
    {
        get => _tronTrc20Amount;
        set => this.RaiseAndSetIfChanged(ref _tronTrc20Amount, Math.Max(0m, value));
    }

    public decimal TronFeeLimitTrx
    {
        get => _tronFeeLimitTrx;
        set => this.RaiseAndSetIfChanged(ref _tronFeeLimitTrx, Math.Max(1m, value));
    }

    public decimal TronTrc20Balance
    {
        get => _tronTrc20Balance;
        private set
        {
            this.RaiseAndSetIfChanged(ref _tronTrc20Balance, value);
            this.RaisePropertyChanged(nameof(TronTrc20BalanceLabel));
        }
    }

    public string TronTrc20BalanceLabel => $"{TronTrc20Balance:0.######} {TronTrc20Symbol}";
    public bool IsTronUsdtSelected =>
        string.Equals(TronTrc20Symbol, "USDT", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(TronTrc20ContractAddress, TronTradeGateway.DefaultUsdtContractAddress, StringComparison.OrdinalIgnoreCase);
    public bool IsTronUsdcSelected =>
        string.Equals(TronTrc20Symbol, "USDC", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(TronTrc20ContractAddress, TronTradeGateway.DefaultUsdcContractAddress, StringComparison.OrdinalIgnoreCase);
    public string TronUsdtPresetBackground => IsTronUsdtSelected ? "#17373B" : "#0F1721";
    public string TronUsdcPresetBackground => IsTronUsdcSelected ? "#17373B" : "#0F1721";
    public string TronUsdtPresetForeground => IsTronUsdtSelected ? "#F4F7FB" : "#8FA3B8";
    public string TronUsdcPresetForeground => IsTronUsdcSelected ? "#F4F7FB" : "#8FA3B8";

    public string TronPaymentStatus
    {
        get => _tronPaymentStatus;
        private set => this.RaiseAndSetIfChanged(ref _tronPaymentStatus, value);
    }

    public IReadOnlyList<string> GlobalQuoteAssetOptions => SharedGlobalQuoteAssetOptions;

    public string GlobalQuoteAssetSymbol
    {
        get => _globalQuoteAssetSymbol;
        set
        {
            var normalized = NormalizeGlobalQuoteAssetSymbol(value);
            if (string.Equals(_globalQuoteAssetSymbol, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _globalQuoteAssetSymbol, normalized);
            RaiseComputedWalletState();
            PersistSavedWallets();
        }
    }

    public string EffectiveGlobalQuoteAssetSymbol => ResolveEffectiveGlobalQuoteAssetSymbol();

    public string GlobalQuoteAssetModeLabel =>
        string.Equals(GlobalQuoteAssetSymbol, "NATIVE", StringComparison.OrdinalIgnoreCase)
            ? $"NATIVE · {GetCurrentNetwork().NativeSymbol}"
            : string.Equals(GlobalQuoteAssetSymbol, EffectiveGlobalQuoteAssetSymbol, StringComparison.OrdinalIgnoreCase)
                ? $"{EffectiveGlobalQuoteAssetSymbol} ACTIVE"
                : $"{GlobalQuoteAssetSymbol} → {EffectiveGlobalQuoteAssetSymbol}";

    public string GlobalQuoteAssetModeBrush =>
        string.Equals(EffectiveGlobalQuoteAssetSymbol, "USDT", StringComparison.OrdinalIgnoreCase)
            ? "#14E0C1"
            : string.Equals(EffectiveGlobalQuoteAssetSymbol, "USDC", StringComparison.OrdinalIgnoreCase)
                ? "#F4B860"
                : "#8FA3B8";

    public string GlobalQuoteAssetSummary
    {
        get
        {
            if (string.Equals(GlobalQuoteAssetSymbol, "NATIVE", StringComparison.OrdinalIgnoreCase))
            {
                return $"DEX and Sniper use the native {GetCurrentNetwork().NativeSymbol} route on {SelectedNetwork}. CEX spot trading stays on USDT pairs.";
            }

            if (string.Equals(GlobalQuoteAssetSymbol, EffectiveGlobalQuoteAssetSymbol, StringComparison.OrdinalIgnoreCase))
            {
                return $"{GlobalQuoteAssetSymbol} is the shared quote asset for DEX and Sniper on {SelectedNetwork}. CEX spot trading stays on USDT pairs.";
            }

            return $"{GlobalQuoteAssetSymbol} stays preferred globally, DEX/Sniper on {SelectedNetwork} are using {EffectiveGlobalQuoteAssetSymbol} as the live fallback route, and CEX spot trading stays on USDT pairs.";
        }
    }

    public string GlobalQuotePersistenceLabel => "Persisted: yes";

    public string GlobalQuoteFallbackLabel =>
        string.Equals(GlobalQuoteAssetSymbol, "NATIVE", StringComparison.OrdinalIgnoreCase)
            ? $"Fallback: native {GetCurrentNetwork().NativeSymbol}"
            : string.Equals(GlobalQuoteAssetSymbol, EffectiveGlobalQuoteAssetSymbol, StringComparison.OrdinalIgnoreCase)
                ? $"Fallback: none · {EffectiveGlobalQuoteAssetSymbol}"
                : $"Fallback: {SelectedNetwork} · {GlobalQuoteAssetSymbol} → {EffectiveGlobalQuoteAssetSymbol}";

    public string GlobalQuoteTabBadgeLabel =>
        string.Equals(EffectiveGlobalQuoteAssetSymbol, "USDT", StringComparison.OrdinalIgnoreCase)
            ? "USDT"
            : string.Equals(EffectiveGlobalQuoteAssetSymbol, "USDC", StringComparison.OrdinalIgnoreCase)
                ? "USDC"
                : GetCurrentNetwork().NativeSymbol.ToUpperInvariant();

    public bool GlobalPaperOnlyMode
    {
        get => _globalPaperOnlyMode;
        set
        {
            if (_globalPaperOnlyMode == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _globalPaperOnlyMode, value);
            RaiseComputedWalletState();
            PersistSavedWallets();
        }
    }

    public bool GlobalLiveExecutionEnabled => !GlobalPaperOnlyMode;
    public string GlobalExecutionModeLabel => GlobalPaperOnlyMode ? "PAPER ONLY" : "LIVE ALLOWED";
    public string GlobalExecutionModeBrush => GlobalPaperOnlyMode ? "#F4B860" : "#FF6B6B";
    public string GlobalExecutionSummary => GlobalPaperOnlyMode
        ? "All real sends are blocked across Trading, DEX, Sniper and Tron payments."
        : "Live sends are allowed across Trading, DEX, Sniper and Tron payments.";
    public string GlobalExecutionPersistenceLabel => "Persisted: yes";
    public string GlobalExecutionGuardStatusLabel => GlobalPaperOnlyMode
        ? "Guard: every live order is stopped before broadcast."
        : "Guard: live execution can leave the terminal right now.";

    public IReadOnlyList<string> GlobalPositionSizingOptions => SharedGlobalPositionSizingOptions;

    public decimal GlobalPositionSizingPercent
    {
        get => _globalPositionSizingPercent;
        set
        {
            var normalized = Math.Clamp(decimal.Round(value, 0, MidpointRounding.AwayFromZero), 1m, 100m);
            if (_globalPositionSizingPercent == normalized)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _globalPositionSizingPercent, normalized);
            RaiseComputedWalletState();
            PersistSavedWallets();
        }
    }

    public string GlobalPositionSizingLabel => $"{GlobalPositionSizingPercent:0}% SIZE";
    public string GlobalPositionSizingSummary => $"One sizing preset drives CEX Trading, DEX and Sniper from available balance.";

    public decimal GlobalMaxSpendPerTradeUsdt
    {
        get => _globalMaxSpendPerTradeUsdt;
        set
        {
            var normalized = Math.Max(1m, decimal.Round(value, 2, MidpointRounding.AwayFromZero));
            if (_globalMaxSpendPerTradeUsdt == normalized)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _globalMaxSpendPerTradeUsdt, normalized);
            RaiseComputedWalletState();
            PersistSavedWallets();
        }
    }

    public decimal GlobalMaxDailyLossUsdt
    {
        get => _globalMaxDailyLossUsdt;
        set
        {
            var normalized = Math.Max(1m, decimal.Round(value, 2, MidpointRounding.AwayFromZero));
            if (_globalMaxDailyLossUsdt == normalized)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _globalMaxDailyLossUsdt, normalized);
            RaiseComputedWalletState();
            PersistSavedWallets();
        }
    }

    public decimal GlobalMaxOpenExposureUsdt
    {
        get => _globalMaxOpenExposureUsdt;
        set
        {
            var normalized = Math.Max(1m, decimal.Round(value, 2, MidpointRounding.AwayFromZero));
            if (_globalMaxOpenExposureUsdt == normalized)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _globalMaxOpenExposureUsdt, normalized);
            RaiseComputedWalletState();
            PersistSavedWallets();
        }
    }

    public string GlobalRiskCapLabel => $"Max trade {GlobalMaxSpendPerTradeUsdt:0.##} USDT";
    public string GlobalRiskSummary => $"Daily loss {GlobalMaxDailyLossUsdt:0.##} USDT · Open exposure {GlobalMaxOpenExposureUsdt:0.##} USDT";

    public bool TryApproveLiveExecution(string routeLabel, out string reason)
    {
        reason = GetExecutionGuardBlockReason(routeLabel);
        return string.IsNullOrWhiteSpace(reason);
    }

    /// <summary>
    /// Set by the license layer: when false (trial expired / no license) live
    /// execution is blocked everywhere and the mode is pinned to PAPER ONLY.
    /// </summary>
    public bool LicenseAllowsLive { get; set; } = true;

    public string GetExecutionGuardBlockReason(string routeLabel)
    {
        if (!LicenseAllowsLive)
            return $"Live execution blocked for {routeLabel}: activate a license to trade live (trial expired).";

        return GlobalPaperOnlyMode
            ? $"Global execution guard blocked {routeLabel}: PAPER ONLY is active."
            : string.Empty;
    }

    public string GetDexExecutionBlockReason(string routeLabel, string? chainId, string? dexId)
    {
        if (!IsConnected)
        {
            return "Connect a wallet session first.";
        }

        if (IsReadOnly)
        {
            return "Import a trading key to unlock live DEX execution.";
        }

        var executionReason = GetExecutionGuardBlockReason(routeLabel);
        if (!string.IsNullOrWhiteSpace(executionReason))
        {
            return executionReason;
        }

        if (string.IsNullOrWhiteSpace(chainId))
        {
            return "The selected token does not expose a chain route yet.";
        }

        var targetNetwork = MapChainIdToNetwork(chainId);
        if (!string.Equals(SelectedNetwork, targetNetwork, StringComparison.OrdinalIgnoreCase))
        {
            return $"The active wallet session is armed for {SelectedNetwork}, but this route needs {targetNetwork}.";
        }

        if (!GetCurrentNetwork().SupportsDexTrading)
        {
            return $"{SelectedNetwork} is connected, but live DEX routing is not enabled for this network.";
        }

        if (ActiveDexGateway is null)
        {
            return $"{SelectedNetwork} trading key is loaded, but the live DEX connector is not attached yet.";
        }

        if (!string.IsNullOrWhiteSpace(dexId) && !ActiveDexGateway.SupportsDex(dexId))
        {
            return $"Dex '{dexId}' is not wired into the active {SelectedNetwork} connector. Supported: {ActiveDexGateway.SupportedDexesLabel}.";
        }

        return string.Empty;
    }

    public string GetTronPaymentBlockReason()
    {
        if (!IsConnected)
        {
            return "Connect a Tron wallet session first.";
        }

        if (!IsTronNetworkSelected)
        {
            return "Switch the wallet network to Tron before broadcasting a TRC20 transfer.";
        }

        if (IsReadOnly)
        {
            return "Import a Tron private key to unlock live TRC20 transfers.";
        }

        var executionReason = GetExecutionGuardBlockReason("TRON payment");
        if (!string.IsNullOrWhiteSpace(executionReason))
        {
            return executionReason;
        }

        if (ActiveDexGateway is not TronTradeGateway)
        {
            return "Tron live routing is not attached yet. Refresh the armed wallet session and try again.";
        }

        return string.Empty;
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string SolanaDiagnosticsSummary
    {
        get => _solanaDiagnosticsSummary;
        private set => this.RaiseAndSetIfChanged(ref _solanaDiagnosticsSummary, value);
    }

    public string TronDiagnosticsSummary
    {
        get => _tronDiagnosticsSummary;
        private set => this.RaiseAndSetIfChanged(ref _tronDiagnosticsSummary, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            RaiseComputedWalletState();
        }
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isReadOnly, value);
            RaiseComputedWalletState();
        }
    }

    public bool IsRunningSolanaDiagnostics
    {
        get => _isRunningSolanaDiagnostics;
        private set => this.RaiseAndSetIfChanged(ref _isRunningSolanaDiagnostics, value);
    }

    public bool IsRunningTronDiagnostics
    {
        get => _isRunningTronDiagnostics;
        private set => this.RaiseAndSetIfChanged(ref _isRunningTronDiagnostics, value);
    }

    public decimal NativeBalance
    {
        get => _nativeBalance;
        private set
        {
            this.RaiseAndSetIfChanged(ref _nativeBalance, value);
            RaiseComputedWalletState();
        }
    }

    public DateTime LastSyncLocal
    {
        get => _lastSyncLocal;
        private set
        {
            this.RaiseAndSetIfChanged(ref _lastSyncLocal, value);
            RaiseComputedWalletState();
        }
    }

    public IDexTradeGateway? ActiveDexGateway
    {
        get => _activeDexGateway;
        private set
        {
            _activeDexGateway = value;
            RaiseComputedWalletState();
        }
    }

    public bool CanUseDexTradingOnSelectedNetwork =>
        IsConnected &&
        !IsReadOnly &&
        GetCurrentNetwork().SupportsDexTrading &&
        ActiveDexGateway is not null;

    public bool IsTronNetworkSelected => string.Equals(SelectedNetwork, "Tron", StringComparison.OrdinalIgnoreCase);

    public bool CanUseTronPayments =>
        IsConnected &&
        !IsReadOnly &&
        IsTronNetworkSelected &&
        ActiveDexGateway is TronTradeGateway;

    public bool CanSendTronPaymentLive => string.IsNullOrWhiteSpace(GetTronPaymentBlockReason());
    public string TronPaymentBlockedReason => CanSendTronPaymentLive
        ? "Ready to broadcast a live TRC20 transfer."
        : GetTronPaymentBlockReason();

    public bool CanUseDexTradingOnBsc => CanTradeChainId("bsc");

    public string WalletAddressShort =>
        string.IsNullOrWhiteSpace(ConnectedAddress)
            ? "No wallet"
            : ConnectedAddress.Length <= 12
                ? ConnectedAddress
                : $"{ConnectedAddress[..6]}...{ConnectedAddress[^4..]}";

    public string ActiveWalletBanner =>
        IsConnected
            ? $"{SelectedProvider} · {WalletAddressShort}"
            : "Wallet Hub · Disconnected";

    public string ConnectionSummary =>
        !IsConnected
            ? "No active wallet session. Connect once and the same wallet context will appear across the terminal."
            : $"{SelectedNetwork} · {(IsReadOnly ? "Watch mode" : "Trading mode")} · Last sync {(LastSyncLocal == DateTime.MinValue ? "pending" : LastSyncLocal.ToString("HH:mm:ss"))}";

    public string NativeBalanceLabel =>
        !IsConnected
            ? "--"
            : $"{NativeBalance:N4} {GetCurrentNetwork().NativeSymbol}";

    public string WalletModeLabel => IsReadOnly ? "Read-only monitor" : "Trade-enabled session";

    public string WalletReadinessLabel =>
        !IsConnected
            ? "DISCONNECTED"
            : IsReadOnly
                ? "WATCH READY"
                : GlobalPaperOnlyMode
                    ? "PAPER ARMED"
                    : ActiveDexGateway is not null
                        ? "LIVE READY"
                        : "KEY LOADED";

    public string WalletReadinessBrush =>
        WalletReadinessLabel switch
        {
            "LIVE READY" => "#14E0C1",
            "PAPER ARMED" => "#F4B860",
            "WATCH READY" => "#5BC0EB",
            "KEY LOADED" => "#FF9F43",
            _ => "#8FA3B8"
        };

    public string WalletReadinessSummary
    {
        get
        {
            if (!IsConnected)
            {
                return "No wallet is connected yet. Connect an address to restore portfolio visibility and route awareness.";
            }

            if (IsReadOnly)
            {
                return $"{SelectedNetwork} is connected in watch mode. Balances and monitoring are live, but real execution stays blocked until a trading key is imported.";
            }

            if (GlobalPaperOnlyMode)
            {
                return $"The {SelectedNetwork} trading session is armed, but the global paper guard is still blocking every broadcast.";
            }

            if (ActiveDexGateway is not null)
            {
                return $"{SelectedNetwork} is fully armed for live execution through {ActiveDexGateway.SupportedDexesLabel}.";
            }

            return $"{SelectedNetwork} key material is loaded, but no live execution connector is attached to the active session yet.";
        }
    }

    public string RouteReadinessLabel =>
        !IsConnected
            ? "NO ROUTE"
            : IsReadOnly
                ? "MONITOR ONLY"
                : GlobalPaperOnlyMode
                    ? $"PAPER {EffectiveGlobalQuoteAssetSymbol}"
                    : CanUseDexTradingOnSelectedNetwork
                        ? $"LIVE {EffectiveGlobalQuoteAssetSymbol}"
                        : "ROUTE LIMITED";

    public string RouteReadinessBrush =>
        RouteReadinessLabel.StartsWith("LIVE", StringComparison.OrdinalIgnoreCase)
            ? "#14E0C1"
            : RouteReadinessLabel.StartsWith("PAPER", StringComparison.OrdinalIgnoreCase)
                ? "#F4B860"
                : string.Equals(RouteReadinessLabel, "MONITOR ONLY", StringComparison.OrdinalIgnoreCase)
                    ? "#5BC0EB"
                    : "#8FA3B8";

    public string RouteReadinessSummary
    {
        get
        {
            if (!IsConnected)
            {
                return "No execution route is active until a wallet session is connected.";
            }

            if (IsReadOnly)
            {
                return $"{SelectedNetwork} route is in monitor-only mode. Explorer access and balances are live, but swaps and sends remain disarmed.";
            }

            var routeLabel = ActiveDexGateway?.SupportedDexesLabel ?? "connector pending";
            var quoteLabel = EffectiveGlobalQuoteAssetSymbol;

            if (GlobalPaperOnlyMode)
            {
                return $"{SelectedNetwork} route is dry-running with {quoteLabel}. Quotes and self-tests work, but the paper guard still blocks broadcast.";
            }

            if (CanUseDexTradingOnSelectedNetwork)
            {
                return $"{SelectedNetwork} can route live execution through {routeLabel} with {quoteLabel} as the active quote path.";
            }

            return $"{SelectedNetwork} session is armed, but the route is limited to wallet visibility until a compatible live connector is available.";
        }
    }

    public string WalletCapabilityText =>
        CanUseDexTradingOnSelectedNetwork
            ? $"This wallet can execute {_selectedNetwork} DEX trades right now. Live connectors: {ActiveDexGateway?.SupportedDexesLabel ?? "none"}."
            : IsConnected && IsReadOnly
                ? string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase)
                    ? "This wallet is connected in watch mode. Import a 64-byte Solana keypair secret to unlock live Solana execution."
                    : IsTronNetworkSelected
                        ? "This wallet is connected in watch mode. Import a Tron private key to unlock live TRC20 transfers."
                        : "This wallet is connected in watch mode. Import an in-memory EVM private key to unlock trading."
                : IsConnected && string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase)
                    ? "Solana wallet sharing is active. Import a 64-byte Solana keypair secret to arm live Jupiter-backed execution."
                : IsConnected && IsTronNetworkSelected
                        ? $"Tron wallet sharing is active. Live connector is armed for SunSwap and TRC20 flows. Available routes: {ActiveDexGateway?.SupportedDexesLabel ?? "none"}."
                        : IsConnected
                        ? $"Connected wallet is shared globally. {_selectedNetwork} DEX execution unlocks when the session is trade-enabled and a live connector exists."
                        : "Pick a provider, connect an address or import an in-memory trading key, and the wallet becomes global for the whole app.";

    public string NetworkExecutionMatrix =>
        "CEX: Binance spot pairs are USDT-only | BSC: native + USDT + USDC | Ethereum: native + USDT + USDC | Base: native + USDC (USDT falls back to USDC) | Solana: native + USDT + USDC | Tron: SunSwap native + USDT + USDC and direct TRC20 transfers.";

    public string NetworkExecutionNotes =>
        "Use the global quote selector to align DEX and Sniper. CEX manual trading still routes through Binance USDT spot symbols, while Tron sessions now support both wallet transfers and SunSwap-based token execution.";

    public bool CanRunSolanaDiagnostics =>
        IsConnected &&
        string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase) &&
        ActiveDexGateway is SolanaTradeGateway;

    public bool CanRunTronDiagnostics =>
        IsConnected &&
        IsTronNetworkSelected &&
        !IsReadOnly &&
        ActiveDexGateway is TronTradeGateway;

    public string ExplorerUrl =>
        string.IsNullOrWhiteSpace(ConnectedAddress)
            ? GetCurrentNetwork().ExplorerBaseUrl
            : $"{GetCurrentNetwork().ExplorerBaseUrl}{ConnectedAddress}";

    public SavedWalletViewModel? SelectedSavedWallet
    {
        get => _selectedSavedWallet;
        set => this.RaiseAndSetIfChanged(ref _selectedSavedWallet, value);
    }

    public void Dispose()
    {
        ActiveDexGateway = null;
        _sessionPrivateKey = null;
        PrivateKeyInput = string.Empty;
    }

    public bool CanTradeChainId(string? chainId)
    {
        if (!CanUseDexTradingOnSelectedNetwork)
        {
            return false;
        }

        var targetNetwork = MapChainIdToNetwork(chainId);
        return string.Equals(SelectedNetwork, targetNetwork, StringComparison.OrdinalIgnoreCase);
    }

    public void SyncGlobalQuoteAssetFromEffectiveSymbol(string? symbol)
    {
        var normalized = NormalizeEffectiveSymbolToGlobalSelection(symbol);
        if (!string.Equals(GlobalQuoteAssetSymbol, normalized, StringComparison.OrdinalIgnoreCase))
        {
            GlobalQuoteAssetSymbol = normalized;
        }
    }

    public bool TryApproveUsdRisk(decimal requestedSpendUsdt, decimal currentOpenExposureUsdt, decimal realizedPnlUsdt, out string reason)
    {
        if (requestedSpendUsdt <= 0m)
        {
            reason = "Requested size must be greater than zero.";
            return false;
        }

        if (requestedSpendUsdt > GlobalMaxSpendPerTradeUsdt)
        {
            reason = $"Risk cap hit: need {requestedSpendUsdt:0.##} USDT, max per trade is {GlobalMaxSpendPerTradeUsdt:0.##} USDT.";
            return false;
        }

        var dailyLossUsed = Math.Max(0m, -realizedPnlUsdt);
        if (dailyLossUsed >= GlobalMaxDailyLossUsdt)
        {
            reason = $"Risk cap hit: daily loss limit reached ({dailyLossUsed:0.##}/{GlobalMaxDailyLossUsdt:0.##} USDT).";
            return false;
        }

        if (currentOpenExposureUsdt + requestedSpendUsdt > GlobalMaxOpenExposureUsdt)
        {
            reason = $"Risk cap hit: open exposure would become {(currentOpenExposureUsdt + requestedSpendUsdt):0.##} USDT, max is {GlobalMaxOpenExposureUsdt:0.##} USDT.";
            return false;
        }

        reason = $"Risk caps clear: {requestedSpendUsdt:0.##} USDT fits trade, daily loss and exposure limits.";
        return true;
    }

    private void SelectProvider(string provider)
    {
        SelectedProvider = string.IsNullOrWhiteSpace(provider) ? "MetaMask" : provider;
        PushActivity($"Provider focus switched to {SelectedProvider}.");
    }

    private async Task ConnectWatchWalletAsync()
    {
        var address = WalletAddressInput.Trim();
        if (!ValidateAddress(address))
        {
            StatusMessage = $"'{address}' is not a valid {SelectedNetwork} wallet address.";
            return;
        }

        ConnectedAddress = address;
        ActiveDexGateway = null;
        _sessionPrivateKey = null;
        IsConnected = true;
        IsReadOnly = true;
        StatusMessage = $"{SelectedProvider} connected in watch mode for {SelectedNetwork}.";
        PushActivity($"Connected {WalletAddressShort} in watch mode.");
        _log?.Invoke($"Wallet connected in watch mode: {SelectedProvider} {WalletAddressShort} on {SelectedNetwork}");
        UpsertSavedWallet(isReadOnly: true, note: "Saved watch profile");
        await RefreshWalletAsync();
    }

    private async Task ImportPrivateKeyAsync()
    {
        var privateKey = PrivateKeyInput.Trim();
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            StatusMessage = string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase)
                ? "Paste Solana secret material to arm a trade-enabled Solana session."
                : IsTronNetworkSelected
                    ? "Paste a Tron private key to arm a trade-enabled TRC20 payment session."
                : "Paste an EVM private key to create a trade-enabled in-memory session.";
            return;
        }

        try
        {
            string address;
            var rpcUrl = GetCurrentNetwork().RpcUrl;
            string? normalizedSolanaSecret = null;

            if (string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase))
            {
                address = WalletAddressInput.Trim();
                if (!SolanaRpcClient.IsValidAddress(address))
                {
                    StatusMessage = "Paste a valid Solana wallet address before importing Solana secret material.";
                    return;
                }

                normalizedSolanaSecret = SolanaKeyMaterial.NormalizeSecret(privateKey);
            }
            else if (IsTronNetworkSelected)
            {
                address = TronAddressCodec.DeriveAddress(privateKey);
            }
            else
            {
                address = EvmWalletClient.DeriveAddress(privateKey);
            }

            ActiveDexGateway = string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase)
                ? new SolanaTradeGateway(address, normalizedSecretMaterial: normalizedSolanaSecret)
                : IsTronNetworkSelected
                    ? new TronTradeGateway(privateKey, rpcUrl)
                : GetCurrentNetwork().SupportsDexTrading
                    ? DEXGateway.CreateForNetwork(SelectedNetwork, privateKey, rpcUrl)
                    : null;

            _sessionPrivateKey = privateKey;
            PrivateKeyInput = string.Empty;
            ConnectedAddress = address;
            IsConnected = true;
            IsReadOnly = false;
            StatusMessage = $"{SelectedProvider} imported in-memory for {SelectedNetwork}.";
            PushActivity($"Trade-enabled wallet session armed for {WalletAddressShort}.");
            _log?.Invoke($"Wallet imported for trading: {SelectedProvider} {WalletAddressShort} on {SelectedNetwork}");
            UpsertSavedWallet(isReadOnly: false, note: "Trading profile saved without private key");
            await RefreshWalletAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Private key import failed: {ex.Message}";
        }
    }

    private async Task RefreshWalletAsync()
    {
        if (!IsConnected)
        {
            StatusMessage = "Connect a wallet first.";
            return;
        }

        try
        {
            if (string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase))
            {
                using var solanaClient = new SolanaRpcClient();
                NativeBalance = await solanaClient.GetNativeBalanceAsync(ConnectedAddress);
                LastSyncLocal = DateTime.Now;
                RebuildAssets(
                    $"{NativeBalance:N4} {GetCurrentNetwork().NativeSymbol}",
                    CanUseDexTradingOnSelectedNetwork
                        ? "Solana wallet sync is live and the session is armed for Jupiter-backed execution."
                        : "Solana wallet sync is live. Import a 64-byte keypair secret to arm execution.");
                StatusMessage = $"{SelectedProvider} Solana wallet synced successfully.";
                PushActivity($"Wallet synced. Native balance: {NativeBalance:N4} {GetCurrentNetwork().NativeSymbol}.");
                return;
            }

            if (IsTronNetworkSelected)
            {
                var tronClient = new TronWalletClient(rpcUrl: GetCurrentNetwork().RpcUrl);
                NativeBalance = await tronClient.GetNativeBalanceAsync(ConnectedAddress);
                TronTrc20Balance = ValidateAddress(TronTrc20ContractAddress)
                    ? await tronClient.GetTrc20BalanceAsync(ConnectedAddress, TronTrc20ContractAddress)
                    : 0m;
                LastSyncLocal = DateTime.Now;
                RebuildAssets(
                    $"{NativeBalance:N4} {GetCurrentNetwork().NativeSymbol}",
                    CanUseDexTradingOnSelectedNetwork
                        ? $"Ready for Tron SunSwap execution and TRC20 flows. Current {TronTrc20Symbol} balance: {TronTrc20Balance:0.######}."
                        : CanUseTronPayments
                            ? $"Ready for Tron TRC20 transfers. Current {TronTrc20Symbol} balance: {TronTrc20Balance:0.######}."
                            : "Watching the Tron wallet globally. Import the Tron private key to unlock SunSwap execution and TRC20 transfers.");
                StatusMessage = $"{SelectedProvider} Tron wallet synced successfully.";
                TronPaymentStatus = $"Token balance refreshed: {TronTrc20Balance:0.######} {TronTrc20Symbol}.";
                PushActivity($"Wallet synced. Native balance: {NativeBalance:N4} {GetCurrentNetwork().NativeSymbol}. {TronTrc20Symbol}: {TronTrc20Balance:0.######}.");
                return;
            }

            var walletClient = new EvmWalletClient(GetCurrentNetwork().RpcUrl);
            NativeBalance = await walletClient.GetNativeBalanceAsync(ConnectedAddress);
            LastSyncLocal = DateTime.Now;
            RebuildAssets(
                $"{NativeBalance:N4} {GetCurrentNetwork().NativeSymbol}",
                CanUseDexTradingOnSelectedNetwork
                    ? $"Ready for {SelectedNetwork} DEX execution. Live connectors: {ActiveDexGateway?.SupportedDexesLabel ?? "none"}."
                    : "Watching the wallet globally across the terminal.");
            StatusMessage = $"{SelectedProvider} wallet synced successfully.";
            PushActivity($"Wallet synced. Native balance: {NativeBalance:N4} {GetCurrentNetwork().NativeSymbol}.");
        }
        catch (Exception ex)
        {
            LastSyncLocal = DateTime.Now;
            RebuildAssets("Balance temporarily unavailable", "RPC refresh failed, but the shared wallet session is still active.");
            StatusMessage = $"Wallet sync failed: {ex.Message}";
            PushActivity("Wallet sync hit an RPC issue.");
        }
    }

    private async Task SendTronPaymentAsync()
    {
        if (!TryApproveLiveExecution("TRON payment", out var executionReason))
        {
            TronPaymentStatus = executionReason;
            PushActivity(executionReason);
            return;
        }

        if (ActiveDexGateway is not TronTradeGateway tronGateway || !IsTronNetworkSelected)
        {
            TronPaymentStatus = "Switch to an armed Tron wallet session before sending TRC20.";
            return;
        }

        if (!ValidateAddress(TronPaymentRecipient))
        {
            TronPaymentStatus = "Recipient is not a valid Tron address.";
            return;
        }

        if (!ValidateAddress(TronTrc20ContractAddress))
        {
            TronPaymentStatus = "TRC20 contract address is not valid for Tron.";
            return;
        }

        if (TronTrc20Amount <= 0m)
        {
            TronPaymentStatus = "Enter a TRC20 amount greater than zero.";
            return;
        }

        try
        {
            TronPaymentStatus = $"Sending {TronTrc20Amount:0.######} {TronTrc20Symbol} on Tron...";
            var result = await tronGateway.SendTrc20Async(
                TronTrc20ContractAddress,
                TronPaymentRecipient.Trim(),
                TronTrc20Amount,
                TronFeeLimitTrx);

            TronPaymentStatus = result.Confirmed
                ? $"TRC20 transfer confirmed: {result.TransactionId}"
                : $"TRC20 transfer broadcast: {result.TransactionId}. {result.Narrative}";
            PushActivity($"Tron TRC20 transfer sent. {TronTrc20Amount:0.######} {TronTrc20Symbol} -> {TronPaymentRecipient.Trim()} | tx {result.TransactionId}");
            await RefreshWalletAsync();
        }
        catch (Exception ex)
        {
            TronPaymentStatus = $"TRC20 transfer failed: {ex.Message}";
            PushActivity($"Tron TRC20 transfer failed: {ex.Message}");
        }
    }

    private async Task ApplyTronTokenPresetAsync(string preset)
    {
        if (string.Equals(preset, "USDT", StringComparison.OrdinalIgnoreCase))
        {
            TronTrc20Symbol = "USDT";
            TronTrc20ContractAddress = TronTradeGateway.DefaultUsdtContractAddress;
        }
        else
        {
            TronTrc20Symbol = "USDC";
            TronTrc20ContractAddress = TronTradeGateway.DefaultUsdcContractAddress;
        }

        TronPaymentStatus = $"Preset applied: {TronTrc20Symbol} on Tron.";
        PushActivity($"Tron payment preset switched to {TronTrc20Symbol}.");
        RaiseComputedWalletState();

        if (IsConnected && IsTronNetworkSelected)
        {
            await RefreshWalletAsync();
        }
    }

    private void ApplyGlobalQuoteAsset(string? symbol)
    {
        GlobalQuoteAssetSymbol = symbol ?? string.Empty;
        PersistSavedWallets();
    }

    private void ApplyGlobalPositionSizing(string? value)
    {
        if (!decimal.TryParse(value, out var percent))
        {
            return;
        }

        GlobalPositionSizingPercent = percent;
    }

    private async Task RunSolanaDiagnosticsAsync()
    {
        if (ActiveDexGateway is not SolanaTradeGateway solanaGateway ||
            !string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase))
        {
            SolanaDiagnosticsSummary = "Switch to an armed Solana wallet session before running diagnostics.";
            return;
        }

        IsRunningSolanaDiagnostics = true;
        SolanaDiagnosticSteps.Clear();
        SolanaDiagnosticsSummary = "Running Solana execution diagnostics...";

        try
        {
            var diagnostics = await solanaGateway.RunExecutionDiagnosticsAsync();
            SolanaDiagnosticsSummary = diagnostics.Summary;

            foreach (var step in diagnostics.Steps)
            {
                SolanaDiagnosticSteps.Add($"{(step.Success ? "[OK]" : "[FAIL]")} {step.Title}: {step.Narrative}");
            }

            StatusMessage = diagnostics.Summary;
            PushActivity(diagnostics.Summary);
        }
        catch (Exception ex)
        {
            SolanaDiagnosticsSummary = $"Solana diagnostics crashed: {ex.Message}";
            SolanaDiagnosticSteps.Add($"[FAIL] Diagnostics runner: {ex.Message}");
            StatusMessage = SolanaDiagnosticsSummary;
        }
        finally
        {
            IsRunningSolanaDiagnostics = false;
            RaiseComputedWalletState();
        }
    }

    private async Task RunTronDiagnosticsAsync()
    {
        if (ActiveDexGateway is not TronTradeGateway tronGateway || !IsTronNetworkSelected)
        {
            TronDiagnosticsSummary = "Switch to an armed Tron wallet session before running diagnostics.";
            return;
        }

        IsRunningTronDiagnostics = true;
        TronDiagnosticSteps.Clear();
        TronDiagnosticsSummary = "Running Tron execution diagnostics...";

        try
        {
            var diagnostics = await tronGateway.RunExecutionDiagnosticsAsync();
            TronDiagnosticsSummary = diagnostics.Summary;

            foreach (var step in diagnostics.Steps)
            {
                TronDiagnosticSteps.Add($"{(step.Success ? "[OK]" : "[FAIL]")} {step.Title}: {step.Narrative}");
            }

            StatusMessage = diagnostics.Summary;
            PushActivity(diagnostics.Summary);
        }
        catch (Exception ex)
        {
            TronDiagnosticsSummary = $"Tron diagnostics crashed: {ex.Message}";
            TronDiagnosticSteps.Add($"[FAIL] Diagnostics runner: {ex.Message}");
            StatusMessage = TronDiagnosticsSummary;
            PushActivity(TronDiagnosticsSummary);
        }
        finally
        {
            IsRunningTronDiagnostics = false;
            RaiseComputedWalletState();
        }
    }

    private void DisconnectWallet()
    {
        ActiveDexGateway = null;
        _sessionPrivateKey = null;
        ConnectedAddress = string.Empty;
        IsConnected = false;
        IsReadOnly = true;
        NativeBalance = 0;
        LastSyncLocal = DateTime.MinValue;
        StatusMessage = "Wallet disconnected. The terminal is back in public-data mode.";
        SeedPreviewCards();
        PushActivity("Wallet session disconnected.");
        _log?.Invoke("Wallet disconnected.");
    }

    private async Task LoadSavedWalletAsync(SavedWalletViewModel? savedWallet)
    {
        if (savedWallet is null)
        {
            return;
        }

        SelectedSavedWallet = savedWallet;
        SelectedProvider = savedWallet.Provider;
        SelectedNetwork = savedWallet.Network;
        WalletAddressInput = savedWallet.Address;

        if (savedWallet.IsReadOnly)
        {
            await ConnectWatchWalletAsync();
            return;
        }

        ConnectedAddress = savedWallet.Address;
        IsConnected = true;
        IsReadOnly = true;
        ActiveDexGateway = null;
        StatusMessage = "Saved trading profile restored in safe watch mode. Re-enter the private key if you want to enable execution again.";
        PushActivity($"Loaded saved wallet profile {savedWallet.DisplayAddress}.");
        await RefreshWalletAsync();
    }

    private void DeleteSavedWallet(SavedWalletViewModel? savedWallet)
    {
        if (savedWallet is null)
        {
            return;
        }

        if (SelectedSavedWallet == savedWallet)
        {
            SelectedSavedWallet = null;
        }

        SavedWallets.Remove(savedWallet);
        PersistSavedWallets();
        StatusMessage = $"Removed saved wallet {savedWallet.DisplayAddress} from local memory.";
        PushActivity($"Deleted saved wallet profile {savedWallet.DisplayAddress}.");
    }

    private Task OpenProviderAsync()
    {
        var url = IsConnected && !string.IsNullOrWhiteSpace(ConnectedAddress)
            ? ExplorerUrl
            : ProviderLaunchUrls.TryGetValue(SelectedProvider, out var providerUrl)
                ? providerUrl
                : "https://metamask.io/download/";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            PushActivity($"Opened {SelectedProvider} route.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open provider route: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    private async Task TryBootstrapFromEnvironmentAsync()
    {
        var privateKey = Environment.GetEnvironmentVariable("CRYPTOAI_DEX_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            return;
        }

        SelectedProvider = "Session Key";
        SelectedNetwork = "BSC";
        PrivateKeyInput = privateKey;
        await ImportPrivateKeyAsync();
    }

    private void UpsertSavedWallet(bool isReadOnly, string note)
    {
        if (string.IsNullOrWhiteSpace(ConnectedAddress))
        {
            return;
        }

        var existing = SavedWallets.FirstOrDefault(item =>
            string.Equals(item.Address, ConnectedAddress, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Network, SelectedNetwork, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new SavedWalletViewModel();
            SavedWallets.Insert(0, existing);
        }

        existing.Provider = SelectedProvider;
        existing.Network = SelectedNetwork;
        existing.Address = ConnectedAddress;
        existing.IsReadOnly = isReadOnly;
        existing.Note = note;
        SelectedSavedWallet = existing;
        PersistSavedWallets();
    }

    private void LoadSavedWalletsFromDisk()
    {
        if (!File.Exists(_storageFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_storageFilePath);
            var snapshot = JsonSerializer.Deserialize<WalletStorageSnapshot>(json);
            var items = snapshot?.Wallets;

            if (items is null)
            {
                items = JsonSerializer.Deserialize<List<SavedWalletStorageItem>>(json) ?? [];
            }

            if (!string.IsNullOrWhiteSpace(snapshot?.GlobalQuoteAssetSymbol))
            {
                _globalQuoteAssetSymbol = NormalizeGlobalQuoteAssetSymbol(snapshot.GlobalQuoteAssetSymbol);
            }

            if (snapshot?.GlobalPositionSizingPercent > 0m)
            {
                _globalPositionSizingPercent = Math.Clamp(decimal.Round(snapshot.GlobalPositionSizingPercent.Value, 0, MidpointRounding.AwayFromZero), 1m, 100m);
            }

            if (snapshot?.GlobalMaxSpendPerTradeUsdt > 0m)
            {
                _globalMaxSpendPerTradeUsdt = Math.Max(1m, snapshot.GlobalMaxSpendPerTradeUsdt.Value);
            }

            if (snapshot?.GlobalMaxDailyLossUsdt > 0m)
            {
                _globalMaxDailyLossUsdt = Math.Max(1m, snapshot.GlobalMaxDailyLossUsdt.Value);
            }

            if (snapshot?.GlobalMaxOpenExposureUsdt > 0m)
            {
                _globalMaxOpenExposureUsdt = Math.Max(1m, snapshot.GlobalMaxOpenExposureUsdt.Value);
            }

            if (snapshot?.GlobalPaperOnlyMode is not null)
            {
                _globalPaperOnlyMode = snapshot.GlobalPaperOnlyMode.Value;
            }

            SavedWallets.Clear();
            foreach (var item in items)
            {
                SavedWallets.Add(new SavedWalletViewModel
                {
                    Provider = item.Provider,
                    Network = item.Network,
                    Address = item.Address,
                    IsReadOnly = item.IsReadOnly,
                    Note = item.Note
                });
            }
        }
        catch (Exception ex)
        {
            PushActivity($"Saved wallet storage could not be loaded: {ex.Message}");
        }
    }

    private void PersistSavedWallets()
    {
        try
        {
            var items = SavedWallets
                .Select(item => new SavedWalletStorageItem(
                    item.Provider,
                    item.Network,
                    item.Address,
                    item.IsReadOnly,
                    item.Note))
                .ToList();

            var snapshot = new WalletStorageSnapshot(
                GlobalQuoteAssetSymbol,
                GlobalPositionSizingPercent,
                GlobalMaxSpendPerTradeUsdt,
                GlobalMaxDailyLossUsdt,
                GlobalMaxOpenExposureUsdt,
                GlobalPaperOnlyMode,
                items);

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_storageFilePath, json);
        }
        catch (Exception ex)
        {
            PushActivity($"Saved wallet storage could not be written: {ex.Message}");
        }
    }

    private void SeedPreviewCards()
    {
        Assets.Clear();
        Assets.Add(new WalletAssetViewModel
        {
            Title = "Global session",
            Value = "Not connected",
            Subtitle = "Connect one wallet and let the same identity flow through the app."
        });
        Assets.Add(new WalletAssetViewModel
        {
            Title = "DEX execution",
            Value = "Locked",
            Subtitle = "Import a session key to unlock EVM trading, Solana execution, or Tron TRC20 payments."
        });
        Assets.Add(new WalletAssetViewModel
        {
            Title = "Explorer route",
            Value = GetCurrentNetwork().ExplorerBaseUrl,
            Subtitle = "One click opens the current provider or the live address explorer."
        });
    }

    private void RebuildAssets(string balanceValue, string tradingSubtitle)
    {
        Assets.Clear();
        Assets.Add(new WalletAssetViewModel
        {
            Title = $"{GetCurrentNetwork().NativeSymbol} balance",
            Value = balanceValue,
            Subtitle = $"Provider: {SelectedProvider} · Network: {SelectedNetwork}"
        });
        Assets.Add(new WalletAssetViewModel
        {
            Title = "Wallet mode",
            Value = WalletModeLabel,
            Subtitle = tradingSubtitle
        });
        Assets.Add(new WalletAssetViewModel
        {
            Title = "Explorer route",
            Value = ExplorerUrl,
            Subtitle = "Use OPEN PROVIDER to jump into the live external wallet or explorer view."
        });
    }

    private bool ValidateAddress(string address)
    {
        if (string.Equals(SelectedNetwork, "Solana", StringComparison.OrdinalIgnoreCase))
        {
            return SolanaRpcClient.IsValidAddress(address);
        }

        if (IsTronNetworkSelected)
        {
            return TronAddressCodec.IsValidAddress(address);
        }

        return EvmWalletClient.IsValidAddress(address);
    }

    private WalletNetworkDefinition GetCurrentNetwork()
    {
        return NetworkDefinitions.TryGetValue(SelectedNetwork, out var network)
            ? network
            : NetworkDefinitions["BSC"];
    }

    private static string MapChainIdToNetwork(string? chainId)
    {
        return chainId?.Trim().ToLowerInvariant() switch
        {
            "bsc" => "BSC",
            "ethereum" => "Ethereum",
            "base" => "Base",
            "solana" => "Solana",
            "tron" => "Tron",
            _ => string.Empty
        };
    }

    private void PushActivity(string message)
    {
        RecentActivity.Insert(0, $"{DateTime.Now:HH:mm:ss} · {message}");
        while (RecentActivity.Count > 10)
        {
            RecentActivity.RemoveAt(RecentActivity.Count - 1);
        }
    }

    private void RaiseComputedWalletState()
    {
        this.RaisePropertyChanged(nameof(CanUseDexTradingOnSelectedNetwork));
        this.RaisePropertyChanged(nameof(CanUseDexTradingOnBsc));
        this.RaisePropertyChanged(nameof(WalletAddressShort));
        this.RaisePropertyChanged(nameof(ActiveWalletBanner));
        this.RaisePropertyChanged(nameof(ConnectionSummary));
        this.RaisePropertyChanged(nameof(NativeBalanceLabel));
        this.RaisePropertyChanged(nameof(WalletModeLabel));
        this.RaisePropertyChanged(nameof(WalletReadinessLabel));
        this.RaisePropertyChanged(nameof(WalletReadinessBrush));
        this.RaisePropertyChanged(nameof(WalletReadinessSummary));
        this.RaisePropertyChanged(nameof(RouteReadinessLabel));
        this.RaisePropertyChanged(nameof(RouteReadinessBrush));
        this.RaisePropertyChanged(nameof(RouteReadinessSummary));
        this.RaisePropertyChanged(nameof(WalletCapabilityText));
        this.RaisePropertyChanged(nameof(NetworkExecutionMatrix));
        this.RaisePropertyChanged(nameof(NetworkExecutionNotes));
        this.RaisePropertyChanged(nameof(CanRunSolanaDiagnostics));
        this.RaisePropertyChanged(nameof(CanRunTronDiagnostics));
        this.RaisePropertyChanged(nameof(IsTronNetworkSelected));
        this.RaisePropertyChanged(nameof(CanUseTronPayments));
        this.RaisePropertyChanged(nameof(CanSendTronPaymentLive));
        this.RaisePropertyChanged(nameof(TronPaymentBlockedReason));
        this.RaisePropertyChanged(nameof(TronTrc20BalanceLabel));
        this.RaisePropertyChanged(nameof(IsTronUsdtSelected));
        this.RaisePropertyChanged(nameof(IsTronUsdcSelected));
        this.RaisePropertyChanged(nameof(TronUsdtPresetBackground));
        this.RaisePropertyChanged(nameof(TronUsdcPresetBackground));
        this.RaisePropertyChanged(nameof(TronUsdtPresetForeground));
        this.RaisePropertyChanged(nameof(TronUsdcPresetForeground));
        this.RaisePropertyChanged(nameof(ExplorerUrl));
        this.RaisePropertyChanged(nameof(EffectiveGlobalQuoteAssetSymbol));
        this.RaisePropertyChanged(nameof(GlobalQuoteAssetModeLabel));
        this.RaisePropertyChanged(nameof(GlobalQuoteAssetModeBrush));
        this.RaisePropertyChanged(nameof(GlobalQuoteAssetSummary));
        this.RaisePropertyChanged(nameof(GlobalQuotePersistenceLabel));
        this.RaisePropertyChanged(nameof(GlobalQuoteFallbackLabel));
        this.RaisePropertyChanged(nameof(GlobalQuoteTabBadgeLabel));
        this.RaisePropertyChanged(nameof(GlobalPaperOnlyMode));
        this.RaisePropertyChanged(nameof(GlobalLiveExecutionEnabled));
        this.RaisePropertyChanged(nameof(GlobalExecutionModeLabel));
        this.RaisePropertyChanged(nameof(GlobalExecutionModeBrush));
        this.RaisePropertyChanged(nameof(GlobalExecutionSummary));
        this.RaisePropertyChanged(nameof(GlobalExecutionPersistenceLabel));
        this.RaisePropertyChanged(nameof(GlobalExecutionGuardStatusLabel));
        this.RaisePropertyChanged(nameof(GlobalPositionSizingPercent));
        this.RaisePropertyChanged(nameof(GlobalPositionSizingLabel));
        this.RaisePropertyChanged(nameof(GlobalPositionSizingSummary));
        this.RaisePropertyChanged(nameof(GlobalMaxSpendPerTradeUsdt));
        this.RaisePropertyChanged(nameof(GlobalMaxDailyLossUsdt));
        this.RaisePropertyChanged(nameof(GlobalMaxOpenExposureUsdt));
        this.RaisePropertyChanged(nameof(GlobalRiskCapLabel));
        this.RaisePropertyChanged(nameof(GlobalRiskSummary));
    }

    private string ResolveEffectiveGlobalQuoteAssetSymbol()
    {
        var options = (ActiveDexGateway?.SupportedQuoteAssets ?? DexQuoteAssetCatalog.GetOptions(SelectedNetwork))
            .Select(static asset => asset.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nativeSymbol = GetCurrentNetwork().NativeSymbol.ToUpperInvariant();

        if (options.Count == 0)
        {
            return nativeSymbol;
        }

        if (string.Equals(GlobalQuoteAssetSymbol, "NATIVE", StringComparison.OrdinalIgnoreCase))
        {
            return options.FirstOrDefault(option => string.Equals(option, nativeSymbol, StringComparison.OrdinalIgnoreCase))
                ?? nativeSymbol;
        }

        if (options.Contains(GlobalQuoteAssetSymbol, StringComparer.OrdinalIgnoreCase))
        {
            return options.First(option => string.Equals(option, GlobalQuoteAssetSymbol, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(GlobalQuoteAssetSymbol, "USDT", StringComparison.OrdinalIgnoreCase) &&
            options.Contains("USDC", StringComparer.OrdinalIgnoreCase))
        {
            return "USDC";
        }

        if (string.Equals(GlobalQuoteAssetSymbol, "USDC", StringComparison.OrdinalIgnoreCase) &&
            options.Contains("USDT", StringComparer.OrdinalIgnoreCase))
        {
            return "USDT";
        }

        return options.FirstOrDefault(option => string.Equals(option, nativeSymbol, StringComparison.OrdinalIgnoreCase))
            ?? options[0];
    }

    private string NormalizeGlobalQuoteAssetSymbol(string? symbol)
    {
        var normalized = string.IsNullOrWhiteSpace(symbol) ? "USDT" : symbol.Trim().ToUpperInvariant();
        return normalized switch
        {
            "USDT" => "USDT",
            "USDC" => "USDC",
            "NATIVE" => "NATIVE",
            _ => NormalizeEffectiveSymbolToGlobalSelection(normalized)
        };
    }

    private void ApplyGlobalExecutionMode(string? mode)
    {
        var wantsLive = string.Equals(mode, "LIVE", StringComparison.OrdinalIgnoreCase);
        // Without a valid license, live mode cannot be enabled — pin to paper.
        GlobalPaperOnlyMode = !(wantsLive && LicenseAllowsLive);
    }

    private string NormalizeEffectiveSymbolToGlobalSelection(string? symbol)
    {
        var normalized = string.IsNullOrWhiteSpace(symbol) ? GetCurrentNetwork().NativeSymbol : symbol.Trim().ToUpperInvariant();
        return string.Equals(normalized, GetCurrentNetwork().NativeSymbol, StringComparison.OrdinalIgnoreCase)
            ? "NATIVE"
            : normalized switch
            {
                "USDT" => "USDT",
                "USDC" => "USDC",
                _ => "NATIVE"
            };
    }

    private sealed record WalletNetworkDefinition(
        string Name,
        string NativeSymbol,
        string RpcUrl,
        string ExplorerBaseUrl,
        bool SupportsDexTrading);

    private sealed record WalletStorageSnapshot(
        string GlobalQuoteAssetSymbol,
        decimal? GlobalPositionSizingPercent,
        decimal? GlobalMaxSpendPerTradeUsdt,
        decimal? GlobalMaxDailyLossUsdt,
        decimal? GlobalMaxOpenExposureUsdt,
        bool? GlobalPaperOnlyMode,
        List<SavedWalletStorageItem> Wallets);

    private sealed record SavedWalletStorageItem(
        string Provider,
        string Network,
        string Address,
        bool IsReadOnly,
        string Note);
}
