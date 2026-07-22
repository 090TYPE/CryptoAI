# Трек 4 — Настоящее 24/7-исполнение серверных ботов

> Цель: чтобы стратегии реально работали **с выключенным ПК юзера**. Сейчас серверный
> исполнитель ботов — заглушка (`StubBotOrderExecutor`, только paper). Надо заменить его
> реальным исполнением на биржах, с теми же риск-контролями, что в десктопе.
>
> Принцип: **деньги двигаются только на изолированной executor-ноде**, opt-in live,
> paper по умолчанию, каждый ордер проходит риск-гейт и пишется в аудит.
>
> Связанные документы: `docs/SERVER_ROADMAP.md` (общий), `DEVELOPMENT_ROADMAP.md` (десктоп).

---

## 1. Что уже есть (факт по коду)

| Компонент | Файл | Состояние |
|---|---|---|
| Цикл исполнения ботов | `CryptoAITerminal.Executor/BotExecutorService.cs` | ✅ тикает 15с, грузит `enabled` bot_configs, **DCA + Grid + Trailing** |
| Интерфейс исполнителя | `CryptoAITerminal.Executor/IBotOrderExecutor.cs` | ✅ `PlaceAsync(userId, side, asset, amount)` |
| Реальный исполнитель | — | 🔴 только `StubBotOrderExecutor` → возвращает `("paper", ref)` |
| AI pre-trade review | `IPreTradeReviewer.cs` / `AiPreTradeReviewer` | ✅ гейт перед сделкой (fail-open, если модель недоступна) |
| Хранение ключей юзера | `secrets` (envelope-encrypted), `SecretsRepository` | ✅ есть; расшифровка `IEnvelopeCipher.DecryptAsync` (Local AES или Vault) |
| Конфиг ботов | `bot_configs` (strategy, params_json, enabled, last_run_utc) | ✅ есть; **нет полей exchange/market/mode** |
| Ордера ботов | `bot_orders` (side/asset/amount/price/status/ext_ref) | ✅ есть; `status` = paper/placed/failed/blocked |
| Биржевые гейтвеи | `Gateway.Binance/Bybit/OKX/KuCoin` → `IExchangeGateway` (в Core) | ✅ чистый net8.0, **переиспользуемы на сервере** (без Avalonia) |
| Риск-менеджер | `CryptoAITerminal.RiskManager` | ✅ переиспользуем |

**Вывод:** все кирпичи есть. Не хватает одного класса-исполнителя + связки «секрет → гейтвей →
риск → ордер → реконсиляция», плюс расширения за пределы DCA.

---

## 2. Целевая архитектура

```
BotExecutorService (tick)
   └─ для каждого enabled bot_config, который "созрел":
        1. StrategyEvaluator.Evaluate(bot) → намерение (side, asset, qty/notional)   [DCA|Grid|Trailing]
        2. RiskGate.Check(userId, intent)  → allow/deny (caps, kill-switch, daily loss)
        3. AiPreTradeReviewer.Review(...)  → allow/deny (уже есть)
        4. IBotOrderExecutor.PlaceAsync(...)
              └─ ExchangeBotOrderExecutor (НОВЫЙ):
                   a. SecretsRepository.GetForExchange(userId, exchange) → decrypt (IEnvelopeCipher)
                   b. GatewayFactory.Create(exchange, market, creds) → IExchangeGateway
                   c. gateway.PlaceOrderAsync(...)  (live)  ИЛИ  PaperFill (paper)
                   d. вернуть (status, extRef)
        5. Reconciler: опросить fill (price/status) → обновить bot_orders
        6. AuditRepository.Write + InboxRepository.Notify
```

Executor-нода — **единственная**, у кого есть KEK и право расшифровывать секреты
(двухнодовая топология из SERVER_ROADMAP). API-нода секреты не расшифровывает.

---

## 2a. Ключевые интерфейсы (скелеты)

Расширяем `IBotOrderExecutor` до типизированного намерения — тогда одна точка исполнения
обслуживает DCA/Grid/Trailing без раздувания сигнатуры.

