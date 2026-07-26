using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
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
    // Set while a close order is in flight. _closed used to be set here instead, before the order
    // was known to have gone through: if the market order was rejected (429, network, minNotional)
    // the catch only logged, HandlePrice kept returning early on _closed, and the position sat with
    // no protection at all — at exactly the moment its stop had triggered.
    private bool _closing;

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
    // Newest trailing level that still has to reach the exchange, set by ticks that arrived while
    // an update was already running.
    private decimal? _pendingSlPrice;
    private decimal _pendingSlFrom;

    // Trailing / peak tracking
    private decimal _currentSlPrice;
    private decimal _peakPrice;

    public event Action<string>? OnEvent;

    /// <summary>
    /// Raised when a triggered TP/SL could not be executed and the position is therefore left open
    /// and unprotected. Separate from <see cref="OnEvent"/> so the UI can surface it loudly instead
    /// of letting it scroll past in a log.
    /// </summary>
    public event Action<string>? OnProtectionLost;

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
        _closing = false;
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
            // Track native TP order ids so we can cancel them if a later leg fails and we
            // fall back to software (else an orphaned native TP coexists with the simulation).
            var placedTpIds = new List<string>();
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
                            var tp1 = await gateway.PlaceTakeProfitOrderAsync(symbol, closeSide, tp1Qty, tp1Price, posSide, reduceOnly: true);
                            if (tp1.Id is not null) placedTpIds.Add(tp1.Id);
                            OnEvent?.Invoke($"TP1 ({_cfg.PartialTpClosePercent}%) @ {tp1Price:N4}");
                        }

                        if (tp2Qty > 0)
                        {
                            var tp2 = await gateway.PlaceTakeProfitOrderAsync(symbol, closeSide, tp2Qty, tp2Price, posSide, reduceOnly: true);
                            if (tp2.Id is not null) placedTpIds.Add(tp2.Id);
                            OnEvent?.Invoke($"TP2 (remaining) @ {tp2Price:N4}");
                        }
                    }
                    else
                    {
                        var tp = await gateway.PlaceTakeProfitOrderAsync(symbol, closeSide, quantity, tp1Price, posSide, reduceOnly: true);
                        if (tp.Id is not null) placedTpIds.Add(tp.Id);
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
                // A non-NotSupported failure (bad trigger price, rate-limit) can leave only ONE
                // leg live on the exchange (e.g. SL placed, TP threw). That would leave
                // _usingExchangeTpSl=true and suppress the software stream → position runs with
                // half its protection. Cancel any native SL and fall back fully to software.
                if (_slOrderId is not null)
                {
                    try { await gateway.CancelOrderAsync(symbol, _slOrderId); } catch { /* best-effort */ }
                }
                foreach (var tpId in placedTpIds)
                {
                    try { await gateway.CancelOrderAsync(symbol, tpId); } catch { /* best-effort */ }
                }
                _usingExchangeTpSl = false;
                _slOrderId = null;
                OnEvent?.Invoke($"TP/SL order failed ({ex.Message}) — falling back to software simulation");
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
            if (_closed || _closing) return;

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
                            _closing = true;
                            _ = FireSpotCloseAsync(price, "TP");
                        }
                        return;
                    }
                }

                if (_cfg.SlEnabled)
                {
                    bool slHit = isLong ? price <= effectiveSl : price >= effectiveSl;
                    if (slHit) { _closing = true; _ = FireSpotCloseAsync(price, "SL"); return; }
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
                        _closing = true;
                        _ = FireFuturesCloseAsync(price, "TP");
                        return;
                    }
                }

                if (_cfg.SlEnabled)
                {
                    bool slHit = isLong ? price <= effectiveSl : price >= effectiveSl;
                    if (slHit) { _closing = true; _ = FireFuturesCloseAsync(price, "SL"); return; }
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
        // A tick that finds the semaphore taken must NOT just drop its update: _currentSlPrice has
        // already been moved by the caller, so the exchange order would stay behind the software
        // level forever. Record it as pending and let the holder replay it before releasing.
        lock (_lock) { _pendingSlPrice = newSlPrice; _pendingSlFrom = oldSlPrice; }
        if (!await _slUpdateSem.WaitAsync(0)) return;
        try
        {
            while (true)
            {
                decimal target, from;
                lock (_lock)
                {
                    if (_pendingSlPrice is null) return;
                    target = _pendingSlPrice.Value;
                    from = _pendingSlFrom;
                    _pendingSlPrice = null;
                }

                var previousOrderId = _slOrderId;
                var closeSide = _side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

                try
                {
                    // Place BEFORE cancelling. The old order sequence cancelled first, so a failure
                    // to place left the position with no stop on the exchange at all, and the
                    // software fallback stays disabled while _usingExchangeTpSl is true.
                    var slOrder = await _gateway!.PlaceStopLossOrderAsync(
                        _symbol, closeSide, _remainingQty, target, _posSide, reduceOnly: true);
                    _slOrderId = slOrder.Id;

                    if (previousOrderId is not null)
                    {
                        try { await _gateway!.CancelOrderAsync(_symbol, previousOrderId); }
                        catch { /* may already have fired */ }
                    }

                    OnEvent?.Invoke($"Trailing SL: {from:N4} → {target:N4}");
                }
                catch (Exception ex)
                {
                    // The old stop is still live because we never cancelled it. Roll the software
                    // level back to match what the exchange actually holds, so the two agree.
                    lock (_lock) { _currentSlPrice = from; }
                    OnEvent?.Invoke($"Trailing SL update failed, keeping stop at {from:N4}: {ex.Message}");

                    if (previousOrderId is null)
                    {
                        // Nothing is protecting the position on the exchange — drop to the software
                        // stop rather than pretending a native one exists.
                        _usingExchangeTpSl = false;
                        OnProtectionLost?.Invoke($"{_symbol}: could not place the trailing stop ({ex.Message}). Switched to the in-app stop — keep the terminal open.");
                    }
                    return;
                }
            }
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
            lock (_lock) { _closed = true; _closing = false; }
            OnEvent?.Invoke($"[{reason}] Spot closed @ {price:N4}");
        }
        catch (Exception ex)
        {
            // Re-arm rather than latch closed: the trigger will fire again on the next tick.
            lock (_lock) { _closing = false; }
            OnEvent?.Invoke($"[{reason}] Spot close failed: {ex.Message}");
            OnProtectionLost?.Invoke($"{_symbol}: {reason} triggered but the close order failed ({ex.Message}). Position is still open — close it manually.");
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
            lock (_lock) { _closed = true; _closing = false; }
            OnEvent?.Invoke($"[{reason}] Futures closed @ {price:N4}");
        }
        catch (Exception ex)
        {
            // Re-arm rather than latch closed: the trigger will fire again on the next tick.
            lock (_lock) { _closing = false; }
            OnEvent?.Invoke($"[{reason}] Futures close failed: {ex.Message}");
            OnProtectionLost?.Invoke($"{_symbol}: {reason} triggered but the close order failed ({ex.Message}). Position is still open — close it manually.");
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
