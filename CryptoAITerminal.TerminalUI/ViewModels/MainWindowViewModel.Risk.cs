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
/// The Risk desk: RiskManager caps, the live budget readout, the block audit log and the daily-loss sparkline.
///
/// Split out of MainWindowViewModel.cs verbatim — same class, same behaviour, just not all in
/// one 10 000-line file any more.
/// </summary>
public partial class MainWindowViewModel
{
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
    public string SessionPnlBrush => (UnrealizedPnl + RealizedPnl) >= 0 ? SemanticColor.Keys.Positive : SemanticColor.Keys.Negative;
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

    /// <summary>
    /// Redesigned Bots tab (self-contained interactive desk prototype). Rendered
    /// by <see cref="Views.BotsView"/> when the Bots section is visible.
    /// </summary>
    public BotsDesk.BotsDeskViewModel BotsDesk { get; } = new();

    /// <summary>Redesigned Rules tab (self-contained interactive rule-engine desk).</summary>
    public RulesDesk.RulesDeskViewModel RulesDesk { get; } = new();

    /// <summary>Redesigned "Market feed" desk (News/Tape/Liquidations), hosted in the News section.</summary>
    public MarketFeedDesk.MarketFeedDeskViewModel MarketFeedDesk { get; } = new();

    /// <summary>Redesigned Analytics tab (trade-analytics dashboard).</summary>
    public AnalyticsDesk.AnalyticsDeskViewModel AnalyticsDesk { get; } = new();

    /// <summary>Redesigned Settings tab (provider / keys / notifications / alerts / execution).</summary>
    public SettingsDesk.SettingsDeskViewModel SettingsDesk { get; } = new();
    public CopilotViewModel CopilotVM { get; private set; } = null!;
    public AutonomousAgentViewModel AutonomousVM { get; private set; } = null!;
    private Services.AppActions.AppAgentService? _appAgent;
    public AgentActionTrayViewModel AgentTray { get; private set; } = null!;
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
    public AiSignalDeskViewModel       AiSignalDeskVM       { get; }
    public PortfolioRebalanceViewModel PortfolioRebalanceVM { get; private set; } = null!;
    public PnlDashboardViewModel      PnlDashboardVM      { get; private set; } = null!;
    public DashboardViewModel              DashboardVM          { get; private set; } = null!;
    public ViewModels.Dashboard.DashboardLayoutViewModel DashboardLayoutVM { get; } = new();
    public ConfigurationProfileService    ProfileService       { get; } = new();
    public DexTradingViewModel DexTradingVM { get; }
    public DexDeskViewModel DexDeskVM { get; }
    public AiTradeAssistantViewModel TradeAssistantVM { get; }
    public WalletWorkspaceViewModel WalletVM { get; }

    /// <summary>Apply an AI-assistant setup to the live CEX order ticket.</summary>
    private void ApplyCexTradeSetup(TradeSetup setup)
    {
        SelectedOrderSide = setup.Bias == "LONG" ? "BUY" : "SELL";
        SelectedOrderType = "Limit";
        LimitPrice = setup.Entry;
        TakeProfitPrice = setup.TakeProfit;
        StopLossPrice = setup.StopLoss;
        WalletVM.GlobalPositionSizingPercent = setup.SizePercent;
        if (IsManualFuturesMode)
        {
            ManualFuturesLeverage = setup.Leverage;
        }

        AddLog($"AI assistant applied {setup.Bias} setup — entry {setup.Entry}, TP {setup.TakeProfit}, SL {setup.StopLoss}, {setup.Leverage}x.");
    }

    /// <summary>Arm price alerts at the assistant setup's entry / target / stop.</summary>
    private void ArmCexSetupAlerts(TradeSetup setup)
    {
        var symbol = SelectedTradingSymbol;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        ArmSetupAlert(symbol, setup.Entry);
        ArmSetupAlert(symbol, setup.TakeProfit);
        ArmSetupAlert(symbol, setup.StopLoss);
        AddLog($"Armed entry/TP/SL alerts for {symbol} from the AI setup.");
    }

    private void ArmSetupAlert(string symbol, decimal price)
    {
        if (price <= 0m)
        {
            return;
        }

        AlertsVM.NewAlertSymbol = symbol;
        AlertsVM.NewAlertThreshold = price;
        AlertsVM.SelectedCondition = price >= CurrentTradePrice ? "PriceAbove" : "PriceBelow";
        AlertsVM.AddAlertCommand.Execute().Subscribe();
    }
    public SniperViewModel SniperVM { get; }
    public BacktestViewModel BacktestVM { get; }
    public AlertsViewModel AlertsVM { get; }

    /// <summary>Unified smart-notification hub: one publish → antispam-deduped fan-out to all channels + inbox.</summary>
    public Services.Notifications.NotificationHub NotificationHubService { get; }
    /// <summary>In-app inbox backing the notification centre (newest first).</summary>
    public Services.Notifications.InMemoryNotificationInbox NotificationInbox { get; }

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
    /// <summary>Server feed: the shared AI digest streams + this user's events. Idle without a server.</summary>
    public AiFeedViewModel               AiFeedVM           { get; } = new();
    public OnChainMetricsViewModel       OnChainVM          { get; private set; } = null!;
    public OnboardingViewModel           OnboardingVM       { get; } = new();
    public LicenseViewModel              LicenseVM          { get; } = new();

}
