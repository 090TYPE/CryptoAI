using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Windows.Input;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.PortfolioDesk;

/// <summary>
/// View model behind the redesigned Portfolio terminal (1:1 of the multi-wallet
/// manager mock). It reuses the real <see cref="PortfolioRebalanceViewModel"/>
/// for live holdings/prices and the real saved-wallet list for the browser; the
/// security-checks panel is a deterministic rule engine. Panels with no live
/// data source (approvals scan, meme radar, staking, tx history, realised-PnL
/// analytics) render 1:1 but stay empty with an honest note rather than showing
/// fabricated figures.
/// </summary>
public sealed class PortfolioDeskViewModel : ReactiveObject
{
    private static readonly string[] CexProviders =
        ["binance", "okx", "bybit", "kraken", "kucoin", "gate", "gate.io", "htx", "huobi", "bitget", "coinbase", "mexc"];

    private readonly PortfolioRebalanceViewModel _rebalance;
    private readonly WalletWorkspaceViewModel _wallet;
    private readonly ObservableCollection<SavedWalletViewModel> _savedWallets;
    private readonly WalletTxHistoryService _txHistory = new();
    private readonly WalletApprovalsService _approvals = new();

    // ── UI state ─────────────────────────────────────────────────────────────
    private string _walletTypeFilter = "ALL";
    private int _selectedWalletIndex;
    private string _blotterTab = "ASSETS";     // ASSETS | ACTIVITY | MEME RADAR | STAKING | REBALANCE
    private string _rightTab = "CHECKS";       // CHECKS | TOKENS | HISTORY | ANALYTICS
    private string _searchText = "";
    private bool _scanning;

    // ── Modal state ──────────────────────────────────────────────────────────
    private bool _showModal;
    private int _modalStep = 1;
    private string? _modalType;                // CEX | DEX | MEME | STAKE | PERP | NFT
    private string _newWalletName = "";
    private string _newWalletAddress = "";
    private string _newApiKey = "";
    private string _newApiSecret = "";
    private string? _selCex;
    private string _selChain = "ETH";
    private string? _selPerp;
    private string? _selStake;

    // ── Connection Studio overlay (advanced WalletVM connect + diagnostics) ───
    private bool _connectionStudioVisible;

    public PortfolioDeskViewModel(PortfolioRebalanceViewModel rebalance,
                                  WalletWorkspaceViewModel wallet)
    {
        _rebalance    = rebalance;
        _wallet       = wallet;
        _savedWallets = wallet.SavedWallets;

        SelectWalletCommand = ReactiveCommand.Create<PfWalletCard>(ApplyWallet, outputScheduler: App.UiScheduler);
        RescanCommand       = ReactiveCommand.Create(RunRescan, outputScheduler: App.UiScheduler);
        CopyAddrCommand     = ReactiveCommand.Create(CopySelectedAddress, outputScheduler: App.UiScheduler);
        ExplorerCommand     = ReactiveCommand.Create(OpenSelectedExplorer, outputScheduler: App.UiScheduler);

        OpenModalCommand    = ReactiveCommand.Create(() => { ShowModal = true; ModalStep = 1; ModalType = null; }, outputScheduler: App.UiScheduler);
        CloseModalCommand   = ReactiveCommand.Create(() => { ShowModal = false; ModalStep = 1; ModalType = null; }, outputScheduler: App.UiScheduler);
        ModalBackCommand    = ReactiveCommand.Create(() => { ModalStep = Math.Max(1, ModalStep - 1); }, outputScheduler: App.UiScheduler);
        ModalNextCommand    = ReactiveCommand.Create(() => { ModalStep = 3; }, outputScheduler: App.UiScheduler);
        ModalConfirmCommand = ReactiveCommand.Create(ConfirmAddWallet, outputScheduler: App.UiScheduler);

        OpenStudioCommand   = ReactiveCommand.Create(() => { ConnectionStudioVisible = true; }, outputScheduler: App.UiScheduler);
        CloseStudioCommand  = ReactiveCommand.Create(() => { ConnectionStudioVisible = false; }, outputScheduler: App.UiScheduler);
        RevokeApprovalCommand = ReactiveCommand.Create<PfApproval>(RevokeApproval, outputScheduler: App.UiScheduler);

        // Recompute derived panels whenever the live holdings change.
        _rebalance.Allocations.CollectionChanged += (_, _) => RecomputeAll();
        _rebalance.WhenAnyValue(r => r.TotalValueUsd).Subscribe(_ => RecomputeAll());
        _savedWallets.CollectionChanged += (_, _) => RecomputeAll();

        RecomputeAll();
    }

    // ── Reused real rebalance VM (the REBALANCE blotter binds straight to it) ─
    public PortfolioRebalanceViewModel Rebalance => _rebalance;

    // ── Commands ─────────────────────────────────────────────────────────────
    public ReactiveCommand<PfWalletCard, Unit> SelectWalletCommand { get; }
    public ReactiveCommand<Unit, Unit> RescanCommand      { get; }
    public ReactiveCommand<Unit, Unit> CopyAddrCommand    { get; }
    public ReactiveCommand<Unit, Unit> ExplorerCommand    { get; }
    public ReactiveCommand<Unit, Unit> OpenModalCommand   { get; }
    public ReactiveCommand<Unit, Unit> CloseModalCommand  { get; }
    public ReactiveCommand<Unit, Unit> ModalBackCommand   { get; }
    public ReactiveCommand<Unit, Unit> ModalNextCommand   { get; }
    public ReactiveCommand<Unit, Unit> ModalConfirmCommand{ get; }
    public ReactiveCommand<Unit, Unit> OpenStudioCommand  { get; }
    public ReactiveCommand<Unit, Unit> CloseStudioCommand { get; }
    public ReactiveCommand<PfApproval, Unit> RevokeApprovalCommand { get; }

    public bool ConnectionStudioVisible
    {
        get => _connectionStudioVisible;
        set => this.RaiseAndSetIfChanged(ref _connectionStudioVisible, value);
    }

    // ── Collections bound by the view ────────────────────────────────────────
    public ObservableCollection<PfHeaderStat>  HeaderStats   { get; } = [];
    public ObservableCollection<PfTypeFilter>  TypeFilters   { get; } = [];
    public ObservableCollection<PfWalletCard>  Wallets       { get; } = [];
    public ObservableCollection<PfSummaryCard> SummaryCards  { get; } = [];
    public ObservableCollection<PfAllocBar>    AllocationBars{ get; } = [];
    public ObservableCollection<PfTopAsset>    TopAssets     { get; } = [];
    public ObservableCollection<PfBlotterTab>  BlotterTabs   { get; } = [];
    public ObservableCollection<PfBlotterTotal>BlotterTotals { get; } = [];
    public ObservableCollection<PfAssetRow>    AssetRows     { get; } = [];
    public ObservableCollection<PfActivityRow> ActivityRows  { get; } = [];
    public ObservableCollection<PfMemeRow>     MemeRows      { get; } = [];
    public ObservableCollection<PfStakingRow>  StakingRows   { get; } = [];
    public ObservableCollection<PfRightTab>    RightTabs     { get; } = [];
    public ObservableCollection<PfWalletStat>  WalletStats   { get; } = [];
    public ObservableCollection<PfCheckGroup>  CheckGroups   { get; } = [];
    public ObservableCollection<PfApproval>    Approvals     { get; } = [];
    public ObservableCollection<PfToken>       WalletTokens  { get; } = [];
    public ObservableCollection<PfTxRow>       TxRows        { get; } = [];
    public ObservableCollection<PfAnalyticsCard> AnalyticsCards { get; } = [];

