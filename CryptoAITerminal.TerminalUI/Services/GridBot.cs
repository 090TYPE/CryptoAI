using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Grid trading bot. Places limit buy/sell orders at evenly-spaced price levels.
/// When a buy fills → places sell one level above. When a sell fills → places buy one level below.
/// Each completed buy→sell cycle increments CyclesCompleted and accumulates GridPnL.
/// Supports Pause (cancels orders, keeps positions) / Resume (re-places orders).
/// </summary>
public sealed class GridBot : IDisposable
{
    private readonly IExchangeGateway _gateway;
    private readonly GridBotConfig _cfg;

    // orderId → level index in _gridPrices[]
    private readonly ConcurrentDictionary<string, int> _activeBuyOrders = new();
    private readonly ConcurrentDictionary<string, int> _activeSellOrders = new();

    private decimal[] _gridPrices = [];
    private decimal _spacing;
    private Timer? _pollTimer;
    private volatile bool _isPaused;
    private volatile bool _isStopped;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    // Стандартная taker-комиссия Binance (0.1% на сторону).
    private const decimal FeeRatePerSide = 0.001m;

    public int CyclesCompleted { get; private set; }
    public decimal GridPnL { get; private set; }
    public bool IsRunning => !_isStopped && !_isPaused;
    public bool IsPaused => _isPaused;

    public event Action<string>? OnLog;
    public event Action? OnStatsChanged;
    /// <summary>Fired each time a full buy→sell cycle completes. Args: symbol, buyPrice, sellPrice, qty, profit.</summary>
    public event Action<string, decimal, decimal, decimal, decimal>? OnCycleCompleted;

    public GridBot(IExchangeGateway gateway, GridBotConfig cfg)
    {
        _gateway = gateway;
        _cfg = cfg;
    }

