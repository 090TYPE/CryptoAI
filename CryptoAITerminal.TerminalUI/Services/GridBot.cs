using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Core.Trading;
using CryptoAITerminal.Gateway.Base;
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
    // Futures account mode. Default one-way (retail default); flips to hedge on the first
    // "position side mismatch" reject and retries, so the grid works on either account mode.
    private bool _isHedgeMode;

    private FuturesPositionSide GridPositionSide() =>
        _cfg.MarketType == TradingMarketType.FuturesUsdM
            ? (_isHedgeMode ? FuturesPositionSide.Long : FuturesPositionSide.Both)
            : FuturesPositionSide.Both;

    private volatile bool _isStopped;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    private static readonly TimeSpan BasePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxPollInterval  = TimeSpan.FromMinutes(2);
    private TimeSpan _pollInterval = BasePollInterval;

    // Ордер, пропавший из open-orders, ещё не обязательно исполнен: ручная отмена, экспирация
    // или пустой ответ биржи при сбое дают тот же снимок. Держим id, пропавший первый раз,
    // и считаем филлом только пропажу, подтверждённую следующим опросом.
    // Читается и пишется только под _pollLock.
    private readonly HashSet<string> _missingOnce = [];

    // Стандартная taker-комиссия Binance (0.1% на сторону).
    private const decimal FeeRatePerSide = 0.001m;

    // Биржевые фильтры (tickSize/stepSize/minNotional) гейтвеи пока не отдают, поэтому шаг цены
    // выводим из точности границ диапазона, которые ввёл пользователь: сырой спейсинг
    // (Upper-Lower)/GridLevels даёт цену вида 1428.571428571428571428571429, биржа отвечает
    // -1013, ошибка уходит в лог, а бот продолжает считать себя работающим.
    private const int MaxPriceDecimals = 8;
    private const decimal QuantityStep = 0.00000001m;
    // Стандартный MIN_NOTIONAL Binance для пар с долларовым котируемым активом.
    private const decimal MinNotionalUsd = 5m;

    private int _priceDecimals;
    private decimal _quantity;

    /// <summary>
    /// The venue's real rules for this symbol, fetched once in <see cref="StartAsync"/>. Null when
    /// the exchange would not answer — everything below then falls back to the guesses this class
    /// used to make on its own (precision inferred from the typed bounds, a flat 1e-8 lot step and
    /// a hardcoded 5 USDT floor).
    /// </summary>
    private SymbolFilters? _filters;

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
        _quantity = FloorToStep(cfg.QuantityPerGrid, QuantityStep);
    }

    public async Task StartAsync()
    {
        _isStopped = false;
        _isPaused = false;
        CyclesCompleted = 0;
        GridPnL = 0m;

        // Ask the venue for the real rules before laying anything out. A REST call, no socket
        // needed, so it happens before ConnectAsync and before the validation below — the whole
        // point is to validate against the exchange's numbers rather than our guesses.
        try
        {
            _filters = await _gateway.GetSymbolFiltersAsync(_cfg.Symbol);
            if (_filters is { } f)
                OnLog?.Invoke($"{_cfg.Symbol} filters: tick {f.TickSize}, step {f.StepSize}, min qty {f.MinQuantity}, min notional {f.MinNotional}");
        }
        catch (Exception ex)
        {
            // Never block a start on this: falling back to the old guesses is worse than exact,
            // but far better than refusing to trade because a metadata endpoint hiccuped.
            OnLog?.Invoke($"Could not read {_cfg.Symbol} filters ({ex.Message}) — using inferred precision.");
        }

        _spacing = (_cfg.UpperPrice - _cfg.LowerPrice) / _cfg.GridLevels;
        _priceDecimals = ResolvePriceDecimals();
        _gridPrices = new decimal[_cfg.GridLevels + 1];
        for (int i = 0; i <= _cfg.GridLevels; i++)
            _gridPrices[i] = NormalizePrice(_cfg.LowerPrice + _spacing * i);

        // Re-floor the quantity now that the real lot step is known — the constructor could only
        // use the 1e-8 placeholder.
        _quantity = NormalizeQuantity(_cfg.QuantityPerGrid);

        // Отказываем до подключения и до первого ордера: иначе каждая лимитка уходит
        // на биржу и отбивается по minNotional, а бот остаётся в состоянии "running".
        if (_quantity <= 0m)
            throw new InvalidOperationException("Quantity per grid must be greater than zero.");

        if (_filters is { MinQuantity: > 0m } minQ && _quantity < minQ.MinQuantity)
            throw new InvalidOperationException(
                $"Quantity per grid {_quantity} is below the exchange minimum of {minQ.MinQuantity} {BaseAssetOf(_cfg.Symbol)}.");

        // The venue's own floor when it publishes one; the old hardcoded 5 USDT only otherwise,
        // and only for dollar-quoted pairs where that guess was ever meaningful.
        decimal levelNotional = _quantity * _cfg.LowerPrice;
        decimal minNotional = _filters is { MinNotional: > 0m } mn
            ? mn.MinNotional
            : IsUsdQuoted(_cfg.Symbol) ? MinNotionalUsd : 0m;

        if (minNotional > 0m && levelNotional < minNotional)
            throw new InvalidOperationException(
                $"Order notional {levelNotional:N2} at the lowest grid level is below the exchange minimum of {minNotional} — increase quantity per grid or raise the lower price.");

        await _gateway.ConnectAsync();

        if (_cfg.MarketType == TradingMarketType.FuturesUsdM)
        {
            // Биржа отбивает смену плеча и режима маржи, когда по символу уже есть позиция —
            // это штатно и не повод не стартовать. Но молчать нельзя: тогда сетка торгует на
            // том плече и режиме, что остались в аккаунте, а пользователь уверен, что на своих.
            try { await _gateway.SetLeverageAsync(_cfg.Symbol, _cfg.Leverage); }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠ Leverage {_cfg.Leverage}× not applied ({ex.Message}) — the account's current leverage stays in force.");
            }

            try { await _gateway.SetMarginModeAsync(_cfg.Symbol, _cfg.MarginMode); }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠ Margin mode {_cfg.MarginMode} not applied ({ex.Message}) — the account's current mode stays in force.");
            }
        }

        decimal currentPrice = await GetCurrentPriceAsync();
        OnLog?.Invoke($"Started · price {currentPrice:N4} · range {_cfg.LowerPrice:N4}–{_cfg.UpperPrice:N4} · spacing {_spacing:N4} · {_cfg.GridLevels} levels");

        await PlaceInitialOrdersAsync(currentPrice);

        // Не передаём async-лямбду в Timer — это эквивалент async void и
        // непойманное исключение крашит процесс. Оборачиваем в SafePollAsync.
        _pollInterval = BasePollInterval;
        _pollTimer = new Timer(_ => { _ = SafePollAsync(); }, null,
            _pollInterval, _pollInterval);
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
            // No init sells above price: this is a long-only grid, so there is no position to
            // reduce at startup. A reduce-only sell here is rejected by the exchange. Sells are
            // placed dynamically one level up when a buy fills (see PollFillsAsync).
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
                Quantity = _quantity,
                MarketType = _cfg.MarketType,
                Leverage = _cfg.MarketType == TradingMarketType.FuturesUsdM ? _cfg.Leverage : null,
                MarginMode = _cfg.MarginMode,
                PositionSide = GridPositionSide()
            };
            try
            {
                var placed = await _gateway.PlaceOrderAsync(order);
                _activeBuyOrders[placed.Id] = levelIndex;
                OnLog?.Invoke($"Buy L{levelIndex} @ {price:N4}");
            }
            catch (Exception ex) when (ExchangeErrors.IsPositionSideMismatch(ex))
            {
                _isHedgeMode = !_isHedgeMode;
                order.PositionSide = GridPositionSide();
                OnLog?.Invoke($"Position-side mismatch — switched to {(_isHedgeMode ? "hedge" : "one-way")} mode and retrying buy L{levelIndex}.");
                var placed = await _gateway.PlaceOrderAsync(order);
                _activeBuyOrders[placed.Id] = levelIndex;
                OnLog?.Invoke($"Buy L{levelIndex} @ {price:N4}");
            }
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
                Quantity = _quantity,
                MarketType = _cfg.MarketType,
                Leverage = _cfg.MarketType == TradingMarketType.FuturesUsdM ? _cfg.Leverage : null,
                MarginMode = _cfg.MarginMode,
                // For Futures: reduce-only sell closes the long opened by the corresponding buy
                // (PositionSide.Long in hedge mode, Both in one-way).
                PositionSide = GridPositionSide(),
                ReduceOnly = _cfg.MarketType == TradingMarketType.FuturesUsdM
            };
            try
            {
                var placed = await _gateway.PlaceOrderAsync(order);
                _activeSellOrders[placed.Id] = levelIndex;
                OnLog?.Invoke($"Sell L{levelIndex} @ {price:N4}");
            }
            catch (Exception ex) when (ExchangeErrors.IsPositionSideMismatch(ex))
            {
                _isHedgeMode = !_isHedgeMode;
                order.PositionSide = GridPositionSide();
                OnLog?.Invoke($"Position-side mismatch — switched to {(_isHedgeMode ? "hedge" : "one-way")} mode and retrying sell L{levelIndex}.");
                var placed = await _gateway.PlaceOrderAsync(order);
                _activeSellOrders[placed.Id] = levelIndex;
                OnLog?.Invoke($"Sell L{levelIndex} @ {price:N4}");
            }
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

            // Пустой снимок при нескольких живых лимитках — это почти всегда сбой запроса
            // (гейтвеи возвращают [] вместо исключения), а не одновременный филл всей сетки.
            int tracked = _activeBuyOrders.Count + _activeSellOrders.Count;
            if (openIds.Count == 0 && tracked > 1)
            {
                _missingOnce.Clear();
                OnLog?.Invoke($"Poll skipped: exchange reported no open orders while the grid tracks {tracked} — snapshot treated as unreliable.");
                return;
            }

            // Оба списка считаем до размещения новых ордеров: иначе лимитка, выставленная
            // в этом же тике, окажется «отсутствующей» в уже снятом снимке open-orders.
            var filledBuys = ConfirmDisappeared(_activeBuyOrders.Keys, openIds);
            var filledSells = ConfirmDisappeared(_activeSellOrders.Keys, openIds);

            // Detect filled buys
            foreach (var id in filledBuys)
            {
                if (!_activeBuyOrders.TryRemove(id, out int lvl)) continue;
                OnLog?.Invoke($"Buy filled L{lvl} @ {_gridPrices[lvl]:N4} → placing sell");
                await PlaceSellAtLevelAsync(lvl + 1);
            }

            // Detect filled sells
            foreach (var id in filledSells)
            {
                if (!_activeSellOrders.TryRemove(id, out int lvl)) continue;

                decimal sellPrice = _gridPrices[lvl];
                decimal buyPrice = _gridPrices[lvl - 1];
                // Реальная прибыль с учётом комиссии: на спот/перпах Binance берёт ~0.1% taker
                // с каждой ноги (0.2% round-trip). Без вычета комиссии бот завышает PnL.
                decimal commission = (buyPrice + sellPrice) * _quantity * FeeRatePerSide;
                decimal profit = (sellPrice - buyPrice) * _quantity - commission;

                CyclesCompleted++;
                GridPnL += profit;
                OnLog?.Invoke($"Sell filled L{lvl} @ {sellPrice:N4} · cycle #{CyclesCompleted} · profit {profit:+0.0####;-0.0####} USDT");
                OnStatsChanged?.Invoke();
                OnCycleCompleted?.Invoke(_cfg.Symbol, buyPrice, sellPrice, _quantity, profit);

                await PlaceBuyAtLevelAsync(lvl - 1);
            }

            ApplyPollBackoff(rateLimited: false);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Poll error: {ex.Message}");
            ApplyPollBackoff(RestBackoff.IsRateLimit(ex));
        }
        finally
        {
            _pollLock.Release();
        }
    }

    /// <summary>
    /// Опрос open-orders — чтение, поэтому на 429 замедляемся, а не ретраим: биржа банит IP
    /// за игнор лимита, а следующий тик всё равно увидит тот же снимок. Размещение ордера
    /// не ретраится ни при каких условиях — повтор мог бы удвоить позицию.
    /// </summary>
    private void ApplyPollBackoff(bool rateLimited)
    {
        var next = rateLimited
            ? RestBackoff.Grow(_pollInterval, MaxPollInterval)
            : BasePollInterval;
        if (next == _pollInterval) return;

        _pollInterval = next;
        // Пауза и остановка владеют таймером сами — иначе поздний тик снова его заведёт.
        // Dispose мог случиться, пока этот проход был в полёте, — тогда менять нечего.
        if (!_isStopped && !_isPaused)
        {
            try { _pollTimer?.Change(next, next); }
            catch (ObjectDisposedException) { return; }
        }
        OnLog?.Invoke(rateLimited
            ? $"Rate limited by the exchange — polling fills every {next.TotalSeconds:0}s."
            : $"Rate limit cleared — polling fills every {next.TotalSeconds:0}s.");
    }

    /// <summary>
    /// Возвращает id, отсутствующие в снимке open-orders уже второй опрос подряд.
    /// Вызывается только под _pollLock.
    /// </summary>
    private List<string> ConfirmDisappeared(ICollection<string> trackedIds, HashSet<string> openIds)
    {
        var confirmed = new List<string>();
        foreach (var id in trackedIds)
        {
            if (openIds.Contains(id)) { _missingOnce.Remove(id); continue; }
            if (_missingOnce.Add(id)) continue; // пропал впервые — ждём подтверждения
            _missingOnce.Remove(id);
            confirmed.Add(id);
        }
        return confirmed;
    }

    public async Task PauseAsync()
    {
        _isPaused = true;
        // Таймер глушим до отмены ордеров: иначе уже запущенный тик увидит снятые
        // лимитки отсутствующими в open-orders и посчитает их исполненными.
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        OnLog?.Invoke("Pausing — cancelling open grid orders (positions stay open)...");
        await _pollLock.WaitAsync();
        try { await CancelAllOrdersAsync(); }
        finally { _pollLock.Release(); }
        OnLog?.Invoke("Grid paused.");
    }

    public async Task ResumeAsync()
    {
        if (!_isPaused) return;
        OnLog?.Invoke("Resuming grid...");

        // Всё восстановление — под _pollLock и при снятом флаге паузы только в самом конце:
        // тик, попавший между размещением лимиток и снятием флага, работал бы со снимком
        // open-orders, снятым до размещения, и объявил бы новые ордера исполненными.
        await _pollLock.WaitAsync();
        try
        {
            _activeBuyOrders.Clear();
            _activeSellOrders.Clear();
            _missingOnce.Clear();
            decimal currentPrice = await GetCurrentPriceAsync();
            await PlaceInitialOrdersAsync(currentPrice);
            _isPaused = false;
        }
        finally { _pollLock.Release(); }

        _pollTimer?.Change(_pollInterval, _pollInterval);
        OnLog?.Invoke($"Grid resumed · {_activeBuyOrders.Count} buys, {_activeSellOrders.Count} sells");
    }

    public async Task StopAsync()
    {
        _isStopped = true;
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        OnLog?.Invoke("Stopping — cancelling all orders...");
        // Под тем же локом, что и опрос: иначе отмена идёт параллельно тику,
        // который в этот момент читает те же словари и размещает новые лимитки.
        await _pollLock.WaitAsync();
        try { await CancelAllOrdersAsync(); }
        finally { _pollLock.Release(); }
        await _gateway.DisconnectAsync();
        OnLog?.Invoke($"Stopped · cycles {CyclesCompleted} · total P&L {GridPnL:+0.00;-0.00} USDT");
    }

    private async Task CancelAllOrdersAsync()
    {
        // Сначала собираем ID, отменяем на бирже — и только потом удаляем из словарей.
        // Иначе PollFillsAsync между Clear() и CancelOrderAsync решит, что эти ордера
        // исполнены, и разместит новые на тех же уровнях — параллельно с теми,
        // которые мы ещё не успели отменить.
        var ids = _activeBuyOrders.Keys.Concat(_activeSellOrders.Keys).ToHashSet();

        foreach (var id in ids)
        {
            try { await _gateway.CancelOrderAsync(id); }
            catch { /* already filled or expired */ }
        }

        _activeBuyOrders.Clear();
        _activeSellOrders.Clear();
        _missingOnce.Clear();

        // Сверяемся с биржей: отмена могла не дойти (сеть, 429), и тогда на бирже остаётся
        // живая лимитка, о которой бот уже забыл. Чужие ордера по символу не трогаем —
        // здесь только проверка своих.
        try
        {
            var stillOpen = await _gateway.GetOpenOrdersAsync(_cfg.Symbol);
            var leftovers = stillOpen.Where(o => ids.Contains(o.Id)).Select(o => o.Id).ToList();
            if (leftovers.Count > 0)
                OnLog?.Invoke($"⚠ {leftovers.Count} grid order(s) still open after cancel ({string.Join(", ", leftovers)}) — cancel them on the exchange.");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Could not verify cancellations: {ex.Message}");
        }
    }

    // ── Нормализация под фильтры биржи ────────────────────────────────────────
    // Минимальный вариант: полноценная загрузка exchangeInfo (PRICE_FILTER/LOT_SIZE/MIN_NOTIONAL)
    // требует нового метода в IExchangeGateway с кэшем на сессию.

    private static decimal FloorToStep(decimal value, decimal step)
        => step <= 0m ? value : Math.Floor(value / step) * step;

    /// <summary>
    /// Snaps a level price to the venue's tick size when it published one, and only falls back to
    /// rounding at the precision inferred from the typed bounds when it did not.
    /// </summary>
    private decimal NormalizePrice(decimal price)
        => _filters is { TickSize: > 0m }
            ? SymbolFilterMath.NormalizePrice(price, _filters, up: false)
            : Math.Round(price, _priceDecimals, MidpointRounding.AwayFromZero);

    /// <summary>Floors a quantity to the venue's lot step, or to the 1e-8 placeholder without one.</summary>
    private decimal NormalizeQuantity(decimal quantity)
        => _filters is { StepSize: > 0m }
            ? SymbolFilterMath.NormalizeQuantity(quantity, _filters)
            : FloorToStep(quantity, QuantityStep);

    /// <summary>Base asset of a pair, for messages — BTCUSDT → BTC.</summary>
    private static string BaseAssetOf(string symbol)
    {
        foreach (var quote in new[] { "USDT", "USDC", "BUSD", "FDUSD", "USD" })
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                return symbol[..^quote.Length];
        return symbol;
    }

    /// <summary>Число знаков после запятой, с которым значение задано (scale decimal-числа).</summary>
    private static int DecimalPlaces(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    /// <summary>
    /// Точность цены — как у введённых границ диапазона, но не грубее шага сетки:
    /// иначе после округления соседние уровни схлопнутся в одну цену.
    /// </summary>
    private int ResolvePriceDecimals()
    {
        int d = Math.Clamp(Math.Max(DecimalPlaces(_cfg.LowerPrice), DecimalPlaces(_cfg.UpperPrice)),
            0, MaxPriceDecimals);
        while (d < MaxPriceDecimals && _spacing < UnitOf(d)) d++;
        return d;
    }

    private static decimal UnitOf(int decimals)
    {
        decimal unit = 1m;
        for (int i = 0; i < decimals; i++) unit /= 10m;
        return unit;
    }

    /// <summary>Порог minNotional в долларах применим только к парам с долларовым котируемым активом.</summary>
    private static bool IsUsdQuoted(string symbol) =>
        symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
        || symbol.EndsWith("USDC", StringComparison.OrdinalIgnoreCase)
        || symbol.EndsWith("BUSD", StringComparison.OrdinalIgnoreCase)
        || symbol.EndsWith("FDUSD", StringComparison.OrdinalIgnoreCase);

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
        catch (Exception ex)
        {
            // По этой цене раскладывается вся стартовая лестница: уровни ниже становятся
            // покупками, выше — продажами. Середина диапазона — не рынок, а догадка, и если
            // реальная цена ушла за границы, сетка встанет не той стороной. Стартовать всё же
            // даём — отказ из-за одного сбойного запроса стакана хуже, — но не молча.
            OnLog?.Invoke($"⚠ Could not read {_cfg.Symbol} price ({ex.Message}) — assuming the middle of the range; grid sides may be placed wrong.");
        }

        return (_cfg.LowerPrice + _cfg.UpperPrice) / 2m;
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _pollLock.Dispose();
    }
}
