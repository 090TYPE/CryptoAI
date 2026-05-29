# CryptoAI Terminal — Дорожная карта развития

> Документ для себя: полная структура проекта, что уже есть, что сделать дальше
> и как подходить к каждой задаче. Обновлять по мере работы.

---

## 1. Что уже есть — карта модулей

```
CryptoAITerminal.Core            — модели, интерфейсы, перечисления
CryptoAITerminal.Gateway.Base    — базовый интерфейс IExchangeGateway
CryptoAITerminal.Gateway.Binance — Binance Spot + USD-M Futures
CryptoAITerminal.Gateway.Bybit   — Bybit Spot + Futures (каркас)
CryptoAITerminal.Gateway.OKX     — OKX Spot + Futures (каркас)
CryptoAITerminal.Gateway.DEX     — EVM (BSC/ETH/Base) + Solana (Jupiter) + Tron (SunSwap, live)
CryptoAITerminal.OrderRouter     — маршрутизатор ордеров между биржами
CryptoAITerminal.RiskManager     — риск-менеджер
CryptoAITerminal.AIEngine        — движок AI (стратегий)
CryptoAITerminal.WhaleTracker    — трекер крупных кошельков
CryptoAITerminal.Core.Tests      — unit-тесты
CryptoAITerminal.TerminalUI      — основное Avalonia-приложение (UI)
```

### Что реализовано в UI (TerminalUI)

| Функция | Статус |
|---|---|
| Trading Desk (ордера, ladder, hotkeys) | Готово |
| AI Bot (MA-стратегия, Spot + Futures) | Готово |
| Sniper DEX — мультичейн (BSC/ETH/Base/Solana/Tron live) | Готово |
| Sniper CEX Spot / Futures | Готово |
| Backtest Engine (RSI, Bollinger, Breakout, Walk-Forward) | Готово |
| Grid Bot | Готово |
| DCA Bot | Готово |
| Market Scanner | Готово |
| Funding Rate Arbitrage | Готово |
| Cross-Exchange Arbitrage | Готово |
| Best Execution Router | Готово |
| Portfolio Rebalancer | Готово |
| Whale Tracker | Готово |
| Sentiment Service | Готово |
| Liquidation Heatmap | Готово |
| Gas Monitor | Готово |
| On-Chain Metrics | Готово |
| News Feed | Готово |
| Telegram Notifications | Готово |
| Trade Journal | Готово |
| PnL Dashboard | Готово |
| Order Templates | Готово |
| Composite Rule Engine | Готово |
| Advanced Trailing Stop | Готово |
| Локализация EN/RU | Готово |
| System Tray (минимизация) | Готово |

---

## 2. Приоритетный список улучшений

### 2.1 Стратегии AI (ВЫСОКИЙ ПРИОРИТЕТ)

**Что сейчас:** Только `SimpleMaStrategy` (два MA, пересечение). RSI, Bollinger, Breakout есть только в backtesting-движке, но НЕ подключены к живому боту.

**Что сделать:**
1. Вынести `RsiStrategy`, `BollingerBandsStrategy`, `BreakoutStrategy` из сервисов бэктестинга в общий интерфейс `IStrategy` (он должен быть в `Core`).
2. Добавить в `AIBotViewModel` выпадающий список стратегий — пользователь выбирает какую стратегию запускать.
3. Добавить `MacdStrategy` — MACD это стандарт, многие ждут его.
4. Добавить `VwapStrategy` — очень популярна для futures внутридня.

**Как реализовать:**
```
CryptoAITerminal.Core/Interfaces/IStrategy.cs   ← добавить интерфейс
CryptoAITerminal.TerminalUI/Services/RsiStrategy.cs   ← уже есть, рефакторинг
CryptoAITerminal.TerminalUI/ViewModels/AIBotViewModel.cs  ← добавить SelectedStrategy
```

**Зачем:** Сейчас бот торгует только по MA, что неэффективно. RSI и Bollinger уже написаны — просто подключить.

---

### 2.2 Настоящий AI / ML движок (ВЫСОКИЙ ПРИОРИТЕТ)

**Что сейчас:** `AIEngine` существует как проект, но реального ML там нет. AI Bot — это просто правиловой бот с MA.

**Что сделать — 3 варианта (выбрать один):**

**Вариант A — Claude API (проще всего):**
- Отправлять свечи + индикаторы в Claude API
- Получать торговый сигнал в формате JSON: `{"signal": "buy", "confidence": 0.75, "reason": "..."}`
- Показывать reasoning в BotLog
- Пакет: `Anthropic.SDK` через NuGet

**Вариант B — ONNX модель (автономно):**
- Обучить LSTM/XGBoost на исторических данных (Python)
- Экспортировать в `.onnx`
- Загружать через `Microsoft.ML.OnnxRuntime`
- Предсказывать следующую свечу или направление

**Вариант C — Локальная LLM (Ollama):**
- Подключиться к Ollama REST API (localhost:11434)
- Загружать `llama3` или `mistral`
- Полностью автономно, без cloud

