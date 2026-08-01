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
/// The multi-symbol software working-order engine: the tick that arms and fires stops the exchange does not hold.
///
/// Split out of MainWindowViewModel.cs verbatim — same class, same behaviour, just not all in
/// one 10 000-line file any more.
/// </summary>
public partial class MainWindowViewModel
{
    // ── Multi-symbol software working-order engine ────────────────────────────
    // Each software (non-exchange-managed) order is evaluated against ITS OWN symbol's
    // live price and its OWN captured spot exchange, so a stop/limit armed on one symbol
    // keeps protecting it while the desk is viewing a different symbol. Exchange-managed
    // orders rest on the venue and are left untouched here.

    // Чтобы объяснение «стоп ждёт свой символ» попало в лог один раз, а не на каждом тике.
    private bool _trailingSymbolMismatchLogged;

    private readonly Dictionary<string, (decimal Qty, DateTime AtUtc)> _softwarePositionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SoftwarePositionCacheTtl = TimeSpan.FromSeconds(15);

    private async Task EvaluateWorkingOrdersAsync()
    {
        var lastPriceActive = CurrentTradePrice;

        // ── Advanced Trailing Stop tick ──────────────────────────────────────
        // Цена и свечи здесь всегда от символа, открытого на торговой вкладке. Раньше стоп кормили
        // ими без разбора: после переключения графика он трейлил уже другую монету от чужого пика,
        // а сработав — рыночно закрывал позицию по ней (ExecuteClosePosition работает с активным
        // символом). Сегодня стоп охраняет ту монету, на которой его вооружили, и только её.
        if (AdvancedTrailingVM.IsArmed && lastPriceActive > 0)
        {
            if (AdvancedTrailingVM.Guards(SelectedTradingSymbol))
            {
                var candleSnap = TradingCandles.ToList();
                AdvancedTrailingVM.OnPriceTick(lastPriceActive, candleSnap);
            }
            else if (!_trailingSymbolMismatchLogged)
            {
                // Один раз за уход с символа, а не на каждом тике таймера.
                _trailingSymbolMismatchLogged = true;
                AddLog($"[Trailing Stop] Ждёт {AdvancedTrailingVM.ArmedSymbol}: на графике сейчас " +
                       $"{SelectedTradingSymbol}, чужую монету стоп не ведёт и не закрывает.");
            }
        }

        if (AdvancedTrailingVM.Guards(SelectedTradingSymbol)) _trailingSymbolMismatchLogged = false;

        if (TradeBlotterVM.WorkingOrders.Count == 0)
            return;

        var candidates = TradeBlotterVM.WorkingOrders.Where(order => !order.IsExchangeManaged).ToList();
        if (candidates.Count == 0)
            return;

        var triggered = new List<WorkingOrderViewModel>();
        foreach (var order in candidates)
        {
            var quote = ResolveLiveQuoteForSymbol(order.Symbol);
            if (quote is null)
                continue; // no live price yet — never fabricate a trigger

            var (bid, ask, last) = quote.Value;
            var positionQty = await ResolveSoftwarePositionQuantityAsync(order);
            if (order.ShouldTrigger(bid, ask, last, positionQty))
                triggered.Add(order);
        }

        foreach (var order in triggered)
            await ExecuteWorkingOrderAsync(order);
    }

