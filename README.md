# Crypto AI Terminal

<p align="center">
  <img src="design/logo-assets/logos/cryptoaiterminal-logo-stacked-variant-a.png" width="220" alt="Crypto AI Terminal Logo"/>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/Avalonia-12.0-883EFF" alt="Avalonia"/>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0078D6?logo=windows" alt="Platform"/>
  <img src="https://img.shields.io/badge/Version-v1.0-21E6C1" alt="v1.0"/>
</p>

> **Профессиональный десктоп-терминал для торговли криптовалютами.** CEX + DEX в одном окне: Binance, Bybit, OKX, KuCoin, Uniswap, Jupiter, SunSwap. Алгоритмические боты, снайпер новых листингов, on-chain аналитика, AI-стратегии.

---

## Содержание

- [Скриншот](#скриншот)
- [Что умеет терминал](#что-умеет-терминал)
- [Архитектура проекта](#архитектура-проекта)
- [Биржи и сети](#биржи-и-сети)
- [Боты и автоматизация](#боты-и-автоматизация)
- [DEX-трейдинг и снайпер](#dex-трейдинг-и-снайпер)
- [Аналитика и сигналы](#аналитика-и-сигналы)
- [Управление рисками](#управление-рисками)
- [Уведомления](#уведомления)
- [Быстрый старт](#быстрый-старт)
- [API-ключи](#api-ключи)
- [Сборка релиза](#сборка-релиза)
- [Безопасность](#безопасность)
- [Стек технологий](#стек-технологий)

---

## Скриншот

> Терминал запускается в полноэкранном режиме без системной рамки. Переключение разделов — боковая навигационная панель.

---

## Что умеет терминал

### Торговля (CEX)

- **Spot и USDT-M Futures** на Binance, Bybit, OKX, KuCoin одновременно
- **Ордера**: Market, Limit, Stop-Limit, OCO; IOC / FOK / GTC
- **Leverage 1–100×**, Cross / Isolated margin, Long-only / Short-only / Long&Short bias
- **9 шаблонов ордеров** с горячими клавишами `Shift+1`…`Shift+9` — одним нажатием выставляется преднастроенный ордер
- **Advanced Trailing Stop** — серверный трейлинг, partial TP (закрытие доли на первом уровне), multi-step exit с настройкой TP2
- **TWAP и Iceberg** — разбивка крупного ордера во времени или скрытые объёмы для снижения market impact
- **Условные ордера** (Conditional Orders) — триггерные цепочки «если цена выше X → купить Y»
- **Best Execution Router** — сравнивает котировки на всех подключённых биржах и маршрутизирует ордер в наиболее выгодную

### DEX-трейдинг

- **EVM-сети**: Ethereum, BSC, Base, Polygon, Arbitrum — через Uniswap V2/V3, Aerodrome, PancakeSwap, SushiSwap, QuickSwap, Camelot
- **Uniswap V3** — автоматический выбор лучшего fee-tier (0.01% / 0.05% / 0.3% / 1%) через QuoterV2
- **Solana** — Jupiter (все DEX: Raydium, Orca, PumpSwap), с поддержкой Jito bundle для MEV-защиты
- **Tron** — SunSwap V2, TRC-20 токены
- **MEV-защита**: Flashbots Protect для ETH/Base, BloxRoute для BSC, Jito для Solana
- **CEX-подобный интерфейс**: 3-колонный макет с live-графиком, таймфреймами, блоттером сделок
- **История свечей** из 4 источников: GeckoTerminal → disk cache → on-chain Swap events → синтетика

### Отображение данных

- **Свечной график** с инструментами рисования: тренд-линии, горизонтальные уровни, прямоугольники, каналы
- **Orderbook** — стакан с глубиной 8 уровней, best bid/ask, спред
- **Real-time котировки** через WebSocket — нет задержки биржевого API

---

## Архитектура проекта

```
CryptoAI/
├── CryptoAITerminal.Core             ← модели, интерфейсы (IExchangeGateway, IStrategy)
├── CryptoAITerminal.Core.Tests       ← 59+ unit-тестов (RiskManager, Strategies, BacktestEngine)
├── CryptoAITerminal.Gateway.Base     ← общий код шлюзов
├── CryptoAITerminal.Gateway.Binance  ← Binance Spot + USDT-M Futures (Binance.Net)
├── CryptoAITerminal.Gateway.Bybit    ← Bybit v5 Spot + Linear Futures (Bybit.Net)
├── CryptoAITerminal.Gateway.OKX      ← OKX Unified API Spot + Swap (OKX.Net)
├── CryptoAITerminal.Gateway.KuCoin   ← KuCoin Spot + Futures (Kucoin.Net)
├── CryptoAITerminal.Gateway.DEX      ← Web3 шлюзы (Nethereum, Jupiter, SunSwap)
├── CryptoAITerminal.OrderRouter      ← Best Execution Router
├── CryptoAITerminal.RiskManager      ← Pre-trade risk checks, дневные лимиты
├── CryptoAITerminal.WhaleTracker     ← On-chain whale alerts
├── CryptoAITerminal.AIEngine         ← Торговые стратегии + Claude API
├── CryptoAITerminal.TerminalUI       ← Avalonia UI (главный проект)
└── CryptoAITerminal.WebApi           ← REST API для мобильного мониторинга
```

Все биржевые шлюзы реализуют единый интерфейс `IExchangeGateway` — стратегия, написанная один раз, работает на любой бирже без изменений.

---

## Биржи и сети

### CEX

| Биржа | Spot | Futures | WebSocket | Статус |
|-------|------|---------|-----------|--------|
| **Binance** | ✅ | ✅ USDT-M | ✅ | Полностью |
| **Bybit** | ✅ | ✅ Linear | ✅ | Полностью |
| **OKX** | ✅ | ✅ Swap | ✅ | Полностью |
| **KuCoin** | ✅ | ✅ Perp | REST poll | Полностью |

### DEX / Блокчейны

| Сеть | DEX | Снайпер | MEV-защита |
|------|-----|---------|-----------|
| **Ethereum** | Uniswap V2/V3, SushiSwap | Factory monitor | Flashbots Protect |
| **BSC** | PancakeSwap, SushiSwap | Factory monitor | BloxRoute |
| **Base** | Aerodrome, Uniswap V2/V3 | Factory monitor | Flashbots Protect |
| **Polygon** | QuickSwap, SushiSwap, Uniswap V3 | — | — |
| **Arbitrum** | SushiSwap, Camelot, Uniswap V3 | — | — |
| **Solana** | Jupiter (все DEX) | Program monitor | Jito bundles |
| **Tron** | SunSwap V2 | — | — |

---

## Боты и автоматизация

### Rule Bot (AI Bot)

Стратегии, выбираемые из выпадающего списка:

| Стратегия | Параметры | Описание |
|-----------|-----------|----------|
| **MA Cross** | Fast / Slow period | Пересечение скользящих средних |
| **RSI** | Period, Overbought, Oversold | Wilder's RSI (правильная формула, совпадает с TradingView) |
| **Bollinger Bands** | Period, Deviation | Торговля от границ полос |
| **Breakout** | Period | Пробой максимума/минимума за N свечей |
| **MACD** | Fast / Slow / Signal | Classic MACD-crossover |
| **VWAP** | Band % | Торговля от VWAP ±band |
| **AI (Claude)** | API key, model, poll interval | Сигнал генерирует Claude API на основе последних свечей |

Для каждой стратегии настраиваются: TP, SL, Trailing Stop, Partial TP (частичное закрытие), Leverage, Margin mode.

### Grid Bot

- Автоматическая расстановка лимитных ордеров между `LowerPrice` и `UpperPrice` на N уровней
- Spot и Futures (с леверджем)
- Комиссии учтены в P&L (0.1% за сторону)
- Pause / Resume без потери позиций

### DCA Bot

- Периодические докупки по расписанию
- Поддержка Binance, Bybit, OKX, KuCoin
- Лестница TP-уровней для частичных продаж

### DEX Sniper

Снайпер для новых листингов. Принцип работы:

1. **Обнаружение** — 8 параллельных источников сигналов:
   - Factory события (PairCreated / PoolCreated) на BSC, ETH, Base
   - Solana Program Activity (Raydium, Pump.fun, Orca)
   - **EVM Mempool** — pending `addLiquidity` транзакции (за 1-3 блока до Factory)
   - **Pump.fun Graduation** — переезд токена с pump.fun на Raydium
   - DexScreener trending API
2. **Фильтрация** — security scan (GoPlus + Honeypot.is + RugCheck), **Deployer Wallet Analysis** (история деплоев, % рагпуллов), liquidity / volume / age gates
3. **Ранжирование** — SniperRankingModel (0-100), band S/A/B/C/D
4. **Исполнение** — live buy через IDexTradeGateway, retry-логика, трейлинг-стоп
5. **Управление позицией** — SniperTrailingStopService: активация, trail distance, multi-level TP (50%/100%/200%/500%), hard stop

### Composite Rule Engine

Конструктор сложных автоматических правил без написания кода:

- **Условия**: RSI < X, цена > MA, цена выше/ниже уровня, cross above/below, volume spike, funding rate, P&L открытой позиции, 24h change
- **Логика**: AND / OR
- **Действия**: Market Buy/Sell, Limit Buy/Sell, запуск DCA, пауза Grid, уведомление, переход SL на breakeven
- **Cooldown**: от 30 сек до 4 часов, один раз или повторяться
- **Применение**: автоматическое управление портфелем без ручного вмешательства

### TradingView Webhook

```
POST /api/webhook/tradingview
{
  "action": "buy",          // buy | sell | close
  "symbol": "BTCUSDT",
  "qty": 0.01,
  "exchange": "Binance",    // Binance | Bybit | OKX | KuCoin
  "market": "Spot",         // Spot | Futures
  "secret": "your_secret"  // опционально, задаётся через CRYPTOAI_TV_SECRET
}
```

TradingView Alert шлёт этот JSON → терминал получает и исполняет ордер. Лог вебхуков: `/api/webhook/tradingview/log`.

---

## DEX-трейдинг и снайпер

### Интерфейс DEX Trading

Три колонки, идентичные CEX:

- **Левая** — тикет: выбор токена, quote asset (ETH/BNB/USDT/USDC), сумма, пресеты 25/50/75/MAX, кнопки BUY/SELL
- **Центральная** — график (GeckoTerminal OHLCV → disk cache → on-chain reconstruction → синтетика), таймфреймы 15M/1H/4H/1D/1W, блоттер сделок, вкладка Wallet, Chart Info
- **Правая** — браузер токенов: поиск, quick cards с ценой и ликвидностью, листинг

### История свечей (4-уровневый fallback)

1. **GeckoTerminal OHLCV** — официальный API, свечи от создания пула
2. **Disk cache** — локальное хранилище `%LocalAppData%/CryptoAITerminal/dex-price-cache/`, сохраняется между сессиями
3. **On-chain reconstruction** (`EvmPoolSwapHistoryScanner`) — сканирует Swap-события пула прямо из блокчейна, восстанавливает историю с момента создания пула
4. **Synthetic bootstrap** — экстраполяция из 5m/1h/24h изменений цены

### LP Calculator

Встроен в DEX Trading вкладку:
- **V2 Impermanent Loss** = 2√k/(1+k) - 1, где k = currentPrice/entryPrice
- **V3 IL** — с учётом tick range (concentrated liquidity)
- On-chain запрос резервов пула через `getReserves()`

---

## Аналитика и сигналы

### On-Chain Metrics
CoinMetrics Community API — MVRV Z-Score, NUPL, Exchange Net Flow для BTC/ETH.

### Whale Tracker
Алерты на крупные переводы (≥ $500K) через Etherscan/BSCScan/Solscan. Помеченные адреса (биржи, фонды, известные киты).

### Correlation Matrix
Матрица корреляций Пирсона между открытыми позициями на дневных log-returns. Предупреждение при |ρ| ≥ 0.7. Цветовая тепловая карта.

### Deribit Options
Данные опционов в реальном времени:
- **ATM IV** для BTC и ETH
- **Put/Call ratio** по открытому интересу
- **25-delta skew** — разница IV пут/колл; положительный skew = страх рынка
- **Sentiment**: Extreme Fear / Fear / Neutral / Greed / Extreme Greed

### Market Scanner
Поиск аномалий: пробои, сжатие волатильности, volume spike, RSI-дивергенции.

### Liquidation Heatmap
Карта концентрации ликвидаций по уровням цены. Алерты на proximity к кластеру.

### Gas Monitor
Текущая стоимость газа в Gwei (Ethereum), BNB (BSC), лампортах (Solana). Алерт при просадке ниже порога.

### News Feed
RSS от CoinTelegraph, CoinDesk, Decrypt, The Block, Bitcoin Magazine. Авто-классификация: bullish / bearish / neutral. Фильтр по монете и ключевым словам.

### Funding Rate
Текущие ставки фандинга Binance, Bybit, OKX с агрегированным view. **Funding Rate Arbitrage** — автоматический delta-neutral для сбора положительного фандинга.

---

## Управление рисками

### Risk Manager
- Максимальный убыток за торговую сессию (USD)
- Максимальный размер позиции (% от баланса)
- Thread-safe накопление дневных убытков

### Sniper Risk Policy
- Лимит одновременных позиций
- Максимальное количество покупок за сессию
- Дневной cap убытков
- Последовательные losing-стопы (emergency stop)
- Cooldown между входами

### Portfolio Correlation
Предупреждение когда открытые позиции высококоррелированы (диверсификация иллюзорна).

### Tax Report
Кнопка «Export Tax Report» в Trade Journal → FIFO P&L расчёт → 2 CSV файла:
- `{year}_trades.csv` — каждая сделка с cost basis, proceeds, holding period
- `{year}_summary.csv` — итог по активу (gross profit, gross loss, win rate)

Совместимо с Koinly, CoinTracker, ручной подачей в ФНС.

### P&L Dashboard
- Equity curve
- Метрики: trades, win rate, avg win/loss, max drawdown, **аннуализированный Sharpe ratio** (×√252)
- Группировка по периоду / бирже / стратегии
- Экспорт в CSV

### Backtest

- MA Cross, RSI, Bollinger Bands, Breakout — на исторических свечах
- Метрики: winrate, net return, max drawdown, Sharpe
- **Walk-Forward Optimization** с визуализацией: синие полосы = in-sample, зелёные = out-of-sample
- **Monte Carlo** — N симуляций на случайных подмножествах данных, доверительный интервал
- Экспорт в 4 CSV: trades, equity, comparison, summary

---

## Уведомления

| Канал | Настройка | Когда срабатывает |
|-------|-----------|-------------------|
| **System Tray (Windows)** | Встроено | Закрытие сделки, ошибка бота, важный алерт |
| **Telegram** | TELEGRAM_BOT_TOKEN + TELEGRAM_CHAT_ID | Любой алерт с флагом «Send Telegram» |
| **Discord** | Discord Webhook URL | Любой алерт с флагом «Send Discord» |
| **ntfy.sh** | ntfy.sh topic | Push на телефон, без приложения |
| **Email (SMTP)** | SMTP-сервер + логин | Критические алерты |

### Volume Spike Alert
Реальный мониторинг объёма: `FeedVolume24h()` сравнивает текущий объём с 7-дневным rolling average. Срабатывает при превышении порога (настраивается, по умолчанию ×2).

---

## Быстрый старт

### Требования

- **Windows 10 / 11 x64**
- **.NET 8.0 SDK** — [скачать](https://dotnet.microsoft.com/download/dotnet/8.0)
- API-ключи бирж (опционально для просмотра данных, обязательно для торговли)

### Запуск из исходников

```powershell
git clone https://github.com/090TYPE/CryptoAI.git
cd CryptoAI

dotnet build CryptoAITerminal.TerminalUI
dotnet run --project CryptoAITerminal.TerminalUI
```

### Запуск готовой сборки

```powershell
# Создать релизный пакет
.\build-release.ps1

# Распаковать архив из ./release/ и запустить
CryptoAITerminal.TerminalUI.exe
```

Установка не требуется. Self-contained build включает .NET runtime.

---

## API-ключи

### Хранение

```
%LOCALAPPDATA%\CryptoAITerminal\api-credentials.json
```

Файл создаётся автоматически. Можно редактировать вручную или через UI **Settings → API Keys**. Переменные окружения имеют приоритет над файлом.

### Биржевые ключи (личные, для торговли)

| Переменная | Биржа |
|-----------|-------|
| `BINANCE_API_KEY` / `BINANCE_API_SECRET` | Binance |
| `BYBIT_API_KEY` / `BYBIT_API_SECRET` | Bybit |
| `OKX_API_KEY` / `OKX_API_SECRET` / `OKX_API_PASSPHRASE` | OKX |
| `KUCOIN_API_KEY` / `KUCOIN_API_SECRET` / `KUCOIN_API_PASSPHRASE` | KuCoin |
| `CRYPTOAI_DEX_PRIVATE_KEY` | Приватный ключ EVM-кошелька для DEX |

### Системные ключи (опциональны — без них терминал работает)

| Ключ | Провайдер | Что улучшает |
|------|-----------|-------------|
| `ETHERSCAN_API_KEY` | etherscan.io | Whale Tracker на Ethereum |
| `BSCSCAN_API_KEY` | bscscan.com | Whale Tracker на BSC |
| `ALCHEMY_API_KEY` | alchemy.com | Обогащение цен токенов |
| `MORALIS_API_KEY` | moralis.io | DEX OHLCV-свечи |
| `BIRDEYE_API_KEY` | birdeye.so | Solana DEX аналитика |
| `COVALENT_API_KEY` | covalenthq.com | Балансы кошельков |
| `COINGECKO_API_KEY` | coingecko.com | Pro-лимиты CoinGecko |
| `CRYPTOPANIC_API_KEY` | cryptopanic.com | Дополнительный новостной поток |
| `COINGLASS_API_KEY` | coinglass.com | Точная liquidation heatmap |

### Уведомления

| Ключ | Назначение |
|------|-----------|
| `TELEGRAM_BOT_TOKEN` | Telegram bot |
| `TELEGRAM_CHAT_ID` | ID чата для уведомлений |
| `CRYPTOAI_TV_SECRET` | Секрет для проверки TradingView webhook |
| `CRYPTOAI_WEBAPI_TOKEN` | Bearer token для REST API (если выставить в публичный интернет) |

---

## REST API (WebApi)

Запустите отдельно как локальный сервер для мониторинга с телефона:

```bash
cd CryptoAITerminal.WebApi
dotnet run
# Слушает на :5180
```

| Endpoint | Метод | Описание |
|----------|-------|----------|
| `/api/health` | GET | Статус и время последнего обновления снимка |
| `/api/positions` | GET | Открытые позиции по всем биржам |
| `/api/sniper/candidates` | GET | Кандидаты снайпера с RankScore |
| `/api/pnl` | GET | Сводка P&L |
| `/api/balances` | GET | Балансы по биржам |
| `/api/snapshot` | GET | Полный снимок состояния |
| `/api/orders/market` | POST | Отправить маркет-ордер |
| `/api/orders/cancel` | POST | Отменить ордер |
| `/api/webhook/tradingview` | POST | TradingView Alert webhook |
| `/api/webhook/tradingview/log` | GET | Последние 50 вебхуков (диагностика) |

Снимок состояния обновляется каждые 5 секунд (записывает TerminalUI в `%AppData%\CryptoAITerminal\webapi\snapshot.json`).

---

## Конфигурационные профили

Сохраните текущие настройки как именованный профиль:

**Settings → Profiles → введите имя → Save Profile**

Профиль содержит все настройки ботов, DEX, снайпера, Grid, DCA — **без API-ключей** (они хранятся отдельно в DPAPI).

Для переключения между "скальпинг" и "свинг" конфигурациями достаточно одного клика.

---

## Сборка релиза

```powershell
.\build-release.ps1
```

Скрипт создаёт self-contained publish для Windows x64 и упаковывает в `./release/CryptoAITerminal-<дата>.zip`. Архив можно передать заказчику — установка .NET не требуется.

---

## Безопасность

- **Локальное хранение** — ключи только в `%LOCALAPPDATA%`, никаких облаков
- **DPAPI шифрование** — файл credentials зашифрован Windows Data Protection API
- **Минимальные права API** — рекомендуется создавать ключи с правом только на торговлю, без вывода, с IP-привязкой
- **Отдельный DEX-кошелёк** — используйте hot wallet с минимальным балансом, никогда не вставляйте seed основного кошелька
- **Webhook secret** — защита TradingView endpoint от подделки запросов через `CRYPTOAI_TV_SECRET`
- **WebApi token** — опциональный bearer token для REST API при публичном доступе

---

## Стек технологий

| Компонент | Технология |
|-----------|-----------|
| Runtime | .NET 8.0 Windows |
| UI Framework | Avalonia 12, ReactiveUI |
| Async | TPL, System.Reactive (Rx.NET) |
| Binance | Binance.Net (CryptoExchange.Net) |
| Bybit | Bybit.Net |
| OKX | OKX.Net |
| KuCoin | Kucoin.Net |
| Web3 EVM | Nethereum |
| Solana | Solana.Unity.SDK, Jupiter API |
| Charts | Custom Avalonia DrawingContext (StreamGeometry) |
| JSON | System.Text.Json |
| Tests | xUnit, 59+ unit tests |
| Notifications | WinForms NotifyIcon, Telegram Bot API, Discord Webhook |

---

<p align="center">
  <img src="design/logo-assets/icons/cryptoaiterminal-icon-variant-a-transparent.png" width="80" alt="icon"/>
  <br/>
  <sub>Crypto AI Terminal · v1.0 · 2026</sub>
</p>