**Рекомендация:** Начать с Варианта A (Claude API) — быстро, объяснимо, уже есть пакет.

**Файлы для работы:**
```
CryptoAITerminal.AIEngine/   ← сюда класть логику
CryptoAITerminal.TerminalUI/ViewModels/AIBotViewModel.cs  ← стратегия "AI-Claude"
```

---

### 2.3 Bybit и OKX шлюзы — завершить (ВЫСОКИЙ ПРИОРИТЕТ)

**Что сейчас:** `Gateway.Bybit` и `Gateway.OKX` существуют как проекты, но их реализация неполная — в AIBotViewModel они принимаются как `IExchangeGateway?` с null-fallback на Binance.

**Что сделать:**
1. Проверить что реализовано в `Gateway.Bybit` и `Gateway.OKX`.
2. Реализовать WebSocket-поток рыночных данных (как в Binance).
3. Реализовать `PlaceOrderAsync`, `CancelOrderAsync`, `GetBalanceAsync`.
4. Протестировать переключение биржи в AI Bot и Trading Desk.

**Зачем:** Пользователи Bybit и OKX не могут торговать — они видят кнопки бирж, но реально торгует только Binance.

---

### 2.4 Улучшение Sniper — ML-ранжирование кандидатов (СРЕДНИЙ ПРИОРИТЕТ)

**Что сейчас:** Sniper фильтрует пары по захардкоженным правилам (ликвидность, возраст, momentum, риск-скор). Порядок в очереди — хронологический.

**Что сделать:**
1. Добавить `SniperRankingModel` — присваивает каждому кандидату `RankScore` 0-100.
2. `RankScore` считается из: momentum5m, liquidity, volume/liquidity ratio, пул-возраст, DEX quality score.
3. Отсортировать `AcceptedPairs` по `RankScore` убыванию.
4. Показывать `RankScore` в карточке кандидата.

**Как реализовать:**
```
CryptoAITerminal.TerminalUI/Services/SniperRankingModel.cs  ← новый файл
SniperViewModel.cs → метод SortAcceptedPairsByRank()
SniperCandidateViewModel.cs → добавить RankScore, RankScoreLabel
```

**Зачем:** Сейчас пары входят в очередь в случайном порядке. ML-ранжирование даст лучшие сигналы первыми.

---

### 2.5 Полноценный бэктест с оптимизацией (СРЕДНИЙ ПРИОРИТЕТ)

**Что сейчас:** `BacktestEngine` работает, `WalkForwardOptimizer` есть, но результаты отображаются только текстом. Нет графика equity curve в реальном времени.

**Что сделать:**
1. Подключить `PerformanceCurvePoints` к графику в BacktestView.
2. Добавить экспорт результатов в CSV.
3. Добавить `MonteCarloSimulator` — запускать стратегию N раз на случайных подмножествах данных, показывать доверительный интервал.
4. Добавить сравнение стратегий — backtest двух стратегий на одних данных, таблица сравнения.

**Зачем:** WalkForward есть, но пользователю неудобно читать числа — нужна визуализация.

---

### 2.6 Подключение реального AI к Sniper (СРЕДНИЙ ПРИОРИТЕТ)

**Что сейчас:** Sniper использует захардкоженные риск-правила (`SniperRiskPolicyService`). Есть `EnableExternalSecurityScan` флаг.

**Что сделать:**
1. Добавить `TokenSecurityAiService` — вызывает Claude API с данными токена (адрес, liquidity, volume, возраст пула, DEX).
2. Claude отвечает: `{"risk": 72, "red_flags": ["low liquidity", "new deployer"], "verdict": "AVOID"}`.
3. Показывать AI-вердикт в `LatestRiskNarrative`.
4. Использовать AI-вердикт как дополнительный фактор в риск-оценке (не заменять, а дополнять).

**Как реализовать:**
```
CryptoAITerminal.TerminalUI/Services/TokenSecurityAiService.cs  ← новый
SniperViewModel.cs → вызов в EvaluateRisk()
```

---

### 2.7 Крипто-новости с AI-анализом (СРЕДНИЙ ПРИОРИТЕТ)

**Что сейчас:** `NewsFeedService` и `NewsFeedViewModel` существуют. Вероятно, загружают RSS.

**Что сделать:**
1. Подключить MCP-сервер CoinDesk (уже доступен через `mcp__claude_ai_CoinDesk`).
2. Добавить AI-суммаризацию новостей через Claude — одно предложение на новость + `sentiment: bullish/bearish/neutral`.
3. Показывать sentiment-иконку рядом с каждой новостью.
4. Добавить `NewsSentimentScore` на основной дашборд — агрегированный сентимент за последний час.

---

### 2.8 Уведомления — расширить (НИЗКИЙ ПРИОРИТЕТ)

**Что сейчас:** `TelegramNotificationService` есть. System tray есть.