    public async Task StartAsync()
    {
        _isStopped = false;
        _isPaused = false;
        CyclesCompleted = 0;
        GridPnL = 0m;

        _spacing = (_cfg.UpperPrice - _cfg.LowerPrice) / _cfg.GridLevels;
        _gridPrices = new decimal[_cfg.GridLevels + 1];
        for (int i = 0; i <= _cfg.GridLevels; i++)
            _gridPrices[i] = _cfg.LowerPrice + _spacing * i;

        await _gateway.ConnectAsync();

        if (_cfg.MarketType == TradingMarketType.FuturesUsdM)
        {
            try { await _gateway.SetLeverageAsync(_cfg.Symbol, _cfg.Leverage); } catch { }
            try { await _gateway.SetMarginModeAsync(_cfg.Symbol, _cfg.MarginMode); } catch { }
        }

        decimal currentPrice = await GetCurrentPriceAsync();
        OnLog?.Invoke($"Started · price {currentPrice:N4} · range {_cfg.LowerPrice:N4}–{_cfg.UpperPrice:N4} · spacing {_spacing:N4} · {_cfg.GridLevels} levels");

        await PlaceInitialOrdersAsync(currentPrice);

        // Не передаём async-лямбду в Timer — это эквивалент async void и
        // непойманное исключение крашит процесс. Оборачиваем в SafePollAsync.
        _pollTimer = new Timer(_ => { _ = SafePollAsync(); }, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task SafePollAsync()
    {
        try { await PollFillsAsync(); }
        catch (Exception ex) { OnLog?.Invoke($"Poll error: {ex.Message}"); }
    }

    private async Task PlaceInitialOrdersAsync(decimal currentPrice)
    {
        int buyCount = 0, sellCount = 0;

        for (int i = 0; i < _gridPrices.Length - 1; i++)
        {
            decimal bottom = _gridPrices[i];
            decimal top = _gridPrices[i + 1];

            if (top <= currentPrice)
            {
                // This cell is entirely below current price → place buy at bottom
                await PlaceBuyAtLevelAsync(i);
                buyCount++;
            }
            else if (bottom >= currentPrice && _cfg.MarketType == TradingMarketType.FuturesUsdM)
            {
                // Futures only: sell (short entry) above current price
                await PlaceSellAtLevelAsync(i + 1);
                sellCount++;
            }
        }

        OnLog?.Invoke($"Orders placed: {buyCount} buys, {sellCount} sells");
    }

    private async Task PlaceBuyAtLevelAsync(int levelIndex)
    {
        if (_isStopped || levelIndex < 0 || levelIndex >= _gridPrices.Length) return;

        decimal price = _gridPrices[levelIndex];
        try
        {
            var order = new Order
            {
                Symbol = _cfg.Symbol,
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Price = price,
                Quantity = _cfg.QuantityPerGrid,
                MarketType = _cfg.MarketType,
                Leverage = _cfg.MarketType == TradingMarketType.FuturesUsdM ? _cfg.Leverage : null,
                MarginMode = _cfg.MarginMode,
                PositionSide = _cfg.MarketType == TradingMarketType.FuturesUsdM
                    ? FuturesPositionSide.Long
                    : FuturesPositionSide.Both
            };
            var placed = await _gateway.PlaceOrderAsync(order);
            _activeBuyOrders[placed.Id] = levelIndex;
            OnLog?.Invoke($"Buy L{levelIndex} @ {price:N4}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Buy L{levelIndex} failed: {ex.Message}");
        }
    }

    private async Task PlaceSellAtLevelAsync(int levelIndex)
    {
        if (_isStopped || levelIndex <= 0 || levelIndex >= _gridPrices.Length) return;

        decimal price = _gridPrices[levelIndex];
        try
        {
            var order = new Order
            {
                Symbol = _cfg.Symbol,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Price = price,
                Quantity = _cfg.QuantityPerGrid,
                MarketType = _cfg.MarketType,
                Leverage = _cfg.MarketType == TradingMarketType.FuturesUsdM ? _cfg.Leverage : null,
                MarginMode = _cfg.MarginMode,
                // For Futures: close the Long position opened by the corresponding buy
                PositionSide = _cfg.MarketType == TradingMarketType.FuturesUsdM
                    ? FuturesPositionSide.Long
                    : FuturesPositionSide.Both,
                ReduceOnly = _cfg.MarketType == TradingMarketType.FuturesUsdM
            };
            var placed = await _gateway.PlaceOrderAsync(order);
            _activeSellOrders[placed.Id] = levelIndex;
            OnLog?.Invoke($"Sell L{levelIndex} @ {price:N4}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Sell L{levelIndex} failed: {ex.Message}");
        }
    }

    private async Task PollFillsAsync()
    {
        if (_isStopped || _isPaused) return;
        if (!await _pollLock.WaitAsync(0)) return;

        try
        {
            var openOrders = await _gateway.GetOpenOrdersAsync(_cfg.Symbol);
            var openIds = openOrders.Select(o => o.Id).ToHashSet();

            // Detect filled buys
            var filledBuys = _activeBuyOrders.Keys.Where(id => !openIds.Contains(id)).ToList();
            foreach (var id in filledBuys)
            {
                if (!_activeBuyOrders.TryRemove(id, out int lvl)) continue;
                OnLog?.Invoke($"Buy filled L{lvl} @ {_gridPrices[lvl]:N4} → placing sell");
                await PlaceSellAtLevelAsync(lvl + 1);
            }

            // Detect filled sells
            var filledSells = _activeSellOrders.Keys.Where(id => !openIds.Contains(id)).ToList();
            foreach (var id in filledSells)
            {
                if (!_activeSellOrders.TryRemove(id, out int lvl)) continue;

                decimal sellPrice = _gridPrices[lvl];
                decimal buyPrice = _gridPrices[lvl - 1];
                // Реальная прибыль с учётом комиссии: на спот/перпах Binance берёт ~0.1% taker
                // с каждой ноги (0.2% round-trip). Без вычета комиссии бот завышает PnL.
                decimal commission = (buyPrice + sellPrice) * _cfg.QuantityPerGrid * FeeRatePerSide;
                decimal profit = (sellPrice - buyPrice) * _cfg.QuantityPerGrid - commission;

                CyclesCompleted++;
                GridPnL += profit;
                OnLog?.Invoke($"Sell filled L{lvl} @ {sellPrice:N4} · cycle #{CyclesCompleted} · profit {profit:+0.0####;-0.0####} USDT");
                OnStatsChanged?.Invoke();
                OnCycleCompleted?.Invoke(_cfg.Symbol, buyPrice, sellPrice, _cfg.QuantityPerGrid, profit);

                await PlaceBuyAtLevelAsync(lvl - 1);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Poll error: {ex.Message}");
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public async Task PauseAsync()
    {
        _isPaused = true;
        OnLog?.Invoke("Pausing — cancelling open grid orders (positions stay open)...");
        await CancelAllOrdersAsync();
        OnLog?.Invoke("Grid paused.");
    }

    public async Task ResumeAsync()
    {
        if (!_isPaused) return;
        _isPaused = false;
        // Защита от устаревших записей: PauseAsync уже очистил словари,
        // но если возникла гонка с PollFillsAsync — гарантируем чистый старт.
        _activeBuyOrders.Clear();
        _activeSellOrders.Clear();
        OnLog?.Invoke("Resuming grid...");
        decimal currentPrice = await GetCurrentPriceAsync();
        await PlaceInitialOrdersAsync(currentPrice);
        OnLog?.Invoke($"Grid resumed · {_activeBuyOrders.Count} buys, {_activeSellOrders.Count} sells");
    }

    public async Task StopAsync()
    {
        _isStopped = true;
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        OnLog?.Invoke("Stopping — cancelling all orders...");
        await CancelAllOrdersAsync();
        await _gateway.DisconnectAsync();
        OnLog?.Invoke($"Stopped · cycles {CyclesCompleted} · total P&L {GridPnL:+0.00;-0.00} USDT");
    }

    private async Task CancelAllOrdersAsync()
    {
        // Сначала собираем ID, отменяем на бирже — и только потом удаляем из словарей.
        // Иначе PollFillsAsync между Clear() и CancelOrderAsync решит, что эти ордера
        // исполнены, и разместит новые на тех же уровнях — параллельно с теми,
        // которые мы ещё не успели отменить.
        var ids = _activeBuyOrders.Keys.Concat(_activeSellOrders.Keys).ToList();

        foreach (var id in ids)
        {
            try { await _gateway.CancelOrderAsync(id); }
            catch { /* already filled or expired */ }
        }

        _activeBuyOrders.Clear();
        _activeSellOrders.Clear();
    }

    private async Task<decimal> GetCurrentPriceAsync()
    {
        try
        {
            var book = await _gateway.GetOrderBookAsync(_cfg.Symbol, 1);
            var bid = book.Bids.FirstOrDefault()?.Price ?? 0m;
            var ask = book.Asks.FirstOrDefault()?.Price ?? 0m;
            if (bid > 0 && ask > 0) return (bid + ask) / 2m;
            if (bid > 0) return bid;
            if (ask > 0) return ask;
        }
        catch { }

        return (_cfg.LowerPrice + _cfg.UpperPrice) / 2m;
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _pollLock.Dispose();
    }
}
