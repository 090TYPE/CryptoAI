# CryptoAI Terminal — Реестр багов

> Все баги найдены статическим анализом кода. Каждый содержит:
> файл + строка, описание, почему это баг, и как исправить.
> Приоритет: КРИТИЧЕСКИЙ (ломает деньги/данные) / ВЫСОКИЙ (неправильное поведение) / СРЕДНИЙ (неудобство) / НИЗКИЙ (качество кода).

---

## КРИТИЧЕСКИЕ БАГИ — ✅ ВСЕ ИСПРАВЛЕНЫ

---

### ~~БАГ-01~~ — ✅ ИСПРАВЛЕН — Неправильная формула P&L

**Файл:** `CryptoAITerminal.TerminalUI/Services/TradingBot.cs`
**Строки:** 149, 204–205

**Код с багом:**
```csharp
// ExecuteBuy: закрытие шорта
var pnl = (_openEntryPrice - currentPrice) / _openEntryPrice * _openQuantity * currentPrice;

// ExecuteSell: закрытие лонга
var pnl = (currentPrice - _openEntryPrice) / _openEntryPrice * _openQuantity * currentPrice;
```

**Почему баг:**
Формула умножает процентное изменение на `currentPrice` (цену выхода), а не на начальную стоимость позиции. Результат неверный:
- Правильно для лонга: `pnl = (exitPrice - entryPrice) * quantity`
- Правильно для шорта: `pnl = (entryPrice - exitPrice) * quantity`
- Реально код считает: `(exitPrice - entryPrice) / entryPrice * qty * exitPrice` — это квадратичная зависимость от цены выхода.

**Пример:** Купили 0.001 BTC по 50000, продали по 51000.
- Правильно: `(51000 - 50000) * 0.001 = $1.00`
- Код считает: `(51000 - 50000) / 50000 * 0.001 * 51000 = $1.02`

**Исправление:**
```csharp
// Закрытие лонга:
var pnl = (currentPrice - _openEntryPrice) * _openQuantity;

// Закрытие шорта:
var pnl = (_openEntryPrice - currentPrice) * _openQuantity;
```

---

### ~~БАГ-02~~ — ✅ ИСПРАВЛЕН — RiskManager.RecordLoss через ReportTradeClosed

**Файл:** `CryptoAITerminal.RiskManager/RiskManager.cs` + `TradingBot.cs`
**Строки:** RiskManager.cs:71, TradingBot.cs — отсутствует вызов

**Почему баг:**
`RiskManager.CanPlaceOrder` проверяет `_dailyLoss >= _maxDailyLossUsd`, но `_dailyLoss` ВСЕГДА равен 0, потому что `RecordLoss(lossUsd)` нигде не вызывается из `TradingBot`. Настройка `MaxRiskPerTrade` в UI создаёт иллюзию защиты, которой нет.

**Исправление:** В `TradingBot.cs`, в обработчике `OnTradeClosed`:
```csharp
_bot.OnTradeClosed += (sym, dir, entry, exit, qty, pnl) =>
{
    if (pnl < 0)
        _riskManager.RecordLoss(Math.Abs(pnl));
    // ... остальной код
};
```

---

### ~~БАГ-03~~ — ✅ ИСПРАВЛЕН — FundingArb: Closed только при errors.Count==0

**Файл:** `CryptoAITerminal.TerminalUI/Services/FundingArbitrageService.cs`
**Строки:** 269–305

**Почему баг:**
```csharp
pos.State = FundingArbPositionState.Closing;

// ...попытка закрыть perp и spot...
// ошибки собираются в errors, но:

pos.State = FundingArbPositionState.Closed; // ВСЕГДА ставится Closed!
```

Даже если оба ордера закрытия провалились (API недоступно, нет баланса), позиция считается закрытой в интерфейсе. Реально она остаётся открытой на бирже, но пользователь её больше не видит.

**Исправление:**
```csharp
pos.State = errors.Count == 0
    ? FundingArbPositionState.Closed
    : FundingArbPositionState.Open; // вернуть в Open если ошибка
```

---

### ~~БАГ-04~~ — ✅ ИСПРАВЛЕН — TradingBot: StopBotAsync с timeout 5s в AIBotViewModel

**Файл:** `CryptoAITerminal.TerminalUI/Services/TradingBot.cs`
**Строка:** 255