**Что сделать:**
1. Добавить Discord webhook — многие трейдеры используют Discord.
2. Добавить Email уведомления через SMTP (опционально).
3. Добавить push-уведомления через `ntfy.sh` — бесплатный open-source push.
4. В `AlertsViewModel` добавить выбор канала уведомлений (Telegram / Discord / Tray).

---

### 2.9 Мобильное сопровождение (НИЗКИЙ ПРИОРИТЕТ)

**Что сделать:** Добавить простой ASP.NET Core `CryptoAITerminal.WebApi` — REST API для просмотра позиций и отправки ордеров с телефона.

Эндпоинты минимум:
- `GET /api/positions` — все открытые позиции
- `GET /api/sniper/candidates` — кандидаты снайпера
- `POST /api/orders/market` — быстрый маркет-ордер
- `GET /api/pnl` — сводка P&L

Это позволит мониторить терминал с телефона через любой HTTP-клиент.

---

### 2.10 Производительность и надёжность (ПОСТОЯННО)

**Проблемы которые нужно решить:**

1. **`SniperViewModel.cs` — 4920 строк** — это слишком много для одного файла.
   Разбить на partial classes:
   ```
   SniperViewModel.Core.cs        — конструктор, поля
   SniperViewModel.Settings.cs    — load/save настроек
   SniperViewModel.Execution.cs   — логика покупки/продажи
   SniperViewModel.RiskChecks.cs  — проверки риска
   SniperViewModel.Telemetry.cs   — метрики и логи
   ```

2. **Локализация** — текущая система сканирует весь визуальный дерево каждую секунду через `_localizationScanTimer`.
   Переделать на `x:Uid` + `ResourceDictionary` — стандартный подход Avalonia.

3. **Тесты** — `CryptoAITerminal.Core.Tests` почти пустой (`UnitTest1.cs`).
   Добавить тесты для:
   - `RiskManager` — основные граничные условия
   - `BacktestEngine` — детерминированные результаты на синтетических данных
   - `SniperRiskPolicyService` — все emergency-stop сценарии

4. **Конфигурация API ключей** — сейчас `CredentialsService` хранит ключи локально.
   Добавить шифрование через `System.Security.Cryptography.ProtectedData` (DPAPI — стандарт Windows).

---

## 3. Архитектурные решения

### 3.1 Как добавить новую стратегию (пошагово)

```csharp
// Шаг 1: Core/Interfaces/IStrategy.cs (создать если нет)
public interface IStrategy
{
    string Name { get; }
    Signal Evaluate(IReadOnlyList<Candle> candles);
}

// Шаг 2: TerminalUI/Services/MacdStrategy.cs
public class MacdStrategy : IStrategy { ... }

// Шаг 3: AIBotViewModel.cs
public IReadOnlyList<string> AvailableStrategies { get; } = ["MA Cross", "RSI", "MACD", "Bollinger"];
private string _selectedStrategy = "MA Cross";

private IStrategy CreateStrategy() => SelectedStrategy switch
{
    "RSI"      => new RsiStrategy(RsiPeriod, RsiOverbought, RsiOversold),
    "MACD"     => new MacdStrategy(MacdFast, MacdSlow, MacdSignal),
    "Bollinger"=> new BollingerBandsStrategy(BbPeriod, BbDeviation),
    _          => new SimpleMaStrategy(MaFastPeriod, MaSlowPeriod)
};

// Шаг 4: Обновить TradingBot.cs — принять IStrategy вместо конкретного типа
```

---

### 3.2 Как добавить новую биржу (пошагово)

```
1. Создать CryptoAITerminal.Gateway.KuCoin/
2. Реализовать IExchangeGateway:
   - ConnectAsync / DisconnectAsync
   - PlaceOrderAsync / CancelOrderAsync
   - GetBalanceAsync
   - MarketDataStream (IObservable<MarketData>) через WebSocket
3. Добавить в AIBotViewModel.AvailableExchanges
4. Добавить в MainWindowViewModel конструктор
5. Добавить в CredentialsService хранение ключей
```

---

### 3.3 Как подключить Claude API (пошагово)

```bash
# Шаг 1: Установить пакет
dotnet add CryptoAITerminal.AIEngine package Anthropic.SDK

# Шаг 2: Создать AIEngine/ClaudeSignalProvider.cs
```

```csharp
// AIEngine/ClaudeSignalProvider.cs
public class ClaudeSignalProvider
{
    private readonly AnthropicClient _client;

    public async Task<AiSignal> GetSignalAsync(string symbol, IReadOnlyList<Candle> candles)
    {
        var prompt = BuildPrompt(symbol, candles);
        var response = await _client.Messages.CreateAsync(new MessageRequest
        {
            Model = "claude-sonnet-4-6",
            MaxTokens = 256,
            Messages = [new(RoleType.User, prompt)]
        });

        return ParseSignal(response.Content[0].Text);
    }

    private static string BuildPrompt(string symbol, IReadOnlyList<Candle> candles)
    {
        var last10 = candles.TakeLast(10).Select(c =>
            $"O:{c.Open:N4} H:{c.High:N4} L:{c.Low:N4} C:{c.Close:N4} V:{c.Volume:N2}");
        return $"""
            Symbol: {symbol}
            Last 10 candles (OHLCV):
            {string.Join("\n", last10)}

            Respond ONLY with JSON: {{"signal":"buy"|"sell"|"hold","confidence":0.0-1.0,"reason":"..."}}
            """;
    }
}
```

