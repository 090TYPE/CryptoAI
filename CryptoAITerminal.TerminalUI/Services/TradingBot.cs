using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.OrderRouter;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

public class TradingBot
{
    private readonly IExchangeGateway _gateway;
    private readonly RiskManager.RiskManager _riskManager;
    private readonly IStrategy _strategy;
    private readonly TpSlConfig _tpSlConfig;
    private readonly string _symbol;
    private readonly decimal _tradeQuantity;
    private readonly TradingMarketType _marketType;
    private readonly int _leverage;
    private readonly FuturesMarginMode _marginMode;
    private readonly FuturesTradeBias _futuresBias;
    private IDisposable? _subscription;
    private TpSlManager? _activeTpSl;
    private readonly SemaphoreSlim _executeSem = new(1, 1); // БАГ-22: предотвращает параллельные вызовы Execute*

    // Futures account state (устанавливается один раз при StartAsync).
    // _isHedgeMode=true → ордеры идут с PositionSide.Long/Short (Binance hedge, Bybit hedge).
    // _isHedgeMode=false → ордеры идут с PositionSide.Both (one-way / OKX net mode).
    // Определяется эмпирически: одна попытка с Long, если падает с "position side does not match" — fallback на Both.
    private bool _isHedgeMode = true;
    private bool _futuresAccountSetupDone;

    public event Action<string>? OnError;
    public event Action<string, decimal, decimal>? OnSignal;

    /// Fired when a round-trip trade is closed.
    /// Args: (symbol, direction, entryPrice, exitPrice, quantity, pnlUsd)
    public event Action<string, string, decimal, decimal, decimal, decimal>? OnTradeClosed;

    // Spot entry tracking
    private decimal _openEntryPrice;
    private decimal _openQuantity;
    private bool    _hasOpenLong;
    private bool    _hasOpenShort;

    // Futures entry tracking (БАГ-06)
    private decimal _futuresEntryPrice;
    private decimal _futuresEntryQty;
    private bool    _hasFuturesLong;
    private bool    _hasFuturesShort;

    public TradingBot(
        IExchangeGateway gateway,
        string symbol,
        decimal tradeQuantity,
        TradingMarketType marketType = TradingMarketType.Spot,
        int leverage = 1,
        decimal maxRiskPerTrade = 200m,
        FuturesMarginMode marginMode = FuturesMarginMode.Cross,
        FuturesTradeBias futuresBias = FuturesTradeBias.LongShort,
        IStrategy? strategy = null,
        TpSlConfig? tpSlConfig = null)
    {
        _gateway = gateway;
        _riskManager = new RiskManager.RiskManager(
            maxPositionSizeUsd: Math.Max(maxRiskPerTrade * 5, tradeQuantity * 50000),
            maxDailyLossUsd: maxRiskPerTrade);
        _strategy = strategy ?? new SimpleMaStrategy();
        _tpSlConfig = tpSlConfig ?? new TpSlConfig();
        _symbol = symbol;
        _tradeQuantity = tradeQuantity;
        _marketType = marketType;
        _leverage = Math.Max(1, leverage);
        _marginMode = marginMode;
        _futuresBias = futuresBias;
    }

