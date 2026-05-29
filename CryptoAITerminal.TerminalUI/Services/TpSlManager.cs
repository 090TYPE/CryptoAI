using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Manages TP, SL, Trailing Stop, and Partial TP for one open position.
/// For futures, tries exchange-native orders first; falls back to software
/// simulation when the gateway throws NotSupportedException.
/// Create a new instance per entry, call DetachAsync() before closing manually.
/// </summary>
public sealed class TpSlManager : IDisposable
{
    private readonly TpSlConfig _cfg;
    private IDisposable? _priceSub;
    private readonly object _lock = new();
    private bool _closed;

    // Position context
    private IExchangeGateway? _gateway;
    private string _symbol = "";
    private OrderSide _side;
    private FuturesPositionSide _posSide;
    private TradingMarketType _marketType;
    private decimal _entryPrice;
    private decimal _remainingQty;

    // Exchange-native TP/SL (Futures only) — false means software simulation
    private bool _usingExchangeTpSl;
    private string? _slOrderId;
    private readonly SemaphoreSlim _slUpdateSem = new(1, 1); // БАГ-08

    // Trailing / peak tracking
    private decimal _currentSlPrice;
    private decimal _peakPrice;

    public event Action<string>? OnEvent;

    public TpSlManager(TpSlConfig cfg) => _cfg = cfg;