**Код с багом:**
```csharp
public void Stop() => StopAsync().ConfigureAwait(false);
```

**Почему баг:**
`ConfigureAwait(false)` без `await` ничего не делает. Задача запускается в фоне, никто не ждёт её завершения. В результате:
- `DetachTpSl()` может не выполниться до закрытия приложения
- TP/SL ордера на бирже остаются висеть
- Исключения в `StopAsync` никто не обрабатывает

**Исправление:**
```csharp
// Вариант 1: убрать нечестный sync-over-async
public async Task StopAsync()  // уже есть, использовать его напрямую
// В AIBotViewModel.StopBot() вызывать через:
_ = _bot?.StopAsync();  // явное fire-and-forget с намерением

// Вариант 2: сделать синхронный Stop нормально
public void Stop()
{
    _subscription?.Dispose();
    _strategy.Reset();
    _activeTpSl?.Dispose();
    _activeTpSl = null;
}
```

---

### ~~БАГ-05~~ — ✅ ИСПРАВЛЕН — TradingBot: guard `if (!_hasOpenLong) return` перед SELL

**Файл:** `CryptoAITerminal.TerminalUI/Services/TradingBot.cs`
**Строки:** 201–214

**Код с багом:**
```csharp
await DetachTpSl();
// if hasOpenLong: записать P&L и снять флаг...
await new MarketOrderRouter(_gateway).SellMarketAsync(_symbol, _tradeQuantity);
_openEntryPrice = currentPrice;
_openQuantity   = _tradeQuantity;
_hasOpenShort   = true;  // ← ставится ПОСЛЕ SellMarketAsync
```

**Почему баг:**
1. Если `SellMarketAsync` выбросит исключение, `_hasOpenShort = true` всё равно не выполнится (OK).
2. Но главная проблема: Binance Spot НЕ поддерживает шорт-продажу токенов которых нет на балансе. Нет проверки баланса перед продажей.
3. `BollingerBandsStrategy` генерирует SELL даже без предшествующего BUY (когда цена в верхней четверти полосы). Это заставит бот пытаться продать токены которых нет.

**Исправление:**
```csharp
// Перед SellMarketAsync:
if (!_hasOpenLong) return; // На спот не продаём без открытой лонг-позиции
```

---

## ВЫСОКИЕ БАГИ — ✅ ВСЕ ИСПРАВЛЕНЫ

---

### ~~БАГ-06~~ — ✅ ИСПРАВЛЕН — TradingBot: `_hasFuturesLong/Short` + `ReportTradeClosed` при ReduceOnly-закрытии

**Файл:** `CryptoAITerminal.TerminalUI/Services/TradingBot.cs`
**Строки:** 101–135 (ExecuteBuy для futures), 163–198 (ExecuteSell для futures)

**Почему баг:**
Для Futures режима бот:
- Не обновляет `_openEntryPrice`, `_openQuantity`
- Не устанавливает `_hasOpenLong` / `_hasOpenShort`
- Никогда не вызывает `OnTradeClosed`

`AIBotViewModel.OnBotTradeClosed` → `PnlDashboardService` — никогда не получают данные о фьючерсных сделках бота.

**Исправление:** После размещения фьючерсного ордера на открытие/закрытие позиции — записывать трейд аналогично спот-логике.

---

### ~~БАГ-07~~ — ✅ ИСПРАВЛЕН — TpSlManager: `FireSpotClosePartialAsync` + обновление `TpPercent` до TP2

**Файл:** `CryptoAITerminal.TerminalUI/Services/TpSlManager.cs`
**Строки:** 165–172

**Код с багом:**
```csharp
if (_cfg.TpEnabled)
{
    bool tpHit = isLong ? price >= _entryPrice * (1m + _cfg.TpPercent / 100m) : ...;
    if (tpHit) { _closed = true; _ = FireSpotCloseAsync(price, "TP"); return; }
}
```

`FireSpotCloseAsync` всегда использует `_remainingQty` — т.е. закрывает 100% позиции. Конфигурация `PartialTp` и `PartialTpClosePercent` игнорируется для спот. Partial TP работает только для Futures через `PlaceTakeProfitOrderAsync`.