```
// Шаг 3: Добавить в AIBotViewModel опцию "AI (Claude)"
// Шаг 4: При выборе этой стратегии — вызывать ClaudeSignalProvider
// Шаг 5: Показывать reason в BotLog
```

---

### 3.4 Разбивка SniperViewModel (как сделать безопасно)

Файл 4920 строк — использовать `partial class`:

```csharp
// SniperViewModel.cs — оставить только конструктор + поля
public partial class SniperViewModel : ReactiveObject, IDisposable { }

// SniperViewModel.Properties.cs — все get/set свойства
public partial class SniperViewModel { ... }

// SniperViewModel.Commands.cs — ArmAsync, ScanAsync, BuyCandidateAsync
public partial class SniperViewModel { ... }

// SniperViewModel.RiskEngine.cs — BuildRiskSnapshot, EvaluateRisk
public partial class SniperViewModel { ... }

// SniperViewModel.Persistence.cs — LoadSettings, PersistSettings
public partial class SniperViewModel { ... }
```

Это НЕ изменяет поведение — только разбивает файл. Компилятор собирает в один класс.

---

## 4. Известные ограничения (не баги — архитектурные решения)

| Ограничение | Почему так | Как изменить |
|---|---|---|
| ~~Tron — только сканер, без live-торговли~~ | ✅ Закрыто: TronTradeGateway реализует IDexTradeGateway, подключён к SunSwap (swapExactETHForTokens / swapExactTokensForTokens), TRC20 баланс/allowance/approve, диагностика; включён в `ChainProfiles["tron"].SupportsLiveExecution = true`. | — |
| CEX Sniper live execution заблокирован | Намеренно — API ключи CEX более чувствительны | Разблокировать через дополнительный confirmation-диалог |
| Локализация через runtime-сканирование (оптимизировано) | Полный рефакторинг x:Uid отклонён пользователем (риск регрессий по 10546 строк MainWindow.axaml). Таймер останавливается после 3 стабильных тиков, перезапуск на LanguageChanged + SelectedShellSection. | Оставить как есть, либо когда-нибудь полный перевод на ResourceDictionary + x:Uid. |
| ~~Bybit/OKX — fallback на Binance~~ | ✅ Закрыто: Bybit Spot/Futures и OKX Spot/Futures реализуют IExchangeGateway (Connect/Place/Cancel/Balance/OrderBook/Candles/OpenOrders + Positions/Leverage/MarginMode для Futures). WebSocket: Bybit Spot — ticker + orderbook depth=1; OKX — ticker через UnifiedApi. | — |
| Bybit Futures + OKX Spot/Futures — не оттестированы live | Реализация завершена статически; не было сессии паперторговли с реальным ключом. | Включить в торговый workflow и проверить PlaceOrder / Cancel / Positions на testnet или малыми суммами. |
| WebApi без аутентификации | MVP для локального мониторинга; рассчитан на запуск в локальной сети или за tunnel-ом. | Добавить bearer-token или mTLS, если планируется выставлять в публичный интернет. |

---

## 5. Реестр багов

> Все баги найдены статическим анализом кода. Каждый содержит:
> файл + строка, описание, почему это баг, и как исправить.
> Приоритет: КРИТИЧЕСКИЙ (ломает деньги/данные) / ВЫСОКИЙ (неправильное поведение) / СРЕДНИЙ (неудобство) / НИЗКИЙ (качество кода).

---

### КРИТИЧЕСКИЕ БАГИ

---

#### БАГ-01 — Неправильная формула P&L (деньги считаются неверно)

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

#### БАГ-02 — RiskManager.RecordLoss никогда не вызывается — дневной лимит потерь не работает

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

#### БАГ-03 — FundingArb: позиция помечается Closed даже когда оба ордера провалились

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

#### БАГ-04 — TradingBot.Stop(): результат Task игнорируется (fire-and-forget)

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

#### БАГ-05 — TradingBot: Spot-шорт устанавливается даже когда ордер проваливается

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

### ВЫСОКИЕ БАГИ

---

#### БАГ-06 — TradingBot: Futures bot не обновляет P&L дашборд (OnTradeClosed не вызывается)

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

#### БАГ-07 — TpSlManager: Partial TP на Spot не работает (всегда закрывает всю позицию)

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

#### БАГ-08 — TpSlManager: _slOrderId не защищён локом при параллельных вызовах

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

#### БАГ-09 — AlertService: Thread safety (список алертов без синхронизации)

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

#### БАГ-10 — GridBot: P&L не учитывает комиссии

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