    public async Task StartAsync()
    {
        await _gateway.ConnectAsync();

        if (_marketType == TradingMarketType.FuturesUsdM)
        {
            await EnsureFuturesAccountSetupAsync();
        }

        _subscription = _gateway.MarketDataStream
            .Where(data => string.Equals(data.Symbol, _symbol, StringComparison.OrdinalIgnoreCase))
            .Sample(TimeSpan.FromSeconds(5))
            .Subscribe(
                async data =>
                {
                    // БАГ-22: .Sample(5s) не гарантирует что предыдущий вызов завершился.
                    // WaitAsync(0) = non-blocking: пропускаем тик если уже идёт торговая операция.
                    if (!await _executeSem.WaitAsync(0)) return;
                    try
                    {
                        var (signal, confidence) = _strategy.Analyze(data);
                        if (signal == "BUY" && confidence > 0.3m)
                        {
                            OnSignal?.Invoke(signal, confidence, data.LastPrice);
                            await ExecuteBuy(data.LastPrice);
                        }
                        else if (signal == "SELL" && confidence > 0.3m)
                        {
                            OnSignal?.Invoke(signal, confidence, data.LastPrice);
                            await ExecuteSell(data.LastPrice);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke($"[{_symbol}] Execution error: {ex.Message}");
                    }
                    finally
                    {
                        _executeSem.Release();
                    }
                },
                ex => OnError?.Invoke($"[{_symbol}] Stream error: {ex.Message}")
            );
    }

    private async Task ExecuteBuy(decimal currentPrice)
    {
        if (_marketType == TradingMarketType.FuturesUsdM)
        {
            var balance = await _gateway.GetBalanceAsync("USDT");
            var position = await GetFuturesPositionAsync();
            var openExposure = Math.Abs(position?.Quantity ?? 0m) * currentPrice;

            if (position?.Quantity < 0)
            {
                // Close short first
                await DetachTpSl();
                var qty = Math.Min(_tradeQuantity, Math.Abs(position.Quantity));
                if (qty <= 0) return;
                await _gateway.PlaceOrderAsync(new Order
                {
                    Symbol = _symbol, Side = OrderSide.Buy, Type = OrderType.Market,
                    Quantity = qty, MarketType = _marketType, Leverage = _leverage,
                    MarginMode = _marginMode, ReduceOnly = true, PositionSide = position.PositionSide
                });
                // БАГ-06: report futures short close to PnL dashboard
                if (_hasFuturesShort)
                {
                    var pnl = (_futuresEntryPrice - currentPrice) * qty;
                    ReportTradeClosed("Short", _futuresEntryPrice, currentPrice, qty, pnl);
                    _hasFuturesShort   = false;
                    _futuresEntryPrice = 0m;
                    _futuresEntryQty   = 0m;
                }
                return;
            }

            if (_futuresBias == FuturesTradeBias.ShortOnly) return;

            var entrySide = EntryPositionSide(isBuy: true);
            var order = new Order
            {
                Symbol = _symbol, Side = OrderSide.Buy, Type = OrderType.Market,
                Quantity = _tradeQuantity, MarketType = _marketType, Leverage = _leverage,
                MarginMode = _marginMode, PositionSide = entrySide
            };

            if (!_riskManager.CanPlaceOrder(order, currentPrice, balance, openExposure)) return;

            try
            {
                await _gateway.PlaceOrderAsync(order);
            }
            catch (Exception ex) when (IsPositionSideMismatch(ex))
            {
                // Один раз перепрыгиваем mode (hedge ↔ one-way) и retry.
                _isHedgeMode = !_isHedgeMode;
                order.PositionSide = EntryPositionSide(isBuy: true);
                OnError?.Invoke($"[{_symbol}] Position-side mismatch — switched to {(_isHedgeMode ? "hedge" : "one-way")} mode and retrying.");
                await _gateway.PlaceOrderAsync(order);
            }
            // БАГ-06: track futures long entry
            _hasFuturesLong    = true;
            _futuresEntryPrice = currentPrice;
            _futuresEntryQty   = _tradeQuantity;
            await AttachTpSl(OrderSide.Buy, currentPrice, _tradeQuantity, order.PositionSide);
            return;
        }

        var spotBalance = await _gateway.GetBalanceAsync("USDT");
        var spotOrder = new Order
        {
            Symbol = _symbol, Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = _tradeQuantity, MarketType = _marketType
        };
        if (!_riskManager.CanPlaceOrder(spotOrder, currentPrice, spotBalance)) return;

        // Close any open short before recording new long
        if (_hasOpenShort)
        {
            var pnl = (_openEntryPrice - currentPrice) * _openQuantity;
            ReportTradeClosed("Short", _openEntryPrice, currentPrice, _openQuantity, pnl);
            _hasOpenShort = false;
        }

        await new MarketOrderRouter(_gateway).BuyMarketAsync(_symbol, _tradeQuantity);
        _openEntryPrice = currentPrice;
        _openQuantity   = _tradeQuantity;
        _hasOpenLong    = true;

        await AttachTpSl(OrderSide.Buy, currentPrice, _tradeQuantity, FuturesPositionSide.Both);
    }

    private async Task ExecuteSell(decimal currentPrice)
    {
        if (_marketType == TradingMarketType.FuturesUsdM)
        {
            var balance = await _gateway.GetBalanceAsync("USDT");
            var position = await GetFuturesPositionAsync();
            var openExposure = Math.Abs(position?.Quantity ?? 0m) * currentPrice;

            if (position?.Quantity > 0)
            {
                // Close long first
                await DetachTpSl();
                var qty = Math.Min(_tradeQuantity, Math.Abs(position.Quantity));
                if (qty <= 0) return;
                await _gateway.PlaceOrderAsync(new Order
                {
                    Symbol = _symbol, Side = OrderSide.Sell, Type = OrderType.Market,
                    Quantity = qty, MarketType = _marketType, Leverage = _leverage,
                    MarginMode = _marginMode, ReduceOnly = true, PositionSide = position.PositionSide
                });
                // БАГ-06: report futures long close to PnL dashboard
                if (_hasFuturesLong)
                {
                    var futuresPnl = (currentPrice - _futuresEntryPrice) * qty;
                    ReportTradeClosed("Long", _futuresEntryPrice, currentPrice, qty, futuresPnl);
                    _hasFuturesLong    = false;
                    _futuresEntryPrice = 0m;
                    _futuresEntryQty   = 0m;
                }
                return;
            }

            if (_futuresBias == FuturesTradeBias.LongOnly) return;

            var entrySide = EntryPositionSide(isBuy: false);
            var order = new Order
            {
                Symbol = _symbol, Side = OrderSide.Sell, Type = OrderType.Market,
                Quantity = _tradeQuantity, MarketType = _marketType, Leverage = _leverage,
                MarginMode = _marginMode, PositionSide = entrySide
            };

            if (!_riskManager.CanPlaceOrder(order, currentPrice, balance, openExposure)) return;

            try
            {
                await _gateway.PlaceOrderAsync(order);
            }
            catch (Exception ex) when (IsPositionSideMismatch(ex))
            {
                _isHedgeMode = !_isHedgeMode;
                order.PositionSide = EntryPositionSide(isBuy: false);
                OnError?.Invoke($"[{_symbol}] Position-side mismatch — switched to {(_isHedgeMode ? "hedge" : "one-way")} mode and retrying.");
                await _gateway.PlaceOrderAsync(order);
            }
            // БАГ-06: track futures short entry
            _hasFuturesShort   = true;
            _futuresEntryPrice = currentPrice;
            _futuresEntryQty   = _tradeQuantity;
            await AttachTpSl(OrderSide.Sell, currentPrice, _tradeQuantity, order.PositionSide);
            return;
        }

        // Spot не поддерживает шорт. SELL допустим только если есть открытая лонг-позиция —
        // тогда это её закрытие. Без _hasOpenLong (например, BollingerBandsStrategy выдаёт SELL
        // без предшествующего BUY) ордер был бы попыткой продать актив, которого нет на балансе.
        if (!_hasOpenLong) return;

        await DetachTpSl();

        var qtyToClose = _openQuantity > 0m ? _openQuantity : _tradeQuantity;
        await new MarketOrderRouter(_gateway).SellMarketAsync(_symbol, qtyToClose);

        var pnl = (currentPrice - _openEntryPrice) * qtyToClose;
        ReportTradeClosed("Long", _openEntryPrice, currentPrice, qtyToClose, pnl);

        _hasOpenLong   = false;
        _openEntryPrice = 0m;
        _openQuantity   = 0m;
    }

    private void ReportTradeClosed(string direction, decimal entry, decimal exit, decimal qty, decimal pnl)
    {
        if (pnl < 0m)
            _riskManager.RecordLoss(Math.Abs(pnl));
        OnTradeClosed?.Invoke(_symbol, direction, entry, exit, qty, pnl);
    }

    private async Task AttachTpSl(OrderSide side, decimal price, decimal qty, FuturesPositionSide posSide)
    {
        if (!_tpSlConfig.TpEnabled && !_tpSlConfig.SlEnabled) return;

        await DetachTpSl();

        var mgr = new TpSlManager(_tpSlConfig);
        mgr.OnEvent += msg => OnError?.Invoke($"[TP/SL] {msg}");
        _activeTpSl = mgr;

        await mgr.AttachAsync(_symbol, side, price, qty, posSide, _marketType, _gateway, _gateway.MarketDataStream);
    }

    private async Task DetachTpSl()
    {
        var mgr = _activeTpSl;
        _activeTpSl = null;
        if (mgr is not null)
        {
            await mgr.DetachAsync();
            mgr.Dispose();
        }
    }

    private async Task<FuturesPosition?> GetFuturesPositionAsync()
    {
        var positions = await _gateway.GetOpenPositionsAsync();
        return positions.FirstOrDefault(p =>
            string.Equals(p.Symbol, _symbol, StringComparison.OrdinalIgnoreCase) && p.Quantity != 0);
    }

    /// <summary>
    /// Применяет leverage и margin mode к futures-аккаунту биржи (по символу) ОДИН раз
    /// при старте бота. Без этого ордеры исполняются с дефолтным плечом/маржой биржи —
    /// то что юзер выбрал в UI игнорируется. Ошибки логируются но не валят запуск:
    /// многие биржи отклоняют SetLeverage если плечо уже установлено в то же значение.
    /// Также пробует определить hedge vs one-way mode: первый ордер с Long; если упадёт
    /// с position-side mismatch — переключаемся на one-way (Both).
    /// </summary>
    private async Task EnsureFuturesAccountSetupAsync()
    {
        if (_futuresAccountSetupDone) return;

        try { await _gateway.SetLeverageAsync(_symbol, _leverage); }
        catch (Exception ex) { OnError?.Invoke($"[{_symbol}] SetLeverage warning: {ex.Message}"); }

        try { await _gateway.SetMarginModeAsync(_symbol, _marginMode); }
        catch (Exception ex) { OnError?.Invoke($"[{_symbol}] SetMarginMode warning: {ex.Message}"); }

        // Определяем hedge vs one-way: пробуем получить позиции; если хоть одна имеет
        // PositionSide.Both — аккаунт в one-way mode.
        try
        {
            var positions = await _gateway.GetOpenPositionsAsync();
            if (positions.Any(p => p.PositionSide == FuturesPositionSide.Both))
                _isHedgeMode = false;
        }
        catch { /* default _isHedgeMode = true */ }

        _futuresAccountSetupDone = true;
    }

    /// <summary>
    /// Возвращает PositionSide для нового entry-ордера с учётом mode аккаунта.
    /// Hedge: Long → Long, Short → Short.
    /// One-way: всегда Both.
    /// </summary>
    private FuturesPositionSide EntryPositionSide(bool isBuy) =>
        _isHedgeMode
            ? (isBuy ? FuturesPositionSide.Long : FuturesPositionSide.Short)
            : FuturesPositionSide.Both;

    /// <summary>
    /// Эвристика: биржи возвращают разные сообщения при mismatch position-side mode.
    /// Binance: "position side does not match the user's settings".
    /// Bybit: "position idx not match position mode".
    /// OKX: "Position side mismatch" / 51124.
    /// </summary>
    private static bool IsPositionSideMismatch(Exception ex)
    {
        var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return msg.Contains("position side") || msg.Contains("position mode")
            || msg.Contains("position idx") || msg.Contains("51124");
    }

    public async Task StopAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        _strategy.Reset();
        await DetachTpSl().ConfigureAwait(false);
        await _gateway.DisconnectAsync().ConfigureAwait(false);
    }
}