```csharp
// Намерение сделки — что стратегия хочет исполнить.
public readonly record struct BotIntent(
    Guid   UserId,
    Guid   BotId,
    string Exchange,   // binance|bybit|okx|kucoin
    string Market,     // spot|futures
    string Mode,       // paper|live
    string Side,       // buy|sell
    string Asset,      // BTC
    string Quote,      // USDT
    decimal? NotionalUsd,   // для DCA (сумма в $)
    decimal? Quantity,      // для grid/trailing (кол-во базовой)
    string?  ClientOrderId); // идемпотентность (см. §4.5)

public interface IBotOrderExecutor
{
    Task<BotFill> PlaceAsync(BotIntent intent, CancellationToken ct);
}

public readonly record struct BotFill(
    string Status,   // paper|placed|failed|blocked
    string? ExtRef,  // id ордера биржи
    decimal? AvgPrice,
    decimal? FilledQty,
    string?  Error);
```

```csharp
// Реальный исполнитель — заменяет StubBotOrderExecutor.
public sealed class ExchangeBotOrderExecutor : IBotOrderExecutor
{
    // deps: SecretsRepository, IEnvelopeCipher, GatewayFactory, IPriceSource, ILogger
    public async Task<BotFill> PlaceAsync(BotIntent i, CancellationToken ct)
    {
        // 1. paper — симуляция по текущей цене, приватные эндпоинты не трогаем
        if (i.Mode != "live")
        {
            var px = await _price.GetAsync(i.Exchange, $"{i.Asset}{i.Quote}", ct);
            var qty = i.Quantity ?? (i.NotionalUsd!.Value / px);
            return new("paper", "paper-" + Guid.NewGuid().ToString("N")[..12], px, qty, null);
        }

        // 2. live — достать ключ юзера, расшифровать (только на этой ноде)
        var sec = await _secrets.GetForExchangeAsync(i.UserId, i.Exchange, "cex_api", ct);
        if (sec is null) return new("failed", null, null, null, "no key for " + i.Exchange);
        if (!sec.Permissions.Contains("trade")) return new("failed", null, null, null, "key lacks trade");

        var creds = await _cipher.DecryptAsync(sec.Ciphertext, sec.WrappedDek, ct); // never logged
        var gw = _factory.Create(i.Exchange, i.Market, creds);

        // 3. поставить ордер (сигнатура — см. IExchangeGateway.PlaceOrderAsync)
        try
        {
            var order = await gw.PlaceOrderAsync(/* symbol, side, qty/notional, ClientOrderId */);
            return new("placed", order.Id, order.AvgPrice, order.FilledQty, null);
        }
        catch (Exception ex) { return new("failed", null, null, null, ex.Message); }
        finally { creds = null; } // затираем секрет из памяти
    }
}
```

```csharp
// Фабрика гейтвеев — единственное место, знающее про конкретные Gateway.* классы.
public sealed class GatewayFactory
{
    public IExchangeGateway Create(string exchange, string market, string creds) => (exchange, market) switch
    {
        ("binance", "spot")    => new BinanceGateway(Parse(creds)),
        ("binance", "futures") => new BinanceFuturesGateway(Parse(creds)),
        ("bybit",   _)         => market == "futures" ? new BybitFuturesGateway(Parse(creds)) : new BybitGateway(Parse(creds)),
        ("okx",     _)         => market == "futures" ? new OKXFuturesGateway(Parse(creds))   : new OKXGateway(Parse(creds)),
        ("kucoin",  _)         => market == "futures" ? new KucoinFuturesGateway(Parse(creds)): new KucoinGateway(Parse(creds)),
        _ => throw new NotSupportedException($"{exchange}/{market}")
    };
}
```

```csharp
// Риск-гейт перед live-ордером (переиспользует CryptoAITerminal.RiskManager).
public interface IRiskGate
{
    // false + reason => сделку не ставим, пишем 'blocked' и уведомляем юзера
    Task<(bool Ok, string? Reason)> CheckAsync(BotIntent intent, CancellationToken ct);
}
```

---

## 3. Пошаговый план

### Фаза 4.1 — Реальный CEX-исполнитель для DCA (MVP «работает с выключенным ПК»)

> **Статус: 🟡 ядро реализовано по TDD (15 тестов зелёные).** Готово: `ExchangeBotOrderExecutor`
> (paper/live, trade-only гейт), `PerOrderCapRiskGate`, `GatewayFactory` (**Binance/Bybit/OKX/KuCoin
> spot**, все с per-user ключами), `SecretsCexKeyProvider`, `HttpPriceSource`,
> `SecretsRepository.FindCexKeyAsync`, DI-переключатель `BOT_LIVE_ENABLED`, риск-гейт встроен в
> `BotExecutorService`. `BinanceGateway` получил keyed-конструктор (польза и десктопу). Осталось:
> живой testnet-прогон; поля exchange/market/mode пока в `params_json` (миграция в колонки — позже).