#### БАГ-11 — GridBot: Timer callback — async void — необработанные исключения крашат процесс

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

#### БАГ-12 — GridBot: Race condition — ID ордеров очищаются ДО отмены

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

#### БАГ-13 — RsiStrategy: Не использует Wilder's EMA (неправильный RSI)

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

#### БАГ-14 — SniperViewModel: _executedBuys никогда не очищается

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

### СРЕДНИЕ БАГИ

---

#### БАГ-15 — AlertService: VolumeSpike алерт никогда не срабатывает (молчит)

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

#### БАГ-16 — BestExecutionRouter: мёртвый код (wavgPrice не используется)

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

#### БАГ-17 — BestExecutionRouter: ternary с одинаковыми ветками (copy-paste)

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

#### БАГ-18 — PortfolioRebalancer: падает если пользователь вводит "BTCUSDT" вместо "BTC"

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

#### БАГ-19 — DcaBot: принимает только BinanceGateway, не IExchangeGateway

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

#### БАГ-20 — BacktestEngine: Sharpe Ratio не является стандартным Sharpe Ratio

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

#### БАГ-21 — RiskManager: не thread-safe

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

### НИЗКИЕ БАГИ / КАЧЕСТВО КОДА

---

#### БАГ-22 — TradingBot: async lambda в Rx Subscribe = потенциальные параллельные выполнения

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

#### БАГ-23 — GridBot: при Resume не очищаются старые отслеживаемые ордера

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

#### БАГ-24 — FundingArbitrageService: статический HttpClient никогда не обновляет DNS

**Файл:** `CryptoAITerminal.TerminalUI/Services/FundingArbitrageService.cs`
**Строка:** 30

```csharp
private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
```

`HttpClient` с бесконечным lifetime кэширует DNS-ответы. Если IP-адрес API биржи меняется (что бывает при инцидентах), клиент продолжит использовать старый IP. В production обычно используют `IHttpClientFactory` или устанавливают `PooledConnectionLifetime`.

---

#### БАГ-25 — BacktestEngine: начальная точка equity curve дублируется

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

### Итоговая таблица багов

| # | Файл | Приоритет | Тема | Статус |
|---|---|---|---|---|
| 01 | TradingBot.cs | КРИТИЧЕСКИЙ | Неверная формула P&L | ✅ Исправлен |
| 02 | RiskManager.cs / TradingBot.cs | КРИТИЧЕСКИЙ | RecordLoss не вызывается | ✅ Исправлен |
| 03 | FundingArbitrageService.cs | КРИТИЧЕСКИЙ | Позиция закрывается при ошибке | ✅ Исправлен |
| 04 | TradingBot.cs | КРИТИЧЕСКИЙ | Stop() не ждёт завершения | ✅ Исправлен |
| 05 | TradingBot.cs | КРИТИЧЕСКИЙ | Спот-шорт без проверки баланса | ✅ Исправлен |
| 06 | TradingBot.cs | ВЫСОКИЙ | Futures P&L дашборд не получает данные | ✅ Исправлен |
| 07 | TpSlManager.cs | ВЫСОКИЙ | Partial TP на спот не работает | ✅ Исправлен |
| 08 | TpSlManager.cs | ВЫСОКИЙ | _slOrderId без синхронизации | ✅ Исправлен |
| 09 | AlertService.cs | ВЫСОКИЙ | _alerts без thread safety | ✅ Исправлен |
| 10 | GridBot.cs | ВЫСОКИЙ | P&L без комиссий | ✅ Исправлен |
| 11 | GridBot.cs | ВЫСОКИЙ | Timer async void = crash | ✅ Исправлен |
| 12 | GridBot.cs | ВЫСОКИЙ | Race condition при отмене ордеров | ✅ Исправлен |
| 13 | RsiStrategy.cs | ВЫСОКИЙ | Не Wilder's RSI | ✅ Исправлен |
| 14 | SniperViewModel.cs | ВЫСОКИЙ | _executedBuys не очищается | ✅ Исправлен |
| 15 | AlertService.cs | СРЕДНИЙ | VolumeSpike молча не работает | ✅ Исправлен |
| 16 | BestExecutionRouter.cs | СРЕДНИЙ | Мёртвый код wavgPrice | ✅ Исправлен |
| 17 | BestExecutionRouter.cs | СРЕДНИЙ | Copy-paste в ternary | ✅ Исправлен |
| 18 | PortfolioRebalanceService.cs | СРЕДНИЙ | Падает для "BTCUSDT" символов | ✅ Исправлен |
| 19 | DcaBot.cs | СРЕДНИЙ | Hardcoded BinanceGateway | ✅ Исправлен |
| 20 | BacktestEngine.cs | СРЕДНИЙ | Sharpe ≠ настоящий Sharpe | ✅ Исправлен |
| 21 | RiskManager.cs | СРЕДНИЙ | Не thread-safe | ✅ Исправлен |
| 22 | TradingBot.cs | НИЗКИЙ | async в Subscribe | ✅ Исправлен |
| 23 | GridBot.cs | НИЗКИЙ | Resume не очищает словари | ✅ Исправлен |
| 24 | FundingArbitrageService.cs | НИЗКИЙ | Статический HttpClient | ✅ Исправлен |
| 25 | BacktestEngine.cs | НИЗКИЙ | Дубль в equity curve | ✅ Исправлен |

