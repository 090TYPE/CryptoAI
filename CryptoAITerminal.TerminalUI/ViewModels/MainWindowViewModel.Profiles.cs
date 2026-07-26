using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Core.Trading;
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

/// <summary>
/// Configuration profiles: the saved-settings snapshot, its properties and helpers.
///
/// Split out of MainWindowViewModel.cs verbatim — same class, same behaviour, just not all in
/// one 10 000-line file any more.
/// </summary>
public partial class MainWindowViewModel
{
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
    /// <summary>Full-bleed redesigned desks (Bots, Rules, …) suppress the generic page title.</summary>
    public bool   ShowSectionTitle        => !IsDeskSectionVisible;

    /// <summary>True while one of the redesigned full-bleed desks owns the page.</summary>
    public bool IsDeskSectionVisible =>
        IsBotsSectionVisible || IsRulesSectionVisible || IsNewsSectionVisible || IsAnalyticsSectionVisible || IsSettingsSectionVisible;

    // A desk draws its own chrome edge-to-edge, so the shared page frame steps out of the way.
    public Avalonia.Thickness   PagePadding         => IsDeskSectionVisible ? new Avalonia.Thickness(0)  : new Avalonia.Thickness(26);
    public Avalonia.Thickness   PageBorderThickness => IsDeskSectionVisible ? new Avalonia.Thickness(0)  : new Avalonia.Thickness(1);
    public Avalonia.CornerRadius PageCornerRadius   => IsDeskSectionVisible ? new Avalonia.CornerRadius(0) : new Avalonia.CornerRadius(14);
    public string               PageBackground      => IsDeskSectionVisible ? "#040b12" : "#07101A";
    public Avalonia.Thickness   PageOuterMargin     => IsDeskSectionVisible ? new Avalonia.Thickness(0)  : new Avalonia.Thickness(18, 16, 18, 16);
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