Минимальный кусок, дающий видимый результат: DCA реально покупает на бирже.

**Схема (новая миграция `db/013_bot_execution.sql`):**
```sql
ALTER TABLE bot_configs ADD COLUMN IF NOT EXISTS exchange TEXT;          -- binance|bybit|okx|kucoin
ALTER TABLE bot_configs ADD COLUMN IF NOT EXISTS market   TEXT DEFAULT 'spot'; -- spot|futures
ALTER TABLE bot_configs ADD COLUMN IF NOT EXISTS mode     TEXT DEFAULT 'paper'; -- paper|live
ALTER TABLE bot_orders  ADD COLUMN IF NOT EXISTS filled_qty   NUMERIC;
ALTER TABLE bot_orders  ADD COLUMN IF NOT EXISTS avg_price     NUMERIC;
ALTER TABLE bot_orders  ADD COLUMN IF NOT EXISTS reconciled_utc TIMESTAMPTZ;
```

**Новые файлы:**
- `CryptoAITerminal.Executor/GatewayFactory.cs` — по (exchange, market, creds) возвращает `IExchangeGateway`. Референсы на `Gateway.*` добавить в `Executor.csproj`.
- `CryptoAITerminal.Executor/ExchangeBotOrderExecutor.cs` — реализация `IBotOrderExecutor`:
  1. `SecretsRepository` → найти `kind='cex_api'`, `exchange_or_chain=bot.exchange`; `IEnvelopeCipher.DecryptAsync`.
  2. `GatewayFactory.Create(...)`.
  3. если `bot.mode=='live'` → `gateway.PlaceOrderAsync`; иначе paper-fill по текущей цене.
  4. вернуть `(status, extRef)`; ошибки → `("failed", null)` + лог.
- `CryptoAITerminal.Server.Data/SecretsRepository.cs` — добавить `GetForExchangeAsync(userId, exchange, ct)` (если ещё нет метода выборки одного секрета).

**Правки:**
- `Program.cs` (Executor) — DI: выбирать `ExchangeBotOrderExecutor`, если задан `BOT_LIVE_ENABLED=true` (иначе Stub). Так live включается осознанно на уровне ноды.
- `BotExecutorService.cs` — прокидывать `bot.mode`/`bot.exchange`/`bot.market` в `PlaceAsync` (расширить сигнатуру интерфейса до `PlaceAsync(BotIntent intent, ...)`).

**Риск-контроль (обязательно до live):** см. §4. Для 4.1 минимум — `max notional per order`, `paper по умолчанию`, глобальный `BOT_LIVE_ENABLED` рубильник.

**Definition of done:** на testnet/малой сумме DCA-бот с `mode=live` реально ставит ордер;
`bot_orders.status='placed'`, `ext_ref` = id ордера биржи; в `mode=paper` поведение как сейчас.

---

### Фаза 4.2 — Серверный Grid-бот

> **Статус: ✅ реализовано по TDD (grid-логика + stateful-обвязка + диспетчеризация в тик-цикле).**
> `ServerGridStrategy` (чистые функции): `GenerateLevels` (spacing + N+1 уровней),
> `CycleProfit` (комиссия с двух сторон, BUG-10), `InitialOrders` (spot — buy-below,
> futures + sell-above), `OnFill` (buy@i→sell@i+1; sell@i→buy@i-1 + закрытие цикла).
> `GridBotRunner` (stateful): `StartAsync` ставит сетку, `PollAsync` ловит филлы через
> `GetOpenOrdersAsync` и переставляет противоположный ордер. Активные ордера — в таблице
> `grid_orders` (уникальный индекс на открытые `(bot,level,side)`, restart-safe).
> `BotExecutorService` диспетчеризует `strategy=grid`: первый прогон → `StartAsync`, дальше
> → `PollAsync`. `PaperExchangeGateway` симулирует филлы по пересечению цены (grid гоняется
> end-to-end без биржи); `GridGatewayProvider` выбирает paper или live trade-only ключ юзера.
> На нотионал каждой ячейки — тот же per-order риск-кап, что у DCA. Осталось: прогон на testnet
> с живыми ключами + реконсилятор (4.5).