---

### Порядок исправления

**Неделя 1 — критические (затрагивают деньги): ✅ DONE**
- БАГ-01 (неверный P&L) — `(exit-entry)*qty`
- БАГ-02 (лимит потерь не работает) — `RecordLoss` через `ReportTradeClosed`
- БАГ-03 (FundingArb позиция теряется) — закрытие только при `errors.Count==0`
- БАГ-04 (Stop fire-and-forget) — `StopBotAsync` с timeout 5s
- БАГ-05 (спот-шорт без баланса) — guard `if (!_hasOpenLong) return`

**Неделя 2 — высокие: ✅ DONE**
- БАГ-09 (AlertService thread safety) — `_alertsLock` + snapshot через ToList
- БАГ-10 (GridBot P&L комиссии) — 0.1% per side учтена
- БАГ-11 (GridBot Timer crash) — `SafePollAsync` с try/catch
- БАГ-12 (GridBot race condition) — отмена до очистки словарей
- БАГ-13 (RSI неверный) — Wilder's smoothing + stateful seed
- БАГ-14 (Sniper executedBuys) — `ReleaseExecutedBuyKey` при закрытии
- БАГ-23 (GridBot Resume) — явная очистка словарей в ResumeAsync

**Неделя 3 — оставшиеся высокие: ✅ DONE**
- БАГ-06 (Futures P&L дашборд) — `_hasFuturesLong/Short` + `ReportTradeClosed` при ReduceOnly-закрытии
- БАГ-07 (Partial TP спот) — `FireSpotClosePartialAsync` + обновление `TpPercent` до TP2
- БАГ-08 (TpSlManager race condition) — `SemaphoreSlim(1,1)` для `UpdateFuturesSlAsync`

**Неделя 4 — средние баги: ✅ DONE**
- БАГ-15 (VolumeSpike молчит) — деактивация алерта при первой проверке
- БАГ-16/17 (BestExecutionRouter мёртвый код) — удалён `wavgPrice`, исправлен ternary
- БАГ-18 (PortfolioRebalancer "BTCUSDT") — `NormalizeToAsset` helper
- БАГ-19 (DcaBot hardcoded Binance) — `IExchangeGateway`; `GetCandlesAsync` добавлен в интерфейс
- БАГ-20 (Sharpe аннуализация) — умножить на √252
- БАГ-21 (RiskManager thread-safe) — `lock (_lockObj)` в `CanPlaceOrder`/`RecordLoss`

**Неделя 5 — низкие баги + разбивка SniperViewModel: ✅ DONE**
- БАГ-22 (TradingBot async Subscribe) — `SemaphoreSlim(1,1)` + `WaitAsync(0)`
- БАГ-24 (статический HttpClient DNS) — `SocketsHttpHandler { PooledConnectionLifetime = 5min }`
- БАГ-25 (equity curve дубль) — `candles.Skip(1)`
- SniperViewModel разбит на 5 partial файлов (итого ~4989 строк вместо одного 4360-строчного файла)

---

## 6. Технический долг — в каком порядке гасить

### Выполнено ✅
- [x] Добавить `IStrategy` интерфейс в `Core` + MA/RSI/Bollinger/Breakout/MACD + AI Claude
- [x] Разбить `SniperViewModel.cs` на partial-классы:
  - `SniperViewModel.cs` — 2688 строк (ядро: поля, конструктор, scan/arm)
  - `SniperViewModel.Execution.cs` — 976 строк (BuyCandidateAsync, EmergencyClose, TryExecuteExit и др.)
  - `SniperViewModel.RiskChecks.cs` — 725 строк (EvaluateRisk, EvaluateCexRisk, records)
  - `SniperViewModel.Presets.cs` — 257 строк
  - `SniperViewModel.Persistence.cs` — 343 строк
- [x] Завершить `Gateway.Bybit` — Spot orderbook depth=1 stream (BestBid/BestAsk)
- [x] Подключить Claude API — `ClaudeSignalProvider` + `ClaudeStrategy` в AIEngine
- [x] API ключи через DPAPI шифрование (`CredentialsService`)
- [x] Все 25 багов из реестра BUGS.md исправлены
- [x] Tron live execution в снайпере (`ChainProfiles["tron"].SupportsLiveExecution=true`)
- [x] Unit-тесты: 41 тест (RiskManager + BacktestEngine + MonteCarloSimulator + SniperRankingModel), все зелёные
- [x] Monte Carlo симулятор: `MonteCarloSimulator.Run` + `BacktestViewModel.MonteCarloCommand`
- [x] Discord webhook уведомления: `DiscordWebhookNotificationService` + `SendDiscord` в `PriceAlert`
- [x] ntfy.sh push: `NtfyNotificationService` + `SendNtfy` в `PriceAlert`; UI в Settings и Add Alert
- [x] SniperRankingModel: `Services/SniperRankingModel.cs` (momentum/liquidity/volume-ratio/age/dex-quality, 0–100, бэнды S/A/B/C/D) + сортировка `AcceptedPairs` в `SniperViewModel`
- [x] Граф equity curve в BacktestView (был сразу — `EquityGeometry` рисует Path в MainWindow.axaml)
- [x] Sidebar теперь скроллится (ScrollViewer вокруг навигационных кнопок)
- [x] Починены клики на Arb/Scanner/Router (отсутствовали кейсы в `NormalizeSectionKey` и `GetSectionDefinition`)