    /// <summary>Live (bid, ask, last) for a symbol from its market row; null when unknown.
    /// Uses the desk's fast path for the active symbol, else the Markets row for that symbol.</summary>
    private (decimal Bid, decimal Ask, decimal Last)? ResolveLiveQuoteForSymbol(string symbol)
    {
        if (string.Equals(symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase))
        {
            var aBid = SelectedMarket?.BestBid > 0 ? SelectedMarket!.BestBid : CurrentTradePrice;
            var aAsk = SelectedMarket?.BestAsk > 0 ? SelectedMarket!.BestAsk : CurrentTradePrice;
            var aLast = CurrentTradePrice;
            return aLast > 0 || aBid > 0 || aAsk > 0 ? (aBid, aAsk, aLast) : null;
        }

        var row = Markets.FirstOrDefault(m =>
            string.Equals(m.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && !m.IsDexMarket);
        if (row is null)
            return null;

        var last = row.LastPrice > 0 ? row.LastPrice : row.MidPrice;
        var bid = row.BestBid > 0 ? row.BestBid : last;
        var ask = row.BestAsk > 0 ? row.BestAsk : last;
        if (last <= 0 && bid <= 0 && ask <= 0)
            return null;
        return (bid, ask, last);
    }

    /// <summary>Open size backing a SELL/TP/SL software order. Active spot symbol uses the
    /// live desk position; a foreign symbol uses its spot base-asset balance (authoritative).
    /// BUY limits do not depend on position.</summary>
    private async Task<decimal> ResolveSoftwarePositionQuantityAsync(WorkingOrderViewModel order)
    {
        if (order.Kind == WorkingOrderKind.LimitBuy)
            return 0m;

        if (string.Equals(order.Symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase) && !OrderTicketVM.IsManualFuturesMode)
            return PositionQuantity;

        return await GetCachedSpotBaseBalanceAsync(order.ExecutionExchange, order.Symbol);
    }

    private async Task<decimal> GetCachedSpotBaseBalanceAsync(string exchangeName, string symbol)
    {
        var key = $"{exchangeName}|{symbol}";
        if (_softwarePositionCache.TryGetValue(key, out var cached) &&
            DateTime.UtcNow - cached.AtUtc < SoftwarePositionCacheTtl)
            return cached.Qty;

        var qty = 0m;
        try
        {
            var gateway = ResolveSpotGatewayByName(exchangeName);
            if (gateway is not null)
                qty = await gateway.GetBalanceAsync(BaseAssetOf(symbol));
        }
        catch (Exception ex)
        {
            AddLog($"Working-order balance check failed for {symbol} on {exchangeName}: {ex.Message}");
        }

        _softwarePositionCache[key] = (qty, DateTime.UtcNow);
        return qty;
    }

    private void InvalidateSoftwarePositionCache(WorkingOrderViewModel order) =>
        _softwarePositionCache.Remove($"{order.ExecutionExchange}|{order.Symbol}");

    private Core.Interfaces.IExchangeGateway? ResolveSpotGatewayByName(string exchangeName)
    {
        if (!string.IsNullOrWhiteSpace(exchangeName) && _spotGatewaysMap is not null &&
            _spotGatewaysMap.TryGetValue(exchangeName, out var gateway))
            return gateway;
        return ActiveSpotGateway;
    }

    private static readonly string[] SpotQuoteAssets = ["USDT", "USDC", "FDUSD", "BUSD", "TUSD", "DAI", "USD"];

    private static string BaseAssetOf(string symbol)
    {
        foreach (var quote in SpotQuoteAssets)
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase) && symbol.Length > quote.Length)
                return symbol[..^quote.Length];
        }
        return symbol;
    }

    private async Task<Order> PlaceSpotMarketOrderForSymbolAsync(
        string exchangeName, CryptoAITerminal.Core.Enums.OrderSide side, string symbol, decimal quantity)
    {
        var gateway = ResolveSpotGatewayByName(exchangeName)
            ?? throw new InvalidOperationException($"No spot gateway available for {exchangeName}.");
        var router = new MarketOrderRouter(gateway);
        return side == CryptoAITerminal.Core.Enums.OrderSide.Buy
            ? await router.BuyMarketAsync(symbol, quantity)
            : await router.SellMarketAsync(symbol, quantity);
    }

    private async Task ExecuteWorkingOrderAsync(WorkingOrderViewModel order)
    {
        if (!TradeBlotterVM.WorkingOrders.Contains(order))
        {
            return;
        }

        // The order is removed by the outcome, not up front. It used to be pulled from the list
        // before anything was sent, so a guard rejection or a failed market order silently threw
        // away the user's software stop-loss — at the moment its trigger had just fired.
        var consumed = false;

        // Only the active spot symbol drives the single-symbol desk position/PnL state.
        var isActiveSpot = string.Equals(order.Symbol, SelectedTradingSymbol, StringComparison.OrdinalIgnoreCase) && !OrderTicketVM.IsManualFuturesMode;

        try
        {
            if (order.Kind == WorkingOrderKind.LimitBuy)
            {
                if (!WalletVM.TryApproveLiveExecution("CEX working buy order", out var buyReason))
                {
                    AddLog($"{buyReason} Order stays armed.");
                    return;
                }

                var result = await PlaceSpotMarketOrderForSymbolAsync(
                    order.ExecutionExchange, CryptoAITerminal.Core.Enums.OrderSide.Buy, order.Symbol, order.Quantity);
                if (isActiveSpot)
                    await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Buy, order.TriggerPrice > 0 ? order.TriggerPrice : CurrentTradePrice, result.Quantity);
                InvalidateSoftwarePositionCache(order);
                AddLog($"BUY LIMIT filled at {order.TriggerPrice:N2} for {order.Symbol} on {order.ExecutionExchange}.");
                consumed = true;
                return;
            }

            // SELL / TAKE PROFIT / STOP LOSS — all reduce a spot holding.
            var approvalLabel = order.Kind switch
            {
                WorkingOrderKind.LimitSell => "CEX working sell order",
                WorkingOrderKind.TakeProfit => "CEX take-profit order",
                _ => "CEX stop-loss order"
            };
            if (!WalletVM.TryApproveLiveExecution(approvalLabel, out var sellReason))
            {
                AddLog($"{sellReason} Order stays armed.");
                return;
            }

            var available = isActiveSpot
                ? PositionQuantity
                : await GetCachedSpotBaseBalanceAsync(order.ExecutionExchange, order.Symbol);
            if (available <= 0)
            {
                AddLog($"{order.KindLabel} removed for {order.Symbol}: no {BaseAssetOf(order.Symbol)} balance to sell.");
                consumed = true;
                return;
            }

            var quantity = Math.Min(order.Quantity, available);
            if (quantity <= 0)
            {
                AddLog($"{order.KindLabel} removed for {order.Symbol}: resolved quantity is zero.");
                consumed = true;
                return;
            }

            var sellResult = await PlaceSpotMarketOrderForSymbolAsync(
                order.ExecutionExchange, CryptoAITerminal.Core.Enums.OrderSide.Sell, order.Symbol, quantity);
            if (isActiveSpot)
                await SyncManualExecutionStateAsync(CryptoAITerminal.Core.Enums.OrderSide.Sell, order.TriggerPrice > 0 ? order.TriggerPrice : CurrentTradePrice, sellResult.Quantity);
            InvalidateSoftwarePositionCache(order);
            AddLog($"{order.KindLabel} triggered at {order.TriggerPrice:N2} for {order.Symbol} on {order.ExecutionExchange}.");
            consumed = true;
        }
        catch (Exception ex)
        {
            // Stays armed: the trigger condition is still true, so the next evaluation pass
            // retries rather than leaving the position unprotected.
            AddLog($"{order.KindLabel} execution failed for {order.Symbol}: {ex.Message} — order stays armed.");
        }
        finally
        {
            if (consumed && TradeBlotterVM.WorkingOrders.Remove(order))
            {
                this.RaisePropertyChanged(nameof(WorkingOrdersCountLabel));
                PersistSoftwareWorkingOrders();
            }
        }
    }

}