Grid — stateful: держит набор лимиток между `lower`/`upper`, ловит филлы, переставляет.

- `CryptoAITerminal.Executor/Strategies/ServerGridStrategy.cs` — портировать логику десктопного `GridBot.cs` (уровни, комиссия 0.1%/сторона учтена — БАГ-10 уже исправлен в десктопе, переиспользовать формулу).
- Таблица `bot_grid_orders` (или расширить `bot_orders` полем `level`) для отслеживания активных уровней.
- Реконсиляция филлов через `gateway.GetOpenOrdersAsync` / `GetRecentTradesAsync`.
- Идемпотентность при рестарте ноды: восстановление активных уровней из БД, не дублировать (учесть урок БАГ-12: сначала отмена, потом очистка).

**DoD:** grid-бот на testnet расставляет сетку, при филле переставляет противоположный ордер, P&L с учётом комиссий совпадает с десктопом.

---

### Фаза 4.3 — Серверный Trailing / TP-SL

> **Статус: ✅ реализовано по TDD (логика + runner + персистентность + диспетчеризация).**
> `ServerTrailingStop` (чистые функции): один тик цены → одно действие (TP / SL / partial TP /
> сдвиг трейл-SL) + следующее состояние, порядок TP→SL→trail как в десктопе. Сохранены анти-спам
> гард трейлинга (>0.1%) и БАГ-07 (partial TP на споте: TP1 закрывает долю и перевзводит цель на TP2,
> TP2 закрывает остаток — без бесконечного деления пополам). `TrailingBotRunner` привязывает решение
> к gateway + `ITslPositionStore`: грузит позицию, оценивает цену, шлёт рыночное закрытие (полное/
> частичное) или просто сохраняет подтянутый стоп; состояние пишется каждый тик (restart-safe).
> Трейлинг софт-симулируется (закрытие по пересечению стопа — без churn нативных SL-ордеров, поэтому
> БАГ-08 с гонкой обновления SL здесь неактуален). Таблица `tsl_positions`
> (одна управляемая позиция на бота, restart-safe) + `TslPositionsRepository` + `SqlTslPositionStore`;
> `BotExecutorService` диспетчеризует `strategy=trailing`: первый прогон «прикрепляет» позицию из
> params (юзер уже держит её — бот ведёт только выход), дальше тики опрашивают цену и `TrailingBotRunner`
> закрывает (полностью/частично) или подтягивает стоп. Gateway paper/live резолвится как у grid, стейт
> пишется каждый тик. Для futures есть **нативный биржевой SL** (`nativeSl:true`): один reduce-only стоп
> ставится на attach, cancel+replace при подтягивании, resize на partial TP, снимается при TP-закрытии;
> пересечение SL отдаётся бирже (без двойного закрытия), `sl_order_id`/`native_sl` в `tsl_positions`.
> **Осталось:** апстрим-поток, который сам открывает позицию (сейчас юзер задаёт entry/qty вручную).

- `CryptoAITerminal.Executor/ServerTrailingStop.cs` + `TrailingBotRunner.cs` — ✅ портировано из `TpSlManager.cs`.
- Для futures — серверные reduce-only SL/TP через `PlaceStopLossOrderAsync` / `PlaceTakeProfitOrderAsync` (уже в `IExchangeGateway`).
- Partial TP (учесть БАГ-07 — на спот тоже работает частичное закрытие).

**DoD:** trailing двигает SL вверх за ценой, на futures reduce-only, один активный SL на позицию.

---

### Фаза 4.4 — Мульти-биржевая маршрутизация (best price)

> **Статус: ✅ реализовано (single-venue best price) по TDD (5 тестов).** `ServerBestExecution`
> (чистое) выбирает биржу по эффективной (с учётом комиссии) цене: дешевле купить / дороже продать.
> `MultiExchangeQuoteSource` тянет публичные спотовые тикеры Binance/Bybit/OKX; биржа, что не ответила,
> пропускается. DCA-параметры `route:true` + `exchanges:[...]` сравнивают площадки каждый тик и шлют
> ордер на лучшую, выбор + экономия/ед. пишутся в `audit_log` (`bot_route`). **Осталось:** сплит
> крупного ордера по стаканам (десктопный `RouteSplit`) — на сервере пока весь ордер на одну площадку.

**DoD:** ✅ ордер уходит на биржу с лучшей эффективной ценой; выбор виден в аудите.