### Следующие приоритеты
- [x] Unit-тесты для `RiskManager` (13 тестов) и `BacktestEngine` (13 тестов). + 5 тестов `MonteCarloSimulator` + 10 `SniperRankingModel` + 18 `SniperRiskPolicyService`. Итого 59 тестов, все зелёные.
- [x] Tron live execution — `TronTradeGateway` подключён к снайперу, `SupportsLiveExecution=true` для `tron` chain profile
- [x] Граф equity curve в BacktestView
- [x] Monte Carlo симулятор

### Сессия 2026-05-25 — пройдено по роадмапу (без AI задач)
- [x] **VwapStrategy** — `Services/VwapStrategy.cs` + интеграция в `AIBotViewModel` (новая опция "VWAP" в `AvailableStrategies`, поле `VwapBandPct`); UI: новая строка `Row 2f-vwap` в MainWindow.axaml после MACD-параметров.
- [x] **Email SMTP уведомления** — обнаружено что уже реализовано в предыдущей сессии (`EmailNotificationService`, `PriceAlert.SendEmail`, AlertService.cs:201/207/211, UI Settings панель в MainWindow.axaml:9741 + чекбокс Email в Add Alert на 10210).
- [x] **Backtest CSV экспорт** — `BacktestViewModel.ExportCsvCommand` пишет 4 файла (`*_trades.csv`, `*_equity.csv`, `*_comparison.csv`, `*_summary.csv`) в `Documents/CryptoAITerminal/Backtests/`. Сравнение стратегий уже было готово (`RunComparisons`/`ComparisonRows`). UI: кнопка `Export CSV` после Monte Carlo панели.
- [x] **SniperRiskPolicyService тесты** — `CryptoAITerminal.Core.Tests/SniperRiskPolicyServiceTests.cs`, 18 тестов: BuildSnapshot (5), EvaluateEntry gating (11), IsEmergencyStopActive (2).
- [x] **Bybit Gateway завершён** — добавлен `GetCandlesAsync` в `BybitGateway` (Spot Category) и `BybitFuturesGateway` (Linear Category) + `BybitTimeframeMap`. Остальное было уже готово (PlaceOrder/Cancel/Balance/OrderBook/OpenOrders/Positions/WebSocket ticker+orderbook depth=1).
- [x] **OKX Gateway завершён** — добавлен `GetCandlesAsync` в `OKXGateway` и `OKXFuturesGateway` + `OKXTimeframeMap`. Остальное (PlaceOrder/Cancel/Balance/OrderBook/Positions/SetLeverage/SetMarginMode/WebSocket ticker) было реализовано.
- [x] **REST API WebApi** — новый проект `CryptoAITerminal.WebApi` (ASP.NET Core Minimal API, Kestrel на :5180). Эндпоинты: GET /api/health, /api/positions, /api/sniper/candidates, /api/pnl, /api/snapshot; POST /api/orders/market (постановка в очередь). Контракт через JSON-снимок `%APPDATA%/CryptoAITerminal/webapi/snapshot.json`, который пишет `WebApiSnapshotWriter` в TerminalUI каждые 5 секунд. Ордеры из WebApi складываются в `webapi/queue/<id>.json` (TerminalUI пока не обрабатывает их — нужна отдельная интеграция).

### Когда время позволит
- [x] Discord webhook уведомления — `DiscordWebhookNotificationService` + `SendDiscord` флаг в `PriceAlert`.
- [x] Monte Carlo в бэктесте (выполнено выше)
- [x] Граф equity curve в BacktestView (выполнено выше)
- [x] REST API (`CryptoAITerminal.WebApi`) для мобильного мониторинга + **WebApi queue watcher** в TerminalUI (`WebApiQueueProcessor` обрабатывает `webapi/queue/*.json` и маршрутизирует в гейтвеи, результат в `webapi/processed/*.json`).
- [x] Оптимизация localization scan — таймер теперь останавливается после 3 стабильных тиков подряд без новых регистраций, перезапускается на `LanguageChanged` и при смене `SelectedShellSection`. Интервал увеличен до 2 секунд. CPU-нагрузка на стабильном дереве ≈0.