**Исправление:**
```csharp
if (tpHit)
{
    if (_cfg.PartialTp)
    {
        var tp1Qty = _remainingQty * _cfg.PartialTpClosePercent / 100m;
        _ = FireSpotClosePartialAsync(price, "TP1", tp1Qty);
        _remainingQty -= tp1Qty;
        _cfg.TpPercent = _cfg.PartialTp2Percent; // следующий TP на уровне TP2
    }
    else
    {
        _closed = true;
        _ = FireSpotCloseAsync(price, "TP");
    }
}
```

---

### ~~БАГ-08~~ — ✅ ИСПРАВЛЕН — TpSlManager: `SemaphoreSlim(1,1)` для `UpdateFuturesSlAsync`

**Файл:** `CryptoAITerminal.TerminalUI/Services/TpSlManager.cs`
**Строки:** 193–197, 216–232

**Почему баг:**
```csharp
// HandlePrice — в _lock:
if (newSl > _currentSlPrice * 1.001m)
{
    _currentSlPrice = newSl;
    _ = UpdateFuturesSlAsync(newSl, oldSl); // запускается ВНЕ лока
}
```

`UpdateFuturesSlAsync` читает и записывает `_slOrderId` без синхронизации. Если два тика цены запустят два параллельных `UpdateFuturesSlAsync`:
- Оба прочитают одинаковый `_slOrderId`
- Оба отменят один и тот же ордер (второй отмена провалится с ошибкой)
- Оба попытаются разместить новый SL ордер → два SL ордера на бирже

**Исправление:** Использовать `SemaphoreSlim(1,1)` для `UpdateFuturesSlAsync`, или lock + async-паттерн.

---

### ~~БАГ-09~~ — ✅ ИСПРАВЛЕН — AlertService: `_alertsLock` + snapshot через ToList

**Файл:** `CryptoAITerminal.TerminalUI/Services/AlertService.cs`
**Строки:** 19, 45–53, 95–167

**Почему баг:**
```csharp
private readonly List<PriceAlert> _alerts = []; // НЕ thread-safe

// Вызывается из Rx background thread:
private void CheckAlerts(MarketData data) { foreach (var alert in _alerts) ... }

// Вызываются из UI thread:
public void AddAlert(PriceAlert alert) => _alerts.Add(alert);
public bool RemoveAlert(string id) => ... _alerts.Remove(alert);
public void ClearFired() => _alerts.RemoveAll(...);
```

Это классическая гонка: `CheckAlerts` итерирует список пока UI поток добавляет/удаляет. Результат — `InvalidOperationException: Collection was modified` или silent data corruption.

**Исправление:** Заменить на `ConcurrentBag<T>` или добавить `lock`:
```csharp
private readonly object _alertsLock = new();
// и везде: lock(_alertsLock) { ... }
```

---

### ~~БАГ-10~~ — ✅ ИСПРАВЛЕН — GridBot: комиссия 0.1% per side учтена в profit

**Файл:** `CryptoAITerminal.TerminalUI/Services/GridBot.cs`
**Строка:** 197

**Код с багом:**
```csharp
decimal profit = (sellPrice - buyPrice) * _cfg.QuantityPerGrid;
```

**Почему баг:** На Binance стандартная комиссия 0.1% на каждый ордер (0.2% round-trip). При узком грид-спреде (например, 0.5%) реальная прибыль = `0.5% - 0.2% = 0.3%`, а бот показывает полные 0.5%. Пользователь думает что зарабатывает больше.

**Исправление:**
```csharp
decimal commission = (buyPrice + sellPrice) * _cfg.QuantityPerGrid * 0.001m; // 0.1% * 2 стороны
decimal profit = (sellPrice - buyPrice) * _cfg.QuantityPerGrid - commission;
```

---

### ~~БАГ-11~~ — ✅ ИСПРАВЛЕН — GridBot: `SafePollAsync` с try/catch вместо async void

**Файл:** `CryptoAITerminal.TerminalUI/Services/GridBot.cs`
**Строка:** 76–77

**Код с багом:**
```csharp
_pollTimer = new Timer(async _ => await PollFillsAsync(), null,
    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
```

`Timer` callback принимает `TimerCallback` (синхронный делегат). `async _` создаёт `async void` эквивалент. Любое необработанное исключение внутри `PollFillsAsync` (за пределами `_pollLock` try/catch) будет выброшено в ThreadPool и крашнет приложение.