---

### Фаза 4.5 — Безопасность исполнения, реконсиляция, тесты

> **Статус: 🟢 kill-switch + идемпотентность готовы; реконсиляция покрыта grid-раннером.**

- **Kill-switch на юзера и глобальный** — ✅ таблица `user_flags` (per-user + строка all-zero UUID = глобальный стоп) + `UserFlagsRepository` + чистый `LiveGate` (3 теста). Executor проверяет перед каждым live-ордером DCA и постановкой grid; paper не гейтится; трейлинг-выходы разрешены при halt (закрытие снижает риск).
- **Идемпотентность** — ✅ `bot_orders.client_order_id` + уникальный индекс; тот же логический тик даёт тот же id, повторный place после рестарта пропускается (+ `ON CONFLICT DO NOTHING` как второй барьер). `MarkRunAsync` — после подтверждённого размещения.
- **Реконсилятор** — grid-раннер сам реконсилит лимитки (`GetOpenOrdersAsync` → fill → противоположный ордер); DCA — market-ордера (мгновенный fill). Отдельный `BackgroundService` для `avg_price`/`filled_qty` нужен только когда добавим частичные лимитки вне grid — **отложено**.
- **Тесты** — fake `IExchangeGateway` покрывает paper-fill, live-place, risk-deny, grid fill-reaction, trailing (soft + native SL), routing, kill-switch, idempotency. **696 зелёных** (>25 по Треку 4).
- **Осталось:** advisory-lock/`SKIP LOCKED` на `bot_id` для мульти-инстансного executor (сейчас один инстанс — не критично).

---

## 4. Риск-контроль и безопасность (зависит от Трека 3)

⚠️ **Live-исполнение не включать, пока не готово:**

1. ✅ **Per-order cap** — `PerOrderCapRiskGate` (`BOT_MAX_ORDER_USD`), проверяется перед каждым ордером.
2. 🟢 **Per-user daily cap** — `DailyCapGate` (`BOT_MAX_DAILY_USD`): потолок дневного live-notional на юзера (сумма из `bot_orders`). *Loss*-cap (по реализованному PnL) требует PnL-учёта — отложено.
3. ⬜ **Max open positions / max bots per user** — из тарифа (Трек 2, plan-limits). Не сделано.
4. ✅ **Kill-switch:** `user_flags(user_id, live_halted)` (+ all-zero UUID = глобальный) + env `BOT_LIVE_ENABLED`; `LiveGate` проверяется перед каждым live-ордером DCA/grid.
5. ✅ **Allowlist символов** — `SymbolAllowlist` (`BOT_SYMBOL_ALLOWLIST`); live DCA/grid только по разрешённым символам.
6. ✅ **Ключи только с правом trade, без withdraw** — `ExchangeBotOrderExecutor`/`GridGatewayProvider` валидируют `permissions` (refuse withdraw, require trade) перед live.
7. ✅ Всё пишется в `audit_log` (`bot_order`/`bot_route`/`kill_switch`/`symbol_blocked`/`daily_cap`/`bot_trade_blocked`).

Реальные деньги трогать только после Трека 3 (MPC/whitelist/2FA) — зафиксировать как gate.

---

## 5. Стратегия тестирования

1. **Paper-режим** — дефолт; полная симуляция по реальным ценам, приватные эндпоинты бирж не трогаются.
2. **Testnet** — Binance/Bybit/OKX testnet-ключи в `secrets`, `mode=live`, малые суммы.
3. **Unit-тесты** — fake gateway (см. 4.5).
4. **Прогон в текущем контуре** — `docker compose up`, включить бота через `AdminCli` (пункт 8 BOTS) или `/api/bots`, наблюдать `bot_orders` и логи executor.

### Testnet-runbook (когда есть testnet-ключи)

Live-путь готов; для реального прогона нужны **trade-only** testnet-ключи (без права withdraw).