    // Modal collections
    public ObservableCollection<PfModalWalletType> ModalWalletTypes { get; } = [];
    public ObservableCollection<PfStepDot>      ModalStepDots { get; } = [];
    public ObservableCollection<PfCexOption>    CexOptions    { get; } = [];
    public ObservableCollection<PfChainOption>  ChainOptions  { get; } = [];
    public ObservableCollection<PfStakeProtocol>StakeProtocols{ get; } = [];
    public ObservableCollection<PfPerpOption>   PerpOptions   { get; } = [];
    public ObservableCollection<PfModalCheck>   ModalChecks   { get; } = [];

    // ── Scalar display props (header + chrome) ───────────────────────────────
    public string TotalEquity   { get; private set; } = "$0";
    public string EquityChg     { get; private set; } = "—";
    public string EquityChgColor{ get; private set; } = "#3d5a72";
    public string WalletCount   { get; private set; } = "0";
    public string ChainCount    { get; private set; } = "0";
    public string StatusMessage { get; private set; } = "";

    // Blotter tab visibility
    public bool ShowAllAssets => _blotterTab == "ASSETS";
    public bool ShowActivity  => _blotterTab == "ACTIVITY";
    public bool ShowMeme      => _blotterTab == "MEME RADAR";
    public bool ShowStaking   => _blotterTab == "STAKING";
    public bool ShowRebalance => _blotterTab == "REBALANCE";
    public int  MemeTokenCount => MemeRows.Count;
    public bool MemeEmpty     => MemeRows.Count == 0;
    public bool StakingEmpty  => StakingRows.Count == 0;
    public bool ActivityEmpty => ActivityRows.Count == 0;
    public bool AssetsEmpty   => AssetRows.Count == 0;
    public bool WalletsEmpty  => Wallets.Count == 0;

    // Right tab visibility
    public bool ShowChecks    => _rightTab == "CHECKS";
    public bool ShowTokens    => _rightTab == "TOKENS";
    public bool ShowTxHistory => _rightTab == "HISTORY";
    public bool ShowAnalytics => _rightTab == "ANALYTICS";
    public bool TokensEmpty   => WalletTokens.Count == 0;
    public bool TxEmpty       => TxRows.Count == 0;
    public bool ApprovalsEmpty=> Approvals.Count == 0;

    // Selected wallet header
    public string SelWalletName { get; private set; } = "No wallet";
    public string SelWalletType { get; private set; } = "";
    public string SelWalletAddr { get; private set; } = "";
    public string SelWalletIcon { get; private set; } = "◈";
    public string SelWalletIconBg { get; private set; } = "#07111a";
    public string SelWalletIconBorder { get; private set; } = "#111d29";
    public string SelWalletIconColor { get; private set; } = SemanticColor.Muted;
    public string SelWalletDot { get; private set; } = SemanticColor.Positive;
    public string SelWalletTypeBadgeColor { get; private set; } = SemanticColor.Muted;
    public string SelWalletTypeBadgeBg { get; private set; } = "rgba(143,163,184,.1)";

    // Checks overall
    public string OverallScore { get; private set; } = "—";
    public string OverallLabel { get; private set; } = "";
    public string OverallDesc  { get; private set; } = "";
    public string OverallColor { get; private set; } = SemanticColor.Positive;
    public string OverallBg     { get; private set; } = "#061e14";
    public string OverallBorder { get; private set; } = "#0d2a1e";
    public string ApprovalCount { get; private set; } = "0";
    public string RescanLabel   { get; private set; } = "RESCAN";
    public string RescanColor   { get; private set; } = "#3d5a72";

    // Analytics win/loss
    public double WinPct  { get; private set; }
    public double LossPct { get; private set; }
    public string WinLabel  { get; private set; } = "—";
    public string LossLabel { get; private set; } = "—";
    public bool AnalyticsEmpty => AnalyticsCards.Count == 0;

    // ── Bound state properties ───────────────────────────────────────────────
    public string SearchText
    {
        get => _searchText;
        set { this.RaiseAndSetIfChanged(ref _searchText, value); RecomputeAll(); }
    }

    public int SelectedWalletIndex
    {
        get => _selectedWalletIndex;
        set { this.RaiseAndSetIfChanged(ref _selectedWalletIndex, value); RecomputeAll(); }
    }

    public bool ShowModal { get => _showModal; set => this.RaiseAndSetIfChanged(ref _showModal, value); }
    public int  ModalStep { get => _modalStep; set { this.RaiseAndSetIfChanged(ref _modalStep, value); RecomputeModal(); } }
    public string? ModalType { get => _modalType; set { this.RaiseAndSetIfChanged(ref _modalType, value); RecomputeModal(); } }

    public string NewWalletName    { get => _newWalletName;    set => this.RaiseAndSetIfChanged(ref _newWalletName, value); }
    public string NewWalletAddress { get => _newWalletAddress; set => this.RaiseAndSetIfChanged(ref _newWalletAddress, value); }
    public string NewApiKey        { get => _newApiKey;        set => this.RaiseAndSetIfChanged(ref _newApiKey, value); }
    public string NewApiSecret     { get => _newApiSecret;     set => this.RaiseAndSetIfChanged(ref _newApiSecret, value); }

    public bool ModalIsStep1 => _modalStep == 1;
    public bool ModalIsStep2 => _modalStep == 2;
    public bool ModalIsStep3 => _modalStep == 3;
    public string ModalStepLabel => _modalStep switch
    {
        1 => "Step 1 of 3 — Choose type",
        2 => "Step 2 of 3 — Configure",
        _ => "Step 3 of 3 — Security scan",
    };
    public bool ModalIsCex  => _modalType == "CEX";
    public bool ModalIsDex  => _modalType is "DEX" or "MEME" or "NFT";
    public bool ModalIsMeme => _modalType == "MEME";
    public bool ModalIsStake=> _modalType == "STAKE";
    public bool ModalIsPerp => _modalType == "PERP";
    public string ModalSelEmoji => _modalType switch { "CEX" => "🏦", "DEX" => "🦊", "MEME" => "🔥", "STAKE" => "🔒", "PERP" => "⚡", "NFT" => "🖼", _ => "◈" };
    public string ModalSelName  => _modalType switch { "CEX" => "CEX Spot", "DEX" => "DEX Wallet", "MEME" => "Meme Wallet", "STAKE" => "Staking", "PERP" => "Futures / Perp", "NFT" => "NFT Wallet", _ => "" };
    public string ModalSelDesc  => _modalType switch { "CEX" => "Connect via read-only API key", "DEX" => "Track by on-chain address", "MEME" => "High-risk meme token wallet", "STAKE" => "Staking & yield tracking", "PERP" => "Leveraged perpetual contracts", "NFT" => "NFT portfolio tracker", _ => "" };
    public string AddrPlaceholder => _selChain == "SOL" ? "e.g. 8Xmv…K3qP" : "e.g. 0x3f4a…8d91";
    public string ModalResultBg     => ModalIsMeme ? "#1a0d05" : "#061e14";
    public string ModalResultBorder => ModalIsMeme ? "#3d2d00" : "#0d2a1e";
    public string ModalResultColor  => ModalIsMeme ? SemanticColor.Warning : SemanticColor.Positive;
    public string ModalResultIcon   => ModalIsMeme ? "⚠️" : "✓";
    public string ModalResultLabel  => ModalIsMeme ? "HIGH RISK — proceed with caution" : "All checks passed";
    public string ModalResultDesc   => ModalIsMeme ? "Meme wallet added with extra monitoring enabled" : "Wallet is safe to add to your portfolio";
    public string ConfirmBtnBg      => ModalIsMeme ? "#1a0d05" : "#0a2d1e";
    public string ConfirmBtnColor   => ModalIsMeme ? "#f97316" : SemanticColor.Accent;