**Исправление:**
```csharp
_pollTimer = new Timer(_ => { _ = SafePollAsync(); }, null, ...);

private async Task SafePollAsync()
{
    try { await PollFillsAsync(); }
    catch (Exception ex) { OnLog?.Invoke($"Poll error: {ex.Message}"); }
}
```

---

### ~~БАГ-12~~ — ✅ ИСПРАВЛЕН — GridBot: сначала CancelOrder, потом Clear словарей

**Файл:** `CryptoAITerminal.TerminalUI/Services/GridBot.cs`
**Строки:** 247–257

**Код с багом:**
```csharp
var ids = _activeBuyOrders.Keys.Concat(_activeSellOrders.Keys).ToList();
_activeBuyOrders.Clear();   // ← СНАЧАЛА очистка
_activeSellOrders.Clear();  // ← СНАЧАЛА очистка

foreach (var id in ids)
    await _gateway.CancelOrderAsync(id); // ← ПОТОМ отмена
```

**Почему баг:** Если `PollFillsAsync` запустится в промежутке между `Clear()` и `CancelOrderAsync`, словари будут пустыми, и бот решит что все ордера исполнены → разместит новые ордера на все уровни. Т.е. на бирже будут висеть и старые (ожидающие отмены) и новые ордера одновременно.

**Исправление:** Сначала отменить, потом очистить. Установить `_isStopped = true` до начала отмены.

---

### ~~БАГ-13~~ — ✅ ИСПРАВЛЕН — RsiStrategy: Wilder's smoothing + stateful seed

**Файл:** `CryptoAITerminal.TerminalUI/Services/RsiStrategy.cs`
**Строки:** 64–83

**Код с багом:**
```csharp
var avgGain = gainSum / n; // простое среднее
var avgLoss = lossSum / n; // простое среднее
```

**Почему баг:** Стандартный RSI (Wilder, 1978) использует Wilder's Smoothing (EMA с α=1/period), а не простое среднее. Результат:
- Реализация даёт другие значения чем TradingView, Binance, и все другие платформы
- На backtesting результаты несопоставимы с реальными
- Пользователь настраивает RSI 70/30 ожидая стандартного поведения