1. **Сгенерировать testnet-ключи** на бирже (Binance/Bybit/OKX testnet), права: только spot trade.
2. **Залить ключ** (шифруется envelope-cipher'ом в `secrets`). `Secret` — JSON с ключами, `permissions` содержит `trade` и НЕ содержит `withdraw`:
   ```
   curl -X POST http://localhost:8080/api/secrets -H "X-License: <token>" -H "Content-Type: application/json" \
     -d '{"kind":"cex_api","exchangeOrChain":"binance","label":"testnet",
          "secret":"{\"key\":\"<API_KEY>\",\"secret\":\"<API_SECRET>\"}","permissions":"trade"}'
   ```
   (OKX/KuCoin — добавить `\"passphrase\":\"...\"` в `secret`.)
3. **Создать live-бота** (`mode:live`), малый размер, `BOT_MAX_ORDER_USD` держит потолок:
   ```
   curl -X POST http://localhost:8080/api/bots -H "X-License: <token>" -H "Content-Type: application/json" \
     -d '{"strategy":"dca","params":{"asset":"BTC","quote":"USDT","amountUsd":10,
          "intervalMinutes":1,"exchange":"binance","market":"spot","mode":"live"}}'
   ```
4. **Включить live на ноде:** в `.env` `BOT_LIVE_ENABLED=true` → `docker compose up -d --build executor`.
   (Executor подхватит `ExchangeBotOrderExecutor`; kill-switch `user_flags` — глобальный/на юзера стоп.)
5. **Наблюдать:** `docker compose logs -f executor`, таблица `bot_orders` (`status=placed`, `ext_ref`=id ордера на бирже), `audit_log`.
6. **Стоп:** `UPDATE user_flags SET live_halted=true WHERE user_id='00000000-0000-0000-0000-000000000000';` (глобальный kill-switch) или выключить бота.

⚠️ Реальные деньги (не testnet) — только после Трека 3 (MPC/whitelist/2FA).

---

## 5a. Режимы отказа (обязательно обработать)

| Ситуация | Поведение |
|---|---|
| Нет ключа юзера для биржи | `status=failed`, inbox-уведомление, бот не отключается (юзер добавит ключ) |
| Ключ без права `trade` (или withdraw-only) | `failed` до размещения, аудит `key_rejected` |
| Недостаточно баланса | `failed`, уведомление; повтор по расписанию, не в цикле |
| Символ не торгуется / делистинг | `failed`, авто-пауза бота + inbox |
| Rate limit / таймаут биржи | ретрай с backoff внутри тика; при исчерпании — `failed`, следующий тик |
| **Рестарт ноды между place и MarkRun** | идемпотентность через `ClientOrderId` (§4.5): повторный place с тем же id биржа отвергнет/вернёт тот же ордер |
| Частичный филл | реконсилятор добивает `filled_qty`/`avg_price`; grid ждёт остаток |
| Биржа лежит | тик пропускает юзера, не блокирует остальных; алерт в метрики |
| KEK/Vault недоступен | live-исполнение останавливается целиком (fail-safe), paper работает |

**Идемпотентность (ядро безопасности):** `ClientOrderId = hash(botId, tickBucket, side, asset)`.
Один и тот же логический тик → один и тот же id → биржа не создаст дубль при повторе.
`MarkRunAsync` — только после подтверждённого размещения.

---

## 5b. Наблюдаемость

- **Метрики** (в `collector_runs`-стиле или Prometheus, если поднимем в Треке 5): ордеров/мин,
  доля `failed`, задержка тика, число активных ботов, live vs paper.
- **Здоровье бота:** поле `bot_configs.last_error` + видно в `AdminCli` (пункт 8) и `/api/bots`.
- **Аудит:** каждый исход (`bot_order`/`bot_trade_blocked`/`key_rejected`) уже пишется в `audit_log`.
- **Алерты:** серия `failed` подряд по одному боту → авто-пауза + inbox (как emergency-stop снайпера).

---

## 6. Файлы, которых коснёмся (сводка)

```
db/013_bot_execution.sql                        ← новая миграция (+ монтировать в docker-compose.yml)
CryptoAITerminal.Executor/
  GatewayFactory.cs                             ← новый
  ExchangeBotOrderExecutor.cs                   ← новый (замена Stub)
  IBotOrderExecutor.cs                          ← расширить сигнатуру (BotIntent)
  BotExecutorService.cs                         ← прокинуть exchange/market/mode, вызвать стратегии
  Program.cs                                    ← DI live/paper по BOT_LIVE_ENABLED
  Strategies/ServerGridStrategy.cs              ← новый (4.2)
  Strategies/ServerTrailingStop.cs              ← новый (4.3)
  Reconciler.cs                                 ← новый (4.5)
  CryptoAITerminal.Executor.csproj              ← + ref на Gateway.*, RiskManager
CryptoAITerminal.Server.Data/SecretsRepository.cs, BotConfigRepository.cs  ← методы выборки
docker-compose.yml                              ← BOT_LIVE_ENABLED, новая миграция
```

---

## 7. Стратегические решения (с рекомендациями)

### 7.1 Модель хранения ключей — развилка всего трека

| Вариант | Что значит | Плюсы | Минусы | Оценка риска |
|---|---|---|---|---|
| **A. Полная кастодия** | сервер хранит ключи с любыми правами (вкл. withdraw) | максимум автоматизации | огромная юр./security-ответственность; нужен весь Трек 3 (MPC) | 🔴 высокий |
| **B. Кастодия trade-only** ✅ | сервер хранит **только trade-ключи без права вывода** | боты работают 24/7; вывод физически невозможен с этих ключей | нужен риск-гейт, но не MPC для вывода | 🟡 средний |
| **C. Non-custodial** | ключи у юзера, сервер только оркестрирует, исполняет клиент | нет хранения секретов | «работает с ПК off» невозможно — теряется вся суть Трека 4 | 🟢 низкий, но цель не достигается |

**Рекомендация — Вариант B (trade-only кастодия).** Схема `secrets.permissions` уже это
поддерживает: на этапе приёма ключа валидировать, что права = только `trade`, и **отклонять
ключи с `withdraw`**. Тогда 24/7-исполнение возможно, а катастрофический сценарий (увод средств
через скомпрометированный сервер) исключён на уровне прав API. MPC/whitelist (Трек 3) остаётся
нужен только для отдельного модуля вывода, но **не блокирует Трек 4**.

### 7.2 Live-гейт

**Рекомендация: да, привязать.** Глобальный `BOT_LIVE_ENABLED=false` по умолчанию; включать live
только когда готовы §4 п.1/4/6 (caps + kill-switch + валидация trade-only). MPC (Трек 3) — только
если выберут Вариант A.

### 7.3 Где хранить риск-состояние (дневной лосс, позиции)

**Рекомендация: в БД** (`bot_risk_state`) — переживает рестарт ноды, консистентно между репликами.
Кэш на ноде поверх для скорости.

### 7.4 NEST (Elasticsearch) в `Gateway.Binance`

Тянется транзитивно в executor-образ и раздувает его. **Действие:** проверить, реально ли он нужен
гейтвею; при ненужности — вынести/удалить зависимость (заодно польза десктопу). Не блокер, но в 4.1
проверить размер образа.

---

## 8. Рекомендуемый порядок и оценки

| Шаг | Фаза | Объём (оценка) | Что даёт |
|---|---|---|---|
| 1 | 4.1 DCA live + миграция 013 | ~1–2 дня | MVP «работает с ПК off» (paper + testnet) |
| 2 | 4.5-lite: риск-гейт + kill-switch + trade-only валидация | ~1 день | безопасно включать live |
| 3 | 4.2 Grid | ~2–3 дня | grid 24/7 (портирование `GridBot.cs`) |
| 4 | 4.3 Trailing/TP-SL | ~2 дня | серверный trailing (портирование `TpSlManager.cs`) |
| 5 | 4.4 Роутинг | ~1 день | best-price для мульти-биржевых юзеров |
| 6 | 4.5 полная: реконсилятор + идемпотентность + тесты | ~2 дня | надёжность, ≥25 тестов |

*Оценки — при переиспользовании десктопной логики; чистое написание с нуля больше.*

### Scope первого PR (Фаза 4.1)
- `db/013_bot_execution.sql` + монтирование в `docker-compose.yml`.
- `BotIntent`/`BotFill` + расширенный `IBotOrderExecutor` (§2a).
- `GatewayFactory` + `ExchangeBotOrderExecutor` (paper-путь полностью, live за `BOT_LIVE_ENABLED`).
- `SecretsRepository.GetForExchangeAsync` + валидация trade-only (§7.1 Вариант B).
- Минимальный `IRiskGate` (per-order cap) + DI-переключение Stub/Exchange в `Program.cs`.
- Тесты с fake-гейтвеем: paper-fill, no-key→failed, withdraw-key→rejected, cap→blocked.
- **DoD:** DCA-бот в paper даёт корректный fill; на testnet с `BOT_LIVE_ENABLED=true` реально ставит ордер.

**Старт — Фаза 4.1** (см. scope выше), после явного решения по §7.1 (рекомендация — Вариант B).