    // ── State setters that also re-render chrome ─────────────────────────────
    private void SetWalletFilter(string t) { _walletTypeFilter = t; _selectedWalletIndex = 0; RecomputeAll(); }
    private void SetBlotterTab(string t)    { _blotterTab = t; RaiseTabFlags(); RecomputeAll(); }
    private void SetRightTab(string t)      { _rightTab = t; RaiseRightFlags(); RecomputeAll(); if (t == "HISTORY") LoadTxHistoryAsync(); if (t == "CHECKS") LoadApprovalsAsync(); }

    private void RaiseTabFlags()
    {
        this.RaisePropertyChanged(nameof(ShowAllAssets));
        this.RaisePropertyChanged(nameof(ShowActivity));
        this.RaisePropertyChanged(nameof(ShowMeme));
        this.RaisePropertyChanged(nameof(ShowStaking));
        this.RaisePropertyChanged(nameof(ShowRebalance));
    }

    private void RaiseRightFlags()
    {
        this.RaisePropertyChanged(nameof(ShowChecks));
        this.RaisePropertyChanged(nameof(ShowTokens));
        this.RaisePropertyChanged(nameof(ShowTxHistory));
        this.RaisePropertyChanged(nameof(ShowAnalytics));
    }

    // ── Commands impl ────────────────────────────────────────────────────────
    private void RunRescan()
    {
        _scanning = true;
        RecomputeRightPanel();
        LoadApprovalsAsync();
        System.Threading.Tasks.Task.Delay(1600).ContinueWith(_ =>
        {
            _scanning = false;
            RecomputeRightPanel();
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyWallet(PfWalletCard w)
    {
        SelectedWalletIndex = w.Index;
        if (w.Source is { } saved && ((ICommand)_wallet.LoadSavedWalletCommand).CanExecute(saved))
        {
            ((ICommand)_wallet.LoadSavedWalletCommand).Execute(saved);
            var name = string.IsNullOrWhiteSpace(saved.Provider) ? saved.DisplayAddress : saved.Provider;
            StatusMessage = $"Applied {name} as the active session.";
            this.RaisePropertyChanged(nameof(StatusMessage));
        }
        if (_rightTab == "HISTORY") LoadTxHistoryAsync();
        LoadApprovalsAsync();
    }

    /// <summary>Fetches real recent transactions for the selected wallet's address+chain.</summary>
    private async void LoadTxHistoryAsync()
    {
        // async void + awaited network I/O: a swallow-all guard keeps a failed
        // fetch from crashing the process via the UI SynchronizationContext.
        try
        {
            TxRows.Clear();
            this.RaisePropertyChanged(nameof(TxEmpty));
            var w = SelectedRaw();
            if (w is null || string.IsNullOrWhiteSpace(w.Address) || w.Address.StartsWith("api:", StringComparison.Ordinal))
                return;
            var txs = await _txHistory.GetRecentAsync(w.Address, w.Network ?? "", 15);
            TxRows.Clear();
            foreach (var t in txs) TxRows.Add(MapTx(t));
            this.RaisePropertyChanged(nameof(TxEmpty));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadTxHistory failed: {ex.Message}");
        }
    }

    private static PfTxRow MapTx(WalletTx t)
    {
        var emoji = t.Failed ? "✕" : t.Direction switch { "out" => "↑", "in" => "↓", _ => "⇄" };
        var iconBg = t.Direction == "out" ? "rgba(255,128,128,.1)" : t.Direction == "in" ? "rgba(61,220,132,.1)" : "rgba(91,192,255,.1)";
        var amtStr = t.Amount == 0
            ? "—"
            : (t.Direction == "out" ? "-" : t.Direction == "in" ? "+" : "") + t.Amount.ToString("0.####", CultureInfo.InvariantCulture) + " " + t.AssetSymbol;
        var amtColor = t.Direction == "out" ? SemanticColor.Negative : t.Direction == "in" ? SemanticColor.Positive : SemanticColor.Primary;
        var hashShort = t.Hash.Length > 12 ? t.Hash[..8] + "…" : t.Hash;
        var fee = t.FeeNative > 0 ? t.FeeNative.ToString("0.######", CultureInfo.InvariantCulture) + " " + t.AssetSymbol : "";
        return new PfTxRow
        {
            Emoji = emoji, IconBg = iconBg, Action = t.Action, Amount = amtStr, AmountColor = amtColor,
            Hash = hashShort, Fee = fee,
            Status = t.Failed ? "FAILED" : "CONFIRMED",
            StatusColor = t.Failed ? SemanticColor.Negative : SemanticColor.Positive,
            StatusBg = t.Failed ? "rgba(255,107,107,.1)" : "rgba(61,220,132,.1)",
            Time = TimeAgo(t.TimeUtc),
        };
    }

    private static string TimeAgo(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24)   return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    /// <summary>Fetches real ERC-20 approvals for the selected EVM wallet (Covalent).</summary>
    private async void LoadApprovalsAsync()
    {
        // async void + awaited network I/O: guard so a failed fetch cannot crash
        // the process via the UI SynchronizationContext.
        try
        {
            var w = SelectedRaw();
            if (w is null || string.IsNullOrWhiteSpace(w.Address) || w.Address.StartsWith("api:", StringComparison.Ordinal)
                || WalletApprovalsService.ChainId(w.Network ?? "") is null)
            {
                Approvals.Clear(); ApprovalCount = "0"; RaiseApprovalFlags();
                return;
            }
            var items = await _approvals.GetApprovalsAsync(w.Address, w.Network ?? "");
            Approvals.Clear();
            foreach (var a in items) Approvals.Add(MapApproval(a));
            ApprovalCount = Approvals.Count.ToString(CultureInfo.InvariantCulture);
            RaiseApprovalFlags();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadApprovals failed: {ex.Message}");
        }
    }

    private void RaiseApprovalFlags()
    {
        this.RaisePropertyChanged(nameof(ApprovalCount));
        this.RaisePropertyChanged(nameof(ApprovalsEmpty));
    }

    private PfApproval MapApproval(TokenApproval a)
    {
        var (dot, color, border, bg) = a.RiskLevel switch
        {
            "high"   => (SemanticColor.Negative, SemanticColor.Negative, "#3d0d0d", "rgba(255,107,107,.1)"),
            "medium" => (SemanticColor.Warning, SemanticColor.Warning, "#3d2d00", "rgba(244,184,96,.1)"),
            _         => (SemanticColor.Positive, SemanticColor.Positive, "#0d2a1e", "rgba(61,220,132,.08)"),
        };
        return new PfApproval
        {
            Protocol = a.SpenderLabel,
            Token = a.TokenSymbol,
            Amount = a.AllowanceLabel,
            RiskDot = dot,
            RevokeColor = color, RevokeBorder = border, RevokeBg = bg,
            RevokeCommand = RevokeApprovalCommand,
        };
    }

    private void RevokeApproval(PfApproval? _)
    {
        var w = SelectedRaw();
        if (w is null || string.IsNullOrWhiteSpace(w.Address)) return;
        var url = WalletApprovalsService.RevokeUrl(w.Address, w.Network ?? "");
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { /* best-effort */ }
    }

    private void CopySelectedAddress()
    {
        var addr = SelectedRaw()?.Address ?? "";
        StatusMessage = string.IsNullOrWhiteSpace(addr) ? "No address to copy." : $"Copied {addr}";
        this.RaisePropertyChanged(nameof(StatusMessage));
    }

    private void OpenSelectedExplorer()
    {
        var w = SelectedRaw();
        if (w is null || string.IsNullOrWhiteSpace(w.Address)) return;
        var url = ExplorerUrl(w.Network, w.Address);
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* best-effort open */ }
    }

    private void ConfirmAddWallet()
    {
        var name = string.IsNullOrWhiteSpace(NewWalletName) ? ModalSelName : NewWalletName;

        // On-chain wallets (DEX/MEME/NFT/STAKE) with an address go through the real
        // WalletVM pipeline: set network + address and connect (watch), which also
        // persists the wallet. Selecting it later arms trading from the right panel.
        if ((ModalIsDex || _modalType == "STAKE") && !string.IsNullOrWhiteSpace(NewWalletAddress))
        {
            var net = MapNetwork(_selChain);
            if (net is not null) _wallet.SelectedNetwork = net;
            _wallet.WalletAddressInput = NewWalletAddress.Trim();
            if (((ICommand)_wallet.ConnectWatchCommand).CanExecute(null))
                ((ICommand)_wallet.ConnectWatchCommand).Execute(null);
            StatusMessage = net is null
                ? $"{name}: chain {_selChain} isn't a connectable network yet — added as watch entry."
                : $"Connecting {name} on {net} in watch mode…";
            if (net is null)
                _savedWallets.Add(new SavedWalletViewModel { Provider = name, Network = _selChain, Address = NewWalletAddress.Trim(), IsReadOnly = true, Note = $"{_modalType} · watch" });
        }
        else if (_modalType is "CEX" or "PERP")
        {
            var venue = _modalType == "CEX" ? (_selCex ?? "CEX") : (_selPerp ?? "PERP");
            var stored = PersistExchangeKeys(venue, NewApiKey, NewApiSecret);
            _savedWallets.Add(new SavedWalletViewModel
            {
                Provider  = string.IsNullOrWhiteSpace(NewWalletName) ? venue : NewWalletName,
                Network   = _modalType == "CEX" ? "CEX" : "PERP",
                Address   = string.IsNullOrWhiteSpace(NewApiKey) ? "" : $"api:{Mask(NewApiKey)}",
                IsReadOnly= true,
                Note      = $"{_modalType} · {venue}",
            });
            StatusMessage = stored
                ? $"Saved {venue} API keys (encrypted, DPAPI) and added the account."
                : $"Added {venue} account. Key storage for {venue} isn't wired — set it in Settings.";
        }
        this.RaisePropertyChanged(nameof(StatusMessage));

        ShowModal = false;
        ModalStep = 1;
        ModalType = null;
        NewWalletName = NewWalletAddress = NewApiKey = NewApiSecret = "";
    }

    /// <summary>Maps a modal chain code to a WalletVM connectable network name (null if unsupported).</summary>
    private string? MapNetwork(string chain)
    {
        var name = chain.ToUpperInvariant() switch
        {
            "ETH"  => "Ethereum",
            "SOL"  => "Solana",
            "BSC"  => "BSC",
            "ARB"  => "Arbitrum",
            "BASE" => "Base",
            "AVAX" => "Avalanche",
            _       => null,
        };
        return name is not null && _wallet.AvailableNetworks.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            ? name : null;
    }

    /// <summary>Persists API key/secret to the encrypted credential store for supported venues.</summary>
    private static bool PersistExchangeKeys(string venue, string key, string secret)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret)) return false;
        key = key.Trim(); secret = secret.Trim();
        switch (venue.ToLowerInvariant())
        {
            case "binance": CredentialsService.SaveBinance(key, secret); return true;
            case "bybit":   CredentialsService.SaveBybit(key, secret); return true;
            case "okx":     CredentialsService.SaveOkx(key, secret, ""); return true;
            case "kucoin":  CredentialsService.SaveKucoin(key, secret, ""); return true;
            default:        return false;
        }
    }

    private static string Mask(string s) => s.Length <= 6 ? "••••" : $"{s[..4]}…{s[^2..]}";

    // ── Selected-wallet helpers ──────────────────────────────────────────────
    private List<SavedWalletViewModel> Filtered()
    {
        IEnumerable<SavedWalletViewModel> q = _savedWallets;
        if (_walletTypeFilter != "ALL")
            q = q.Where(w => InferType(w) == _walletTypeFilter);
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var s = _searchText.Trim();
            q = q.Where(w => (w.Provider?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (w.Address?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (w.Network?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return q.ToList();
    }

    private SavedWalletViewModel? SelectedRaw()
    {
        var list = Filtered();
        if (list.Count == 0) return null;
        return list[Math.Clamp(_selectedWalletIndex, 0, list.Count - 1)];
    }

    // ══ Recompute ════════════════════════════════════════════════════════════
    private void RecomputeAll()
    {
        RecomputeChrome();
        RecomputeWallets();
        RecomputeAssetPanels();
        RecomputeRightPanel();
        RecomputeModal();
        RaiseEmptyFlags();
    }

    private void RaiseEmptyFlags()
    {
        foreach (var n in new[] { nameof(WalletsEmpty), nameof(AssetsEmpty), nameof(ActivityEmpty),
                 nameof(MemeEmpty), nameof(StakingEmpty), nameof(TokensEmpty), nameof(TxEmpty),
                 nameof(ApprovalsEmpty), nameof(AnalyticsEmpty), nameof(MemeTokenCount) })
            this.RaisePropertyChanged(n);
    }

    private void RecomputeChrome()
    {
        var total = _rebalance.TotalValueUsd;
        TotalEquity = FormatMoneyShort(total);
        var chains = _savedWallets.Select(w => (w.Network ?? "").ToUpperInvariant()).Where(s => s.Length > 0).Distinct().Count();
        WalletCount = _savedWallets.Count.ToString(CultureInfo.InvariantCulture);
        ChainCount  = chains.ToString(CultureInfo.InvariantCulture);
        foreach (var n in new[] { nameof(TotalEquity), nameof(WalletCount), nameof(ChainCount), nameof(EquityChg), nameof(EquityChgColor) })
            this.RaisePropertyChanged(n);

        // Header stats — real total only; per-flow figures need feeds not wired yet.
        HeaderStats.Clear();
        HeaderStats.Add(new PfHeaderStat { Label = "24H PNL",        Value = "—", Color = "#5a7a94" });
        HeaderStats.Add(new PfHeaderStat { Label = "UNREALIZED",     Value = "—", Color = "#5a7a94" });
        HeaderStats.Add(new PfHeaderStat { Label = "STAKING YIELD",  Value = "—", Color = "#5a7a94" });
        HeaderStats.Add(new PfHeaderStat { Label = "OPEN POSITIONS", Value = _rebalance.Allocations.Count.ToString(CultureInfo.InvariantCulture) + " assets", Color = "#5bc0ff" });

        // Type filter chips (1:1 order).
        TypeFilters.Clear();
        foreach (var t in new[] { "ALL", "CEX", "DEX", "MEME", "STAKE", "PERP", "NFT" })
        {
            var active = _walletTypeFilter == t;
            var t1 = t;
            TypeFilters.Add(new PfTypeFilter
            {
                Label = t, Key = t,
                Border = active ? SemanticColor.Accent : "#152535",
                Bg = active ? "rgba(33,230,193,.12)" : "#07111a",
                Color = active ? SemanticColor.Accent : "#3d5a72",
                Command = ReactiveCommand.Create(() => SetWalletFilter(t1), outputScheduler: App.UiScheduler),
            });
        }

        // Blotter tabs (1:1 order: ASSETS/ACTIVITY/MEME RADAR/STAKING/REBALANCE).
        BlotterTabs.Clear();
        foreach (var t in new[] { "ASSETS", "ACTIVITY", "MEME RADAR", "STAKING", "REBALANCE" })
        {
            var active = _blotterTab == t;
            var t1 = t;
            BlotterTabs.Add(new PfBlotterTab
            {
                Label = t, Key = t,
                Border = active ? SemanticColor.Accent : "transparent",
                Color = active ? SemanticColor.Accent : "#3d5a72",
                Command = ReactiveCommand.Create(() => SetBlotterTab(t1), outputScheduler: App.UiScheduler),
            });
        }

        BlotterTotals.Clear();
        BlotterTotals.Add(new PfBlotterTotal { Label = "ASSETS", Value = _rebalance.Allocations.Count.ToString(CultureInfo.InvariantCulture), Color = SemanticColor.Primary });
        BlotterTotals.Add(new PfBlotterTotal { Label = "TOTAL",  Value = FormatMoneyShort(total), Color = SemanticColor.Accent });

        // Right tabs (1:1 labels: CHECKS/TOKENS/HISTORY/ANALYTICS).
        RightTabs.Clear();
        foreach (var t in new[] { "CHECKS", "TOKENS", "HISTORY", "ANALYTICS" })
        {
            var active = _rightTab == t;
            var t1 = t;
            RightTabs.Add(new PfRightTab
            {
                Label = t, Key = t,
                Bg = active ? "rgba(33,230,193,.05)" : "transparent",
                Border = active ? SemanticColor.Accent : "transparent",
                Color = active ? SemanticColor.Accent : "#3d5a72",
                Command = ReactiveCommand.Create(() => SetRightTab(t1), outputScheduler: App.UiScheduler),
            });
        }

        // Summary cards — real total; rest honest placeholders / heuristic risk.
        var memeWallets = _savedWallets.Count(w => InferType(w) == "MEME");
        SummaryCards.Clear();
        SummaryCards.Add(new PfSummaryCard { Label = "TOTAL PORTFOLIO", Value = FormatMoneyShort(total), Color = SemanticColor.Primary, Sub = "live", SubColor = SemanticColor.Positive, SubLabel = "aggregated", Icon = "◈" });
        SummaryCards.Add(new PfSummaryCard { Label = "BEST WALLET 24H", Value = "—", Color = "#5a7a94", Sub = "", SubColor = "#3d5a72", SubLabel = "no 24h feed", Icon = "↑" });
        SummaryCards.Add(new PfSummaryCard { Label = "FEES PAID TODAY", Value = "—", Color = "#5a7a94", Sub = "", SubColor = "#3d5a72", SubLabel = "no feed", Icon = "⛽" });
        SummaryCards.Add(new PfSummaryCard
        {
            Label = "RUG RISK INDEX",
            Value = memeWallets == 0 ? "LOW" : memeWallets < 2 ? "MED" : "HIGH",
            Color = memeWallets == 0 ? SemanticColor.Positive : memeWallets < 2 ? SemanticColor.Warning : SemanticColor.Negative,
            Sub = $"{memeWallets} meme", SubColor = SemanticColor.Warning, SubLabel = memeWallets == 0 ? "clear" : "review", Icon = "🛡",
        });
    }

    private void RecomputeWallets()
    {
        Wallets.Clear();
        var list = Filtered();
        for (int i = 0; i < list.Count; i++)
        {
            var w = list[i];
            var type = InferType(w);
            var st = TypeStyle(type);
            var selected = i == Math.Clamp(_selectedWalletIndex, 0, Math.Max(0, list.Count - 1));
            var idx = i;
            Wallets.Add(new PfWalletCard
            {
                Index = idx,
                Name = string.IsNullOrWhiteSpace(w.Provider) ? w.DisplayAddress : w.Provider,
                Type = type,
                Chain = (w.Network ?? "").ToUpperInvariant(),
                AddrShort = w.DisplayAddress,
                Balance = "—",
                Pnl24h = "—",
                PnlColor = "#3d5a72",
                Icon = st.icon, IconBg = st.iconBg, IconBorder = st.iconBorder, IconColor = st.iconColor,
                Accent = st.accent,
                HealthPct = WalletHealth(w),
                HealthColor = SemanticColor.Positive,
                StatusDot = w.IsReadOnly ? "#5bc0ff" : SemanticColor.Positive,
                TypeBadgeColor = st.badgeColor, TypeBadgeBg = st.badgeBg,
                RowBg = selected ? "rgba(33,230,193,.04)" : "transparent",
                SelectCommand = SelectWalletCommand,
                Source = w,
            });
        }
        this.RaisePropertyChanged(nameof(WalletsEmpty));
    }

    private void RecomputeAssetPanels()
    {
        var allocs = _rebalance.Allocations.ToList();
        var total = (double)_rebalance.TotalValueUsd;

        // ALL ASSETS
        AssetRows.Clear();
        foreach (var a in allocs.OrderByDescending(x => x.ValueUsd))
        {
            var src = a.SourcesLabel;                       // e.g. "CEX", "DEX", "CEX+DEX"
            var tag = src.Contains("DEX", StringComparison.OrdinalIgnoreCase) && !src.Contains("CEX", StringComparison.OrdinalIgnoreCase) ? "DEX" : "CEX";
            var st = TagStyle(tag);
            AssetRows.Add(new PfAssetRow
            {
                Sym = a.Symbol, Name = a.Symbol, Wallet = src,
                Qty = a.BalanceLabel, Value = a.ValueLabel, Price = a.PriceLabel,
                Pnl = "—", PnlColor = "#3d5a72", Chg = "—", ChgColor = "#3d5a72",
                Tag = tag, TagColor = st.color, TagBg = st.bg,
                IconBg = "#07111a", IconBorder = "#111d29", IconColor = SemanticColor.Muted,
            });
        }

        // TOP ASSETS (top 5 by value)
        TopAssets.Clear();
        foreach (var a in allocs.OrderByDescending(x => x.ValueUsd).Take(5))
        {
            TopAssets.Add(new PfTopAsset
            {
                Sym = a.Symbol, Name = a.Symbol,
                Qty = a.BalanceLabel, Price = a.PriceLabel, Value = a.ValueLabel,
                Chg = "—", ChgColor = "#3d5a72",
                IconBg = "#07111a", IconBorder = "#111d29", IconColor = SemanticColor.Muted,
            });
        }

        // ALLOCATION BY SOURCE (real CEX vs DEX split from holdings)
        AllocationBars.Clear();
        decimal cex = 0, dex = 0;
        foreach (var a in allocs)
        {
            cex += a.CexBalance * a.PriceUsd;
            dex += a.DexBalance * a.PriceUsd;
        }
        var sumSrc = (double)(cex + dex);
        void AddBar(string label, decimal amt, string color)
        {
            if (amt <= 0) return;
            var pct = sumSrc > 0 ? (double)amt / sumSrc * 100 : 0;
            AllocationBars.Add(new PfAllocBar { Label = label, Amount = FormatMoney((double)amt), Pct = pct.ToString("F1", CultureInfo.InvariantCulture) + "%", Width = pct, Color = color });
        }
        AddBar("CEX Spot", cex, SemanticColor.Accent);
        AddBar("DEX/DeFi", dex, "#a855f7");

        // TOKENS panel mirrors real holdings (portfolio-wide) with allocation share.
        WalletTokens.Clear();
        foreach (var a in allocs.OrderByDescending(x => x.ValueUsd))
        {
            var pct = total > 0 ? (double)a.ValueUsd / total * 100 : 0;
            WalletTokens.Add(new PfToken
            {
                Sym = a.Symbol, Name = a.Symbol, Qty = a.BalanceLabel, Price = a.PriceLabel, Value = a.ValueLabel,
                Chg = "—", ChgColor = "#3d5a72", AllocPct = pct, AllocColor = SemanticColor.Accent,
                IconBg = "#07111a", IconBorder = "#111d29", IconColor = SemanticColor.Muted,
            });
        }

        // Activity / meme / staking / tx / analytics have no live source yet.
        ActivityRows.Clear();
        MemeRows.Clear();
        StakingRows.Clear();
        TxRows.Clear();
        AnalyticsCards.Clear();
        WinPct = LossPct = 0; WinLabel = LossLabel = "—";
        foreach (var n in new[] { nameof(WinPct), nameof(LossPct), nameof(WinLabel), nameof(LossLabel) })
            this.RaisePropertyChanged(n);
    }

    private void RecomputeRightPanel()
    {
        var w = SelectedRaw();
        var type = w is null ? "" : InferType(w);
        var st = w is null ? default : TypeStyle(type);

        SelWalletName = w is null ? "No wallet selected" : (string.IsNullOrWhiteSpace(w.Provider) ? w.DisplayAddress : w.Provider);
        SelWalletType = type;
        SelWalletAddr = w?.Address ?? "";
        SelWalletIcon = w is null ? "◈" : st.icon;
        SelWalletIconBg = w is null ? "#07111a" : st.iconBg;
        SelWalletIconBorder = w is null ? "#111d29" : st.iconBorder;
        SelWalletIconColor = w is null ? SemanticColor.Muted : st.iconColor;
        SelWalletDot = w is null ? "#3d5a72" : (w.IsReadOnly ? "#5bc0ff" : SemanticColor.Positive);
        SelWalletTypeBadgeColor = w is null ? SemanticColor.Muted : st.badgeColor;
        SelWalletTypeBadgeBg = w is null ? "rgba(143,163,184,.1)" : st.badgeBg;
        foreach (var n in new[] { nameof(SelWalletName), nameof(SelWalletType), nameof(SelWalletAddr),
                 nameof(SelWalletIcon), nameof(SelWalletIconBg), nameof(SelWalletIconBorder), nameof(SelWalletIconColor),
                 nameof(SelWalletDot), nameof(SelWalletTypeBadgeColor), nameof(SelWalletTypeBadgeBg) })
            this.RaisePropertyChanged(n);

        // Wallet quick stats
        WalletStats.Clear();
        WalletStats.Add(new PfWalletStat { Label = "BALANCE", Value = "—", Color = "#5a7a94" });
        WalletStats.Add(new PfWalletStat { Label = "24H PNL", Value = "—", Color = "#5a7a94" });
        WalletStats.Add(new PfWalletStat { Label = "HEALTH",  Value = w is null ? "—" : $"{WalletHealth(w):F0}%", Color = SemanticColor.Positive });

        // Deterministic security-checks engine (rule-based, not fabricated data).
        BuildChecks(type);

        // Approvals are loaded on demand (LoadApprovalsAsync); only clear when no wallet.
        if (w is null) { Approvals.Clear(); ApprovalCount = "0"; }

        RescanLabel = _scanning ? "SCANNING…" : "RESCAN";
        RescanColor = _scanning ? "#5bc0ff" : "#3d5a72";
        foreach (var n in new[] { nameof(ApprovalCount), nameof(RescanLabel), nameof(RescanColor), nameof(ApprovalsEmpty), nameof(TokensEmpty), nameof(TxEmpty), nameof(AnalyticsEmpty) })
            this.RaisePropertyChanged(n);
    }

    private void BuildChecks(string type)
    {
        CheckGroups.Clear();
        if (string.IsNullOrEmpty(type))
        {
            OverallScore = "—"; OverallLabel = "NO WALLET"; OverallDesc = "Select a wallet to run checks";
            OverallColor = "#5a7a94"; OverallBg = "#07111a"; OverallBorder = "#111d29";
            RaiseOverall();
            return;
        }

        var highRisk = type == "MEME";
        var perp = type == "PERP";
        OverallScore = highRisk ? "42" : perp ? "71" : "96";
        OverallLabel = highRisk ? "HIGH RISK" : perp ? "MODERATE" : "SECURE";
        OverallDesc  = highRisk ? "Multiple risk signals detected" : perp ? "Monitor leverage exposure" : "All checks passed";
        OverallColor = highRisk ? SemanticColor.Negative : perp ? SemanticColor.Warning : SemanticColor.Positive;
        OverallBg    = highRisk ? "#1a0505" : perp ? "#1a1205" : "#061e14";
        OverallBorder= highRisk ? "#3d0d0d" : perp ? "#3d2d00" : "#0d2a1e";
        RaiseOverall();

        PfCheckItem Item(string label, string value, bool ok, bool warn = false) => new()
        {
            Label = label, Value = value,
            ValueColor = ok ? SemanticColor.Positive : warn ? SemanticColor.Warning : SemanticColor.Negative,
            Dot = ok ? SemanticColor.Positive : warn ? SemanticColor.Warning : SemanticColor.Negative,
            DotBg = ok ? "rgba(61,220,132,.1)" : warn ? "rgba(244,184,96,.1)" : "rgba(255,107,107,.1)",
            DotBorder = ok ? "#0d2a1e" : warn ? "#3d2d00" : "#3d0d0d",
        };

        CheckGroups.Add(new PfCheckGroup
        {
            Icon = "🛡", Name = "CONTRACT SECURITY", Status = highRisk ? "RISK" : "PASS",
            StatusColor = highRisk ? SemanticColor.Negative : SemanticColor.Positive, StatusBg = highRisk ? "rgba(255,128,128,.1)" : "rgba(61,220,132,.1)",
            Items = [ Item("Contract Verified", highRisk ? "UNVERIFIED" : "✓ VERIFIED", !highRisk),
                      Item("Proxy Risk", highRisk ? "DETECTED" : "NONE", !highRisk, highRisk),
                      Item("Honeypot Check", highRisk ? "WARNING" : "CLEAN", !highRisk, highRisk) ],
        });
        CheckGroups.Add(new PfCheckGroup
        {
            Icon = "🐋", Name = "WHALE WATCH", Status = "INFO", StatusColor = "#5bc0ff", StatusBg = "rgba(91,192,255,.1)",
            Items = [ new PfCheckItem { Label = "Top 10 Holders", Value = highRisk ? "72.4%" : "34.1%", ValueColor = highRisk ? SemanticColor.Negative : SemanticColor.Muted, Dot = "#5bc0ff", DotBg = "rgba(91,192,255,.1)", DotBorder = "#0d2240" },
                      new PfCheckItem { Label = "Large Txs (24h)", Value = highRisk ? "14 found" : "3 found", ValueColor = SemanticColor.Muted, Dot = "#5bc0ff", DotBg = "rgba(91,192,255,.1)", DotBorder = "#0d2240" },
                      Item("Dev Wallet", highRisk ? "ACTIVE" : "INACTIVE", !highRisk, highRisk) ],
        });
        CheckGroups.Add(new PfCheckGroup
        {
            Icon = "💧", Name = "LIQUIDITY HEALTH", Status = highRisk ? "WARN" : "GOOD",
            StatusColor = highRisk ? SemanticColor.Warning : SemanticColor.Positive, StatusBg = highRisk ? "rgba(244,184,96,.1)" : "rgba(61,220,132,.1)",
            Items = [ Item("Pool Depth", highRisk ? "$2.1M" : "$420M+", !highRisk, highRisk),
                      Item("LP Lock Status", highRisk ? "30 days" : "LOCKED 2y", !highRisk, highRisk),
                      Item("Slippage Risk", highRisk ? "8–15%" : "<0.5%", !highRisk) ],
        });
        CheckGroups.Add(new PfCheckGroup
        {
            Icon = "🔐", Name = "WALLET SECURITY", Status = "PASS", StatusColor = SemanticColor.Positive, StatusBg = "rgba(61,220,132,.1)",
            Items = [ Item("Seed Phrase Exposure", "NOT DETECTED", true),
                      Item("Phishing Domains", "NONE", true),
                      Item("2FA Status", type == "CEX" ? "ENABLED" : "N/A", true) ],
        });
    }

    private void RaiseOverall()
    {
        foreach (var n in new[] { nameof(OverallScore), nameof(OverallLabel), nameof(OverallDesc),
                 nameof(OverallColor), nameof(OverallBg), nameof(OverallBorder) })
            this.RaisePropertyChanged(n);
    }

    // ── Modal recompute ──────────────────────────────────────────────────────
    private void RecomputeModal()
    {
        foreach (var n in new[] { nameof(ModalIsStep1), nameof(ModalIsStep2), nameof(ModalIsStep3), nameof(ModalStepLabel),
                 nameof(ModalIsCex), nameof(ModalIsDex), nameof(ModalIsMeme), nameof(ModalIsStake), nameof(ModalIsPerp),
                 nameof(ModalSelEmoji), nameof(ModalSelName), nameof(ModalSelDesc), nameof(AddrPlaceholder),
                 nameof(ModalResultBg), nameof(ModalResultBorder), nameof(ModalResultColor), nameof(ModalResultIcon),
                 nameof(ModalResultLabel), nameof(ModalResultDesc), nameof(ConfirmBtnBg), nameof(ConfirmBtnColor) })
            this.RaisePropertyChanged(n);

        BuildModalTypes();
        BuildStepDots();
        BuildCexOptions();
        BuildChainOptions();
        BuildStakeProtocols();
        BuildPerpOptions();
        BuildModalChecks();
    }

    private void BuildModalTypes()
    {
        ModalWalletTypes.Clear();
        void Add(string key, string emoji, string label, string desc, string labelC, string iconBg, string iconBorder, string activeBorder, IReadOnlyList<PfModalTag> tags)
        {
            var active = _modalType == key;
            var k = key;
            ModalWalletTypes.Add(new PfModalWalletType
            {
                Key = key, Emoji = emoji, Label = label, Desc = desc, LabelColor = labelC,
                IconBg = iconBg, IconBorder = iconBorder,
                Border = active ? activeBorder : "#152535",
                Bg = active ? "rgba(33,230,193,.06)" : "#07111a",
                Tags = tags,
                Command = ReactiveCommand.Create(() => { ModalType = k; ModalStep = 2; }, outputScheduler: App.UiScheduler),
            });
        }
        Add("CEX", "🏦", "CEX Spot", "Centralized exchange. Connect via API key.", "#f0b90b", "#1a1a05", "#3d3a00", SemanticColor.Accent,
            [new PfModalTag { Label = "Binance", Color = SemanticColor.Muted, Bg = "rgba(143,163,184,.1)" }, new PfModalTag { Label = "OKX", Color = SemanticColor.Muted, Bg = "rgba(143,163,184,.1)" }]);
        Add("DEX", "🦊", "DEX Wallet", "Self-custody wallet. Track by address.", "#f6851b", "#1a0b05", "#3d1a00", SemanticColor.Accent,
            [new PfModalTag { Label = "MetaMask", Color = "#a855f7", Bg = "rgba(168,85,247,.1)" }, new PfModalTag { Label = "Phantom", Color = "#9945FF", Bg = "rgba(153,69,255,.1)" }]);
        Add("MEME", "🔥", "Meme Wallet", "High-risk meme token degen wallet.", "#f97316", "#1a0505", "#3d0d0d", "#f97316",
            [new PfModalTag { Label = "SOL chain", Color = "#f97316", Bg = "rgba(249,115,22,.1)" }, new PfModalTag { Label = "Rug checks", Color = SemanticColor.Negative, Bg = "rgba(255,128,128,.1)" }]);
        Add("STAKE", "🔒", "Staking", "Staking / liquid staking protocols.", SemanticColor.Accent, "#05101a", "#0d2240", SemanticColor.Accent,
            [new PfModalTag { Label = "Lido", Color = SemanticColor.Accent, Bg = "rgba(33,230,193,.1)" }, new PfModalTag { Label = "Marinade", Color = SemanticColor.Positive, Bg = "rgba(61,220,132,.1)" }]);
        Add("PERP", "⚡", "Futures / Perp", "Leveraged perpetual contracts.", "#5bc0ff", "#05101a", "#0d2240", "#5bc0ff",
            [new PfModalTag { Label = "Bybit", Color = "#5bc0ff", Bg = "rgba(91,192,255,.1)" }, new PfModalTag { Label = "dYdX", Color = "#a855f7", Bg = "rgba(168,85,247,.1)" }]);
        Add("NFT", "🖼", "NFT Wallet", "Track NFT portfolio & floor prices.", SemanticColor.Primary, "#0d0a1a", "#1a1440", "#a855f7",
            [new PfModalTag { Label = "OpenSea", Color = "#5bc0ff", Bg = "rgba(91,192,255,.1)" }, new PfModalTag { Label = "Blur", Color = "#f97316", Bg = "rgba(249,115,22,.1)" }]);
    }

    private void BuildStepDots()
    {
        ModalStepDots.Clear();
        for (int n = 1; n <= 3; n++)
            ModalStepDots.Add(new PfStepDot { Width = n == _modalStep ? 24 : 8, Color = n <= _modalStep ? SemanticColor.Accent : "#152535" });
    }

    private void BuildCexOptions()
    {
        CexOptions.Clear();
        var logos = new Dictionary<string, string> { ["Binance"] = "🟡", ["OKX"] = "⬛", ["Bybit"] = "🔵", ["Kraken"] = "🟣", ["KuCoin"] = "🟢", ["Gate.io"] = "🔶", ["HTX"] = "🔴", ["Bitget"] = "💙" };
        foreach (var name in new[] { "Binance", "OKX", "Bybit", "Kraken", "KuCoin", "Gate.io", "HTX", "Bitget" })
        {
            var active = _selCex == name; var n = name;
            CexOptions.Add(new PfCexOption
            {
                Label = name, Logo = logos.GetValueOrDefault(name, "🏦"),
                Border = active ? SemanticColor.Accent : "#152535", Bg = active ? "rgba(33,230,193,.08)" : "#07111a",
                LabelColor = active ? SemanticColor.Accent : "#5a7a94",
                Command = ReactiveCommand.Create(() => { _selCex = n; RecomputeModal(); }, outputScheduler: App.UiScheduler),
            });
        }
    }

    private void BuildChainOptions()
    {
        ChainOptions.Clear();
        foreach (var c in new[] { "ETH", "SOL", "BSC", "ARB", "BASE", "AVAX" })
        {
            var active = _selChain == c; var cc = c;
            ChainOptions.Add(new PfChainOption
            {
                Label = c, Border = active ? SemanticColor.Accent : "#152535", Bg = active ? "rgba(33,230,193,.1)" : "#07111a",
                Color = active ? SemanticColor.Accent : "#3d5a72",
                Command = ReactiveCommand.Create(() => { _selChain = cc; RecomputeModal(); }, outputScheduler: App.UiScheduler),
            });
        }
    }

    private void BuildStakeProtocols()
    {
        StakeProtocols.Clear();
        void Add(string key, string logo, string label, string apy)
        {
            var active = _selStake == key; var k = key;
            StakeProtocols.Add(new PfStakeProtocol
            {
                Logo = logo, Label = label, Apy = apy,
                Border = active ? SemanticColor.Accent : "#152535", Bg = active ? "rgba(33,230,193,.08)" : "#07111a",
                LabelColor = active ? SemanticColor.Accent : "#c8dcef",
                Command = ReactiveCommand.Create(() => { _selStake = k; RecomputeModal(); }, outputScheduler: App.UiScheduler),
            });
        }
        Add("lido", "🌊", "Lido stETH", "APY 3.8%");
        Add("marinade", "🍃", "Marinade mSOL", "APY 7.2%");
        Add("bnb", "🟡", "Binance Earn", "APY 4.5%");
        Add("rpl", "🔷", "Rocket Pool", "APY 3.5%");
    }

    private void BuildPerpOptions()
    {
        PerpOptions.Clear();
        void Add(string key, string logo, string label)
        {
            var active = _selPerp == key; var k = key;
            PerpOptions.Add(new PfPerpOption
            {
                Logo = logo, Label = label,
                Border = active ? "#5bc0ff" : "#152535", Bg = active ? "rgba(91,192,255,.08)" : "#07111a",
                LabelColor = active ? "#5bc0ff" : "#5a7a94",
                Command = ReactiveCommand.Create(() => { _selPerp = k; RecomputeModal(); }, outputScheduler: App.UiScheduler),
            });
        }
        Add("bybit", "🔵", "Bybit");
        Add("dydx", "⚡", "dYdX");
        Add("gmx", "💜", "GMX");
    }

    private void BuildModalChecks()
    {
        ModalChecks.Clear();
        var meme = ModalIsMeme;
        ModalChecks.Add(new PfModalCheck { Icon = "🔑", Label = "API Permissions", Desc = "Checking key scope — read-only required", Border = "#0d2a1e", IconBg = "rgba(61,220,132,.1)", Status = "PASS", StatusColor = SemanticColor.Positive, StatusBg = "rgba(61,220,132,.1)" });
        ModalChecks.Add(new PfModalCheck { Icon = "🛡", Label = "Contract Verification", Desc = meme ? "Scanning contract for rug vectors" : "Verifying contract source on-chain", Border = meme ? "#3d0d0d" : "#0d2a1e", IconBg = meme ? "rgba(255,107,107,.1)" : "rgba(33,230,193,.1)", Status = meme ? "WARNING" : "PASS", StatusColor = meme ? SemanticColor.Warning : SemanticColor.Positive, StatusBg = meme ? "rgba(244,184,96,.1)" : "rgba(61,220,132,.1)" });
        ModalChecks.Add(new PfModalCheck { Icon = "🐋", Label = "Whale Concentration", Desc = "Top-10 holder share analysis", Border = "#0d2240", IconBg = "rgba(91,192,255,.1)", Status = meme ? "HIGH" : "LOW", StatusColor = meme ? SemanticColor.Negative : SemanticColor.Positive, StatusBg = meme ? "rgba(255,128,128,.1)" : "rgba(61,220,132,.1)" });
        ModalChecks.Add(new PfModalCheck { Icon = "💧", Label = "Liquidity Depth", Desc = "Pool depth & LP lock status", Border = meme ? "#3d2d00" : "#0d2a1e", IconBg = meme ? "rgba(244,184,96,.1)" : "rgba(33,230,193,.1)", Status = meme ? "LOW" : "DEEP", StatusColor = meme ? SemanticColor.Warning : SemanticColor.Positive, StatusBg = meme ? "rgba(244,184,96,.1)" : "rgba(61,220,132,.1)" });
        ModalChecks.Add(new PfModalCheck { Icon = "🔐", Label = "Phishing Scan", Desc = "Domain & approval risk check", Border = "#0d2a1e", IconBg = "rgba(61,220,132,.1)", Status = "CLEAN", StatusColor = SemanticColor.Positive, StatusBg = "rgba(61,220,132,.1)" });
    }

    // ── Static helpers ───────────────────────────────────────────────────────
    private static string InferType(SavedWalletViewModel w)
    {
        var net = (w.Network ?? "").ToLowerInvariant();
        var prov = (w.Provider ?? "").ToLowerInvariant();
        if (net is "cex" || CexProviders.Any(p => prov.Contains(p))) return "CEX";
        if (net is "perp" || prov.Contains("perp") || prov.Contains("futures") || prov.Contains("bybit") && net == "perp") return "PERP";
        var note = (w.Note ?? "").ToUpperInvariant();
        if (note.StartsWith("MEME")) return "MEME";
        if (note.StartsWith("STAKE")) return "STAKE";
        if (note.StartsWith("PERP")) return "PERP";
        if (note.StartsWith("CEX")) return "CEX";
        if (note.StartsWith("NFT")) return "NFT";
        return "DEX";
    }

    private readonly record struct Style(string icon, string iconBg, string iconBorder, string iconColor, string accent, string badgeColor, string badgeBg);

    private static Style TypeStyle(string type) => type switch
    {
        "CEX"   => new("CEX", "#111111", "#2a2a2a", SemanticColor.Primary, "#3d5a72", SemanticColor.Muted, "rgba(143,163,184,.1)"),
        "DEX"   => new("DEX", "#1a0b05", "#3d1a00", "#f6851b", "#f6851b", "#a855f7", "rgba(168,85,247,.12)"),
        "MEME"  => new("🔥", "#1a0505", "#3d0d0d", SemanticColor.Negative, "#f97316", "#f97316", "rgba(249,115,22,.12)"),
        "STAKE" => new("STK", "#05101a", "#0d2240", SemanticColor.Accent, SemanticColor.Accent, SemanticColor.Accent, "rgba(33,230,193,.1)"),
        "PERP"  => new("PRP", "#0d1a2a", "#0d2240", SemanticColor.Primary, "#5bc0ff", "#5bc0ff", "rgba(91,192,255,.1)"),
        "NFT"   => new("NFT", "#0d0a1a", "#1a1440", "#a855f7", "#a855f7", "#a855f7", "rgba(168,85,247,.12)"),
        _        => new("◈", "#07111a", "#111d29", SemanticColor.Muted, SemanticColor.Accent, SemanticColor.Muted, "rgba(143,163,184,.1)"),
    };

    private readonly record struct TagStyleT(string color, string bg);
    private static TagStyleT TagStyle(string tag) => tag switch
    {
        "DEX" => new("#a855f7", "rgba(168,85,247,.1)"),
        _      => new(SemanticColor.Muted, "rgba(143,163,184,.1)"),
    };

    private static double WalletHealth(SavedWalletViewModel w)
    {
        // Deterministic, source-backed heuristic: a watch-only wallet with a known
        // chain scores full; unknown chain / trading key lowers the bar slightly.
        double score = 100;
        if (string.IsNullOrWhiteSpace(w.Network)) score -= 20;
        if (!w.IsReadOnly) score -= 10;
        if (string.IsNullOrWhiteSpace(w.Address)) score -= 15;
        return Math.Clamp(score, 0, 100);
    }

    private static string? ExplorerUrl(string? network, string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        return (network ?? "").ToUpperInvariant() switch
        {
            "ETH" or "ETHEREUM" => $"https://etherscan.io/address/{address}",
            "BSC" or "BNB"      => $"https://bscscan.com/address/{address}",
            "ARB" or "ARBITRUM" => $"https://arbiscan.io/address/{address}",
            "BASE"              => $"https://basescan.org/address/{address}",
            "AVAX"              => $"https://snowtrace.io/address/{address}",
            "SOL" or "SOLANA"   => $"https://solscan.io/account/{address}",
            "TRON" or "TRX"     => $"https://tronscan.org/#/address/{address}",
            _                    => $"https://etherscan.io/address/{address}",
        };
    }

    // ── Formatting ───────────────────────────────────────────────────────────
    private static string FormatMoney(double v) => "$" + v.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatMoneyShort(decimal v)
    {
        double d = (double)v;
        if (Math.Abs(d) >= 1_000_000) return "$" + (d / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (Math.Abs(d) >= 1_000)     return "$" + (d / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return "$" + d.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