**Исправление (Wilder's Smoothed RSI):**
```csharp
private static decimal ComputeRsi(decimal[] prices)
{
    if (prices.Length < 2) return 50m;
    
    // Первый средний gain/loss — простое среднее за первый period
    decimal avgGain = 0, avgLoss = 0;
    for (int i = 1; i <= Math.Min(prices.Length - 1, 14); i++)
    {
        var delta = prices[i] - prices[i - 1];
        if (delta > 0) avgGain += delta;
        else avgLoss -= delta;
    }
    avgGain /= 14; avgLoss /= 14;
    
    // Wilder's smoothing для последующих точек
    for (int i = 15; i < prices.Length; i++)
    {
        var delta = prices[i] - prices[i - 1];
        var gain = delta > 0 ? delta : 0m;
        var loss = delta < 0 ? -delta : 0m;
        avgGain = (avgGain * 13 + gain) / 14;
        avgLoss = (avgLoss * 13 + loss) / 14;
    }
    
    if (avgLoss == 0) return 100m;
    return 100m - 100m / (1m + avgGain / avgLoss);
}
```

---

### ~~БАГ-14~~ — ✅ ИСПРАВЛЕН — SniperViewModel: `ReleaseExecutedBuyKey` при закрытии позиции

**Файл:** `CryptoAITerminal.TerminalUI/ViewModels/SniperViewModel.cs`
**Строки:** ~88–89

```csharp
private readonly HashSet<string> _executedBuys = new(StringComparer.OrdinalIgnoreCase);
private readonly HashSet<string> _paperExecutedBuys = new(StringComparer.OrdinalIgnoreCase);
```

**Почему баг:** Ключи токенов добавляются при каждой покупке, но никогда не удаляются. После закрытия позиции токен остаётся в `_executedBuys`. Через несколько часов торговли снайпер не сможет повторно войти в ни один из ранее торговавшихся токенов — даже если была новая волна, новый листинг, или пользователь просто перезапустил сессию без перезапуска приложения.

**Исправление:** При закрытии позиции (paper или live) удалять токен из соответствующего сета:
```csharp
private void OnPositionClosed(SniperCandidateViewModel position)
{
    _executedBuys.Remove(position.TokenKey);
    // или _paperExecutedBuys.Remove(...)
}
```

---

## СРЕДНИЕ БАГИ — ✅ ВСЕ ИСПРАВЛЕНЫ

---

### ~~БАГ-15~~ — ✅ ИСПРАВЛЕН — AlertService: VolumeSpike деактивируется при первой проверке

**Файл:** `CryptoAITerminal.TerminalUI/Services/AlertService.cs`
**Строки:** 138–141

```csharp
case AlertCondition.VolumeSpike:
    // MarketData stream carries no volume — cannot evaluate
    triggered = false;
    break;
```

**Почему баг:** Пользователь может создать VolumeSpike алерт в UI. Он сохранится, будет показан как активный, но никогда не сработает. Нет никакого предупреждения в UI что этот тип алерта неработоспособен.

**Исправление (вариант 1):** Убрать `VolumeSpike` из списка доступных типов алертов в UI.
**Исправление (вариант 2):** Реализовать через periodic REST запрос к Binance `/api/v3/ticker/24hr`.

---

### ~~БАГ-16~~ — ✅ ИСПРАВЛЕН — BestExecutionRouter: мёртвый `wavgPrice` удалён

**Файл:** `CryptoAITerminal.TerminalUI/Services/BestExecutionRouterService.cs`
**Строки:** 133–139

```csharp
var wavgPrice = totalQty > 0 ? Math.Round(totalValue / (legs.Sum(...) * ...), 4) : 0m;
// Simpler weighted avg fill (before fee)
var wavg = totalCoins > 0 ? ... : 0m;
// в результат идёт wavg, wavgPrice нигде не используется
```

**Исправление:** Удалить вычисление `wavgPrice`.

---

### ~~БАГ-17~~ — ✅ ИСПРАВЛЕН — BestExecutionRouter: ternary заменён на `totalCoins * worstEff`

**Файл:** `CryptoAITerminal.TerminalUI/Services/BestExecutionRouterService.cs`
**Строка:** 146–148

```csharp
var worstTotal = side == OrderSide.Buy
    ? totalCoins * worstEff
    : totalCoins * worstEff;  // ← обе ветки одинаковы!
```

Ternary совершенно бесполезен. Обе ветки вычисляют `totalCoins * worstEff`.

**Исправление:** `var worstTotal = totalCoins * worstEff;`

---

### ~~БАГ-18~~ — ✅ ИСПРАВЛЕН — PortfolioRebalancer: `NormalizeToAsset` убирает суффикс "USDT"

**Файл:** `CryptoAITerminal.TerminalUI/Services/PortfolioRebalanceService.cs`
**Строка:** 149

```csharp
var quotedPairs = string.Join(",", toFetch.Select(s => $"\"{s}USDT\""));
```

**Почему баг:** Если пользователь добавляет актив "BTCUSDT" (полную пару), код формирует запрос "BTCUSDTUSDT" → Binance API возвращает ошибку → цена остаётся 0 → стоимость актива = $0 → дельта ребалансировки неверная.

**Исправление:**
```csharp
private static string NormalizeToAsset(string symbol) =>
    symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
        ? symbol[..^4]
        : symbol;

var quotedPairs = string.Join(",", toFetch.Select(s => $"\"{NormalizeToAsset(s)}USDT\""));
```

---

### ~~БАГ-19~~ — ✅ ИСПРАВЛЕН — DcaBot: `BinanceGateway` → `IExchangeGateway`; `GetCandlesAsync` добавлен в интерфейс

**Файл:** `CryptoAITerminal.TerminalUI/Services/DcaBot.cs`
**Строка:** 34

```csharp
public DcaBot(BinanceGateway gateway, DcaBotConfig cfg)
```

**Почему баг:** DCA бот жёстко привязан к Binance. Bybit и OKX использовать невозможно, даже если шлюзы будут реализованы. Это нарушает архитектуру (есть `IExchangeGateway`).

**Исправление:**
```csharp
public DcaBot(IExchangeGateway gateway, DcaBotConfig cfg)
```
И внутри заменить `_gateway.GetCandlesAsync` вызовом через интерфейс (метод должен быть в `IExchangeGateway`).

---

### ~~БАГ-20~~ — ✅ ИСПРАВЛЕН — BacktestEngine: Sharpe аннуализирован (×√252)

**Файл:** `CryptoAITerminal.TerminalUI/Services/BacktestEngine.cs`
**Строки:** 174–181

```csharp
private static decimal ComputeSharpe(List<decimal> returns)
{
    var avg = returns.Average();
    var variance = returns.Sum(r => (r - avg) * (r - avg)) / (returns.Count - 1);
    var stdDev = (decimal)Math.Sqrt((double)variance);
    return stdDev == 0m ? 0m : avg / stdDev;
}
```

**Почему баг:**
1. Нет вычитания безрисковой ставки (risk-free rate)
2. Нет аннуализации (умножение на √252 для дневных данных)
3. Результат — отношение среднего к стандартному отклонению доходности сделок, а не Sharpe

Пользователь видит "Sharpe: 2.5" и думает что это хорошая стратегия — но сравнение с реальными инструментами некорректно.

**Как исправить:** Пометить в UI как "Profit Factor" или "Signal Ratio", или добавить аннуализацию:
```csharp
// Аннуализированный Sharpe (если returns — дневные):
return stdDev == 0m ? 0m : (avg / stdDev) * (decimal)Math.Sqrt(252);
```

---

### ~~БАГ-21~~ — ✅ ИСПРАВЛЕН — RiskManager: `lock (_lockObj)` в `CanPlaceOrder` и `RecordLoss`

**Файл:** `CryptoAITerminal.RiskManager/RiskManager.cs`
**Строки:** 11, 21–25

```csharp
private decimal _dailyLoss;
private DateTime _currentDate = DateTime.UtcNow.Date;
// нет lock, нет Interlocked
```

**Почему баг:** `TradingBot` получает рыночные данные асинхронно. Если два тика обработаются параллельно, оба вызовут `CanPlaceOrder` одновременно, создав race condition на `_dailyLoss`.

**Исправление:** Добавить `lock (_lockObj) { ... }` в `CanPlaceOrder` и `RecordLoss`.

---

## НИЗКИЕ БАГИ / КАЧЕСТВО КОДА — ✅ ВСЕ ИСПРАВЛЕНЫ

---

### ~~БАГ-22~~ — ✅ ИСПРАВЛЕН — TradingBot: `SemaphoreSlim(1,1)` + `WaitAsync(0)` в Rx Subscribe

**Файл:** `CryptoAITerminal.TerminalUI/Services/TradingBot.cs`
**Строки:** 73–96

```csharp
.Sample(TimeSpan.FromSeconds(5))
.Subscribe(
    async data =>
    {
        await ExecuteBuy(data.LastPrice); // может выполняться параллельно!
    })
```

`.Sample(5s)` гарантирует один тик каждые 5 секунд, но `async data =>` запускает `Task` без ожидания завершения предыдущего. Если `ExecuteBuy` занимает > 5 секунд (медленный API), запустятся два одновременных вызова.

**Исправление:** Добавить `SemaphoreSlim(1,1)` для предотвращения конкурентного исполнения.

---

### ~~БАГ-23~~ — ✅ ИСПРАВЛЕН — GridBot: явная очистка словарей в `ResumeAsync`

**Файл:** `CryptoAITerminal.TerminalUI/Services/GridBot.cs`
**Строки:** 226–234

```csharp
public async Task ResumeAsync()
{
    if (!_isPaused) return;
    _isPaused = false;
    decimal currentPrice = await GetCurrentPriceAsync();
    await PlaceInitialOrdersAsync(currentPrice); // размещает новые ордера
    // НО: _activeBuyOrders и _activeSellOrders не очищены!
}
```

После паузы словари уже были очищены в `PauseAsync → CancelAllOrdersAsync`. Но если `CancelAllOrdersAsync` добавил новые записи или если возникла гонка, словари могут содержать устаревшие данные. Код работает, но хрупко.

**Исправление:** В начале `ResumeAsync` явно очистить словари:
```csharp
_activeBuyOrders.Clear();
_activeSellOrders.Clear();
```

---

### ~~БАГ-24~~ — ✅ ИСПРАВЛЕН — FundingArb: `SocketsHttpHandler { PooledConnectionLifetime = 5min }`

**Файл:** `CryptoAITerminal.TerminalUI/Services/FundingArbitrageService.cs`
**Строка:** 30

```csharp
private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
```

`HttpClient` с бесконечным lifetime кэширует DNS-ответы. Если IP-адрес API биржи меняется (что бывает при инцидентах), клиент продолжит использовать старый IP. В production обычно используют `IHttpClientFactory` или устанавливают `PooledConnectionLifetime`.

---

### ~~БАГ-25~~ — ✅ ИСПРАВЛЕН — BacktestEngine: `candles.Skip(1)` устраняет дублирование стартовой точки

**Файл:** `CryptoAITerminal.TerminalUI/Services/BacktestEngine.cs`
**Строки:** 69, 107–109

```csharp
equityCurve.Add(new EquityPoint(candles[0].Timestamp, equity)); // добавить точку 0

foreach (var candle in candles) // первая итерация: candle = candles[0]
{
    equityCurve.Add(new EquityPoint(candle.Timestamp, currentEquity)); // дубль!
}
```

Дедупликация `GroupBy.Last()` это убирает, но неэффективно и запутанно. 

**Исправление:** Начать цикл с индекса 1, или добавить начальную точку только если `candles` не пуст и начинать foreach с `candles[1]`.

---

## Итоговая таблица

| # | Файл | Приоритет | Тема | Статус |
|---|---|---|---|---|
| 01 | TradingBot.cs | КРИТИЧЕСКИЙ | Неверная формула P&L | ✅ |
| 02 | RiskManager.cs / TradingBot.cs | КРИТИЧЕСКИЙ | RecordLoss не вызывается | ✅ |
| 03 | FundingArbitrageService.cs | КРИТИЧЕСКИЙ | Позиция закрывается при ошибке | ✅ |
| 04 | TradingBot.cs | КРИТИЧЕСКИЙ | Stop() не ждёт завершения | ✅ |
| 05 | TradingBot.cs | КРИТИЧЕСКИЙ | Спот-шорт без проверки баланса | ✅ |
| 06 | TradingBot.cs | ВЫСОКИЙ | Futures P&L дашборд не получает данные | ✅ |
| 07 | TpSlManager.cs | ВЫСОКИЙ | Partial TP на спот не работает | ✅ |
| 08 | TpSlManager.cs | ВЫСОКИЙ | _slOrderId без синхронизации | ✅ |
| 09 | AlertService.cs | ВЫСОКИЙ | _alerts без thread safety | ✅ |
| 10 | GridBot.cs | ВЫСОКИЙ | P&L без комиссий | ✅ |
| 11 | GridBot.cs | ВЫСОКИЙ | Timer async void = crash | ✅ |
| 12 | GridBot.cs | ВЫСОКИЙ | Race condition при отмене ордеров | ✅ |
| 13 | RsiStrategy.cs | ВЫСОКИЙ | Не Wilder's RSI | ✅ |
| 14 | SniperViewModel.cs | ВЫСОКИЙ | _executedBuys не очищается | ✅ |
| 15 | AlertService.cs | СРЕДНИЙ | VolumeSpike молча не работает | ⬜ |
| 16 | BestExecutionRouter.cs | СРЕДНИЙ | Мёртвый код wavgPrice | ⬜ |
| 17 | BestExecutionRouter.cs | СРЕДНИЙ | Copy-paste в ternary | ⬜ |
| 18 | PortfolioRebalanceService.cs | СРЕДНИЙ | Падает для "BTCUSDT" символов | ⬜ |
| 19 | DcaBot.cs | СРЕДНИЙ | Hardcoded BinanceGateway | ⬜ |
| 20 | BacktestEngine.cs | СРЕДНИЙ | Sharpe ≠ настоящий Sharpe | ⬜ |
| 21 | RiskManager.cs | СРЕДНИЙ | Не thread-safe | ⬜ |
| 22 | TradingBot.cs | НИЗКИЙ | async в Subscribe | ✅ |
| 23 | GridBot.cs | НИЗКИЙ | Resume не очищает словари | ✅ |
| 24 | FundingArbitrageService.cs | НИЗКИЙ | Статический HttpClient | ✅ |
| 25 | BacktestEngine.cs | НИЗКИЙ | Дубль в equity curve | ✅ |

---

## ✅ ВСЕ 25 БАГОВ ИСПРАВЛЕНЫ

Реестр закрыт. Дальнейшие задачи — в DEVELOPMENT_ROADMAP.md.