    public async Task AttachAsync(
        string symbol,
        OrderSide side,
        decimal entryPrice,
        decimal quantity,
        FuturesPositionSide posSide,
        TradingMarketType marketType,
        IExchangeGateway gateway,
        IObservable<MarketData> priceStream)
    {
        _symbol = symbol;
        _side = side;
        _entryPrice = entryPrice;
        _remainingQty = quantity;
        _posSide = posSide;
        _marketType = marketType;
        _gateway = gateway;
        _peakPrice = entryPrice;
        _closed = false;
        _usingExchangeTpSl = false;

        bool isLong = side == OrderSide.Buy;
        var closeSide = isLong ? OrderSide.Sell : OrderSide.Buy;

        decimal slPrice = isLong
            ? entryPrice * (1m - _cfg.SlPercent / 100m)
            : entryPrice * (1m + _cfg.SlPercent / 100m);

        decimal tp1Price = isLong
            ? entryPrice * (1m + _cfg.TpPercent / 100m)
            : entryPrice * (1m - _cfg.TpPercent / 100m);

        decimal tp2Price = isLong
            ? entryPrice * (1m + _cfg.PartialTp2Percent / 100m)
            : entryPrice * (1m - _cfg.PartialTp2Percent / 100m);

        _currentSlPrice = slPrice;

        if (marketType == TradingMarketType.FuturesUsdM)
        {
            // Try exchange-native TP/SL — fall back to software on NotSupportedException.
            try
            {
                if (_cfg.SlEnabled)
                {
                    var slOrder = await gateway.PlaceStopLossOrderAsync(
                        symbol, closeSide, quantity, slPrice, posSide, reduceOnly: true);
                    _slOrderId = slOrder.Id;
                    _usingExchangeTpSl = true;
                    OnEvent?.Invoke($"SL placed @ {slPrice:N4}");
                }

                if (_cfg.TpEnabled)
                {
                    if (_cfg.PartialTp)
                    {
                        decimal tp1Qty = Math.Round(quantity * _cfg.PartialTpClosePercent / 100m, 3, MidpointRounding.ToZero);
                        decimal tp2Qty = Math.Round(quantity - tp1Qty, 3, MidpointRounding.ToZero);

                        if (tp1Qty > 0)
                        {
                            await gateway.PlaceTakeProfitOrderAsync(symbol, closeSide, tp1Qty, tp1Price, posSide, reduceOnly: true);
                            OnEvent?.Invoke($"TP1 ({_cfg.PartialTpClosePercent}%) @ {tp1Price:N4}");
                        }

                        if (tp2Qty > 0)
                        {
                            await gateway.PlaceTakeProfitOrderAsync(symbol, closeSide, tp2Qty, tp2Price, posSide, reduceOnly: true);
                            OnEvent?.Invoke($"TP2 (remaining) @ {tp2Price:N4}");
                        }
                    }
                    else
                    {
                        await gateway.PlaceTakeProfitOrderAsync(symbol, closeSide, quantity, tp1Price, posSide, reduceOnly: true);
                        OnEvent?.Invoke($"TP placed @ {tp1Price:N4}");
                    }
                    _usingExchangeTpSl = true;
                }
            }
            catch (NotSupportedException)
            {
                _usingExchangeTpSl = false;
                _slOrderId = null;
                OnEvent?.Invoke("Exchange TP/SL not supported — using software simulation");
            }
            catch (Exception ex)
            {
                OnEvent?.Invoke($"TP/SL order failed: {ex.Message}");
            }
        }

        // Price stream: needed for trailing stop, spot TP/SL, and futures software simulation.
        bool needsStream = _cfg.TrailingStop
            || (marketType == TradingMarketType.Spot && (_cfg.TpEnabled || _cfg.SlEnabled))
            || (marketType == TradingMarketType.FuturesUsdM && !_usingExchangeTpSl && (_cfg.TpEnabled || _cfg.SlEnabled));

        if (needsStream)
        {
            _priceSub = priceStream
                .Where(d => string.Equals(d.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                .Sample(TimeSpan.FromSeconds(3))
                .Subscribe(d => HandlePrice(d.LastPrice));
        }
    }

    private void HandlePrice(decimal price)
    {
        lock (_lock)
        {
            if (_closed) return;

            bool isLong = _side == OrderSide.Buy;

            // Spot in-process TP / SL check
            if (_marketType == TradingMarketType.Spot)
            {
                decimal effectiveSl = _cfg.TrailingStop
                    ? _currentSlPrice
                    : (isLong
                        ? _entryPrice * (1m - _cfg.SlPercent / 100m)
                        : _entryPrice * (1m + _cfg.SlPercent / 100m));

                if (_cfg.TpEnabled)
                {
                    bool tpHit = isLong
                        ? price >= _entryPrice * (1m + _cfg.TpPercent / 100m)
                        : price <= _entryPrice * (1m - _cfg.TpPercent / 100m);

                    if (tpHit)
                    {
                        // БАГ-07: Partial TP на спот — закрываем часть позиции, обновляем TP-уровень
                        if (_cfg.PartialTp && _remainingQty > 0)
                        {
                            var tp1Qty = Math.Round(_remainingQty * _cfg.PartialTpClosePercent / 100m, 3, MidpointRounding.ToZero);
                            if (tp1Qty > 0)
                            {
                                _remainingQty -= tp1Qty;
                                _ = FireSpotClosePartialAsync(price, "TP1", tp1Qty);
                                _cfg.TpPercent = _cfg.PartialTp2Percent;
                            }
                            // Не ставим _closed — ждём TP2 или SL
                        }
                        else
                        {
                            _closed = true;
                            _ = FireSpotCloseAsync(price, "TP");
                        }
                        return;
                    }
                }

                if (_cfg.SlEnabled)
                {
                    bool slHit = isLong ? price <= effectiveSl : price >= effectiveSl;
                    if (slHit) { _closed = true; _ = FireSpotCloseAsync(price, "SL"); return; }
                }
            }

            // Futures software TP/SL (when exchange-native orders are not available)
            if (_marketType == TradingMarketType.FuturesUsdM && !_usingExchangeTpSl)
            {
                decimal effectiveSl = _cfg.TrailingStop
                    ? _currentSlPrice
                    : (isLong
                        ? _entryPrice * (1m - _cfg.SlPercent / 100m)
                        : _entryPrice * (1m + _cfg.SlPercent / 100m));

                if (_cfg.TpEnabled)
                {
                    bool tpHit = isLong
                        ? price >= _entryPrice * (1m + _cfg.TpPercent / 100m)
                        : price <= _entryPrice * (1m - _cfg.TpPercent / 100m);

                    if (tpHit)
                    {
                        _closed = true;
                        _ = FireFuturesCloseAsync(price, "TP");
                        return;
                    }
                }

                if (_cfg.SlEnabled)
                {
                    bool slHit = isLong ? price <= effectiveSl : price >= effectiveSl;
                    if (slHit) { _closed = true; _ = FireFuturesCloseAsync(price, "SL"); return; }
                }
            }

            // Trailing stop movement
            if (!_cfg.TrailingStop || !_cfg.SlEnabled) return;

            if (isLong && price > _peakPrice)
            {
                _peakPrice = price;
                decimal newSl = price * (1m - _cfg.SlPercent / 100m);

                // Only move when improvement > 0.1% to avoid order spam
                if (newSl > _currentSlPrice * 1.001m)
                {
                    decimal oldSl = _currentSlPrice;
                    _currentSlPrice = newSl;

                    if (_marketType == TradingMarketType.FuturesUsdM && _usingExchangeTpSl)
                        _ = UpdateFuturesSlAsync(newSl, oldSl);
                }
            }
            else if (!isLong && price < _peakPrice)
            {
                _peakPrice = price;
                decimal newSl = price * (1m + _cfg.SlPercent / 100m);

                if (newSl < _currentSlPrice * 0.999m)
                {
                    decimal oldSl = _currentSlPrice;
                    _currentSlPrice = newSl;

                    if (_marketType == TradingMarketType.FuturesUsdM && _usingExchangeTpSl)
                        _ = UpdateFuturesSlAsync(newSl, oldSl);
                }
            }
        }
    }

    private async Task UpdateFuturesSlAsync(decimal newSlPrice, decimal oldSlPrice)
    {
        // БАГ-08: SemaphoreSlim предотвращает одновременные вызовы из параллельных тиков.
        if (!await _slUpdateSem.WaitAsync(0)) return;
        try
        {
            if (_slOrderId is not null)
            {
                try { await _gateway!.CancelOrderAsync(_symbol, _slOrderId); }
                catch { /* may already have fired */ }
                _slOrderId = null;
            }

            var closeSide = _side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var slOrder = await _gateway!.PlaceStopLossOrderAsync(
                _symbol, closeSide, _remainingQty, newSlPrice, _posSide, reduceOnly: true);
            _slOrderId = slOrder.Id;
            OnEvent?.Invoke($"Trailing SL: {oldSlPrice:N4} → {newSlPrice:N4}");
        }
        catch (Exception ex)
        {
            OnEvent?.Invoke($"Trailing SL update failed: {ex.Message}");
        }
        finally
        {
            _slUpdateSem.Release();
        }
    }

    private async Task FireSpotCloseAsync(decimal price, string reason)
    {
        try
        {
            await _gateway!.PlaceOrderAsync(new Order
            {
                Symbol = _symbol,
                Side = _side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = _remainingQty,
                MarketType = _marketType
            });
            OnEvent?.Invoke($"[{reason}] Spot closed @ {price:N4}");
        }
        catch (Exception ex)
        {
            OnEvent?.Invoke($"[{reason}] Spot close failed: {ex.Message}");
        }
    }

    // БАГ-07: частичное закрытие спот-позиции без полного выхода
    private async Task FireSpotClosePartialAsync(decimal price, string reason, decimal qty)
    {
        try
        {
            await _gateway!.PlaceOrderAsync(new Order
            {
                Symbol = _symbol,
                Side = _side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = qty,
                MarketType = _marketType
            });
            OnEvent?.Invoke($"[{reason}] Partial close {qty} @ {price:N4}, remaining {_remainingQty}");
        }
        catch (Exception ex)
        {
            OnEvent?.Invoke($"[{reason}] Partial close failed: {ex.Message}");
        }
    }

    private async Task FireFuturesCloseAsync(decimal price, string reason)
    {
        var closeSide = _side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        try
        {
            await _gateway!.PlaceOrderAsync(new Order
            {
                Symbol = _symbol,
                Side = closeSide,
                Type = OrderType.Market,
                Quantity = _remainingQty,
                MarketType = _marketType,
                ReduceOnly = true,
                PositionSide = _posSide
            });
            OnEvent?.Invoke($"[{reason}] Futures closed @ {price:N4}");
        }
        catch (Exception ex)
        {
            OnEvent?.Invoke($"[{reason}] Futures close failed: {ex.Message}");
        }
    }

    /// <summary>Cancel outstanding exchange orders and stop price monitoring.</summary>
    public async Task DetachAsync()
    {
        lock (_lock) { _closed = true; }

        _priceSub?.Dispose();
        _priceSub = null;

        if (_usingExchangeTpSl && _gateway is not null && _slOrderId is not null)
        {
            try { await _gateway.CancelOrderAsync(_symbol, _slOrderId); }
            catch { /* already filled or expired */ }
            _slOrderId = null;
        }
    }

    public void Dispose()
    {
        _priceSub?.Dispose();
    }
}