### Сессия 2026-05-26 — дополнительные доработки (продолжение по spirit'у роадмапа)
- [x] **Тесты VwapStrategy** — `CryptoAITerminal.Core.Tests/VwapStrategyTests.cs`, 10 тестов. По ходу выявлен культурный баг в `VwapStrategy.Name` (запятая вместо точки на ru-RU) — исправлен через `string.Create(CultureInfo.InvariantCulture, …)`.
- [x] **WebApi bearer-token auth** — middleware в `Program.cs`. Активируется env var `CRYPTOAI_WEBAPI_TOKEN`. `/api/health` остаётся открытым. Без переменной — режим локального мониторинга без авторизации.
- [x] **WebApi cancel endpoint** — POST `/api/orders/cancel { exchange, market, orderId }`. Кладёт запись в queue с `action: "cancel"`; `WebApiQueueProcessor` различает place/cancel и вызывает соответствующий метод гейтвея.
- [x] **WebApi balances endpoint** — GET `/api/balances`. Данные пополняются отдельным `BalanceRefresher` (60-сек таймер, опрос ключевых активов USDT/USDC/BTC/ETH/BNB/SOL по 6 парам Binance/Bybit/OKX × Spot/Futures, потокобезопасный кэш). Snapshot читает кэш мгновенно.
- [x] **DCA Bot multi-exchange** — `DcaBotViewModel` теперь принимает 3 spot-гейтвея (Binance/Bybit/OKX) через новый конструктор. `AvailableExchanges` + `SelectedExchange` (ComboBox в UI, disabled при IsRunning). Fallback на Binance если выбранный недоступен. Старый конструктор `(BinanceGateway)` оставлен для совместимости.
- [x] **Тесты остальных стратегий** — `StrategyTests.cs`: SimpleMaStrategy (6), BollingerBandsStrategy (6), BreakoutStrategy (6), MacdStrategy (6). Итого +24 теста. Все IStrategy реализации теперь покрыты unit-тестами (SimpleMa/RSI/BB/Breakout/MACD/VWAP).

- [x] **KuCoin gateway (3.2)** — `CryptoAITerminal.Gateway.KuCoin/` (новый проект, Kucoin.Net 5.18.0). Реализованы `KucoinGateway` (Spot) и `KucoinFuturesGateway` (Perpetual) с интерфейсом IExchangeGateway. WebSocket для тиков не используется (поле модели не документированы); вместо этого REST polling каждые 3 сек на `/market/ticker`. PlaceOrder/Cancel/GetBalance/OrderBook/Candles/Positions — все есть. Символы: `BTCUSDT ↔ BTC-USDT` (spot), `BTCUSDT ↔ XBTUSDTM` (futures perpetual, KuCoin использует XBT и суффикс M). 19 тестов `KucoinSymbolHelperTests`. Подключено к TerminalUI: AllPositionsVM, BalanceRefresher, WebApiQueueProcessor (теперь 8 пар exchange/market), DCA Bot (4-я опция). Credentials расширены полями KucoinKey/Secret/Passphrase.

- [ ] Полный переход локализации на `x:Uid`+ResourceDictionary — пользователь отказался (риск регрессий по 10546 строк MainWindow.axaml превышает выгоду от оптимизации таймера).
- [ ] AI-related пункты роадмапа (2.2/2.6/2.7 + новости с Claude-суммаризацией) — намеренно вне scope по решению пользователя
- [ ] KuCoin — добавить UI-блок KuCoin credentials в Settings (сейчас читаются только из credentials JSON-файла или env); KuCoin не интегрирован в AIBotViewModel конструктор (требует изменения сигнатуры и большого числа call sites).

---

## 6. Зависимости для новых фич

| Фича | NuGet пакет | Кому добавить |
|---|---|---|
| Claude API | `Anthropic.SDK` | `AIEngine` |
| ONNX ML модели | `Microsoft.ML.OnnxRuntime` | `AIEngine` |
| SQLite для истории | `Microsoft.EntityFrameworkCore.Sqlite` | `Core` или `TerminalUI` |
| gRPC для WebApi | `Grpc.AspNetCore` | новый `WebApi` проект |
| DPAPI шифрование | встроен в .NET | `TerminalUI` |

---

## 7. Контекст для следующей сессии

Когда начинаешь новую работу — сначала прочитай этот файл, затем конкретный файл который будешь менять. Не нужно читать весь проект заново.

**Ключевые файлы:**
- `SniperViewModel.cs` — ядро снайпера (4920 строк)
- `AIBotViewModel.cs` — конфигурация и запуск торгового бота
- `TradingBot.cs` — логика исполнения
- `SimpleMaStrategy.cs` — единственная живая стратегия сейчас
- `SniperRiskPolicyService.cs` — логика риск-блокировок
- `SolanaTradeGateway.cs` — пример правильно реализованного DEX-шлюза
- `MainWindowViewModel.cs` — точка входа, создаёт все VM

**Последнее известное состояние сборки:** Проект собирается, есть Release-билд в `bin/Release/net8.0/win-x64/`.
