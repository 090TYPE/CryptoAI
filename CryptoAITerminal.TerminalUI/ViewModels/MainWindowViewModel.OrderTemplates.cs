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
/// Order templates — applying a saved ticket to the trading desk.
///
/// Split out of MainWindowViewModel.cs verbatim — same class, same behaviour, just not all in
/// one 10 000-line file any more.
/// </summary>
public partial class MainWindowViewModel
{
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

        ChartPanelVM.SelectedTradeTimeframe = timeframe;
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
        var requestedTimeframe = ChartPanelVM.SelectedTradeTimeframe;
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
                    !string.Equals(ChartPanelVM.SelectedTradeTimeframe, requestedTimeframe, StringComparison.OrdinalIgnoreCase))
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
                    ChartPanelVM.ChartResetViewVersion++;
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
        var spotGateway = ActiveSpotGateway;
        var gateway = useFutures ? ActiveFuturesGateway : spotGateway;

        await _orderBookRefreshLock.WaitAsync();
        try
        {
            OrderBook orderBook;
            try
            {
                orderBook = await gateway.GetOrderBookAsync(market.Symbol, depth: 100);
                if (useFutures && !HasUsableOrderBook(orderBook))
                {
                    AddLog($"Futures order book for {market.Symbol} returned no depth, using spot display fallback.");
                    orderBook = await spotGateway.GetOrderBookAsync(market.Symbol, depth: 100);
                }
            }
            catch (Exception ex) when (useFutures)
            {
                AddLog($"Futures order book unavailable for {market.Symbol}, using spot display fallback: {ex.Message}");
                orderBook = await spotGateway.GetOrderBookAsync(market.Symbol, depth: 100);
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

}
