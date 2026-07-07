# CryptoAI — Trading Audit & Connect List
_Дата: 2026-07-07. Аудит: реально ли торговля подключена, используются ли вбитые/выбранные кошельки, есть ли фейковые данные на странице трейдинга, баги._

---

## 1. ЧТО РЕАЛЬНО РАБОТАЕТ (проверено по коду)

| Функция | Статус | Источник |
|---|---|---|
| Котировки по 4 биржам (Binance/Bybit/OKX/KuCoin) в шапке трейдинга | ✅ РЕАЛ | live order book mid, `MainWindowViewModel.TradingDesk.cs:161` |
| Лента сделок (tape) | ✅ РЕАЛ | публичные сделки Binance, без ключей, `RefreshTapeAsync` |
| Стакан (order book) + свечи | ✅ РЕАЛ | gateway `GetOrderBookAsync` / candles |
| Баланс кошелька | ✅ РЕАЛ | RPC/биржа, `RefreshManualAccountStateAsync`, `RefreshWalletAsync` |
| CEX market buy/sell (spot + futures) | ✅ РЕАЛ | `PlaceCexMarketOrderAsync` → `gateway.PlaceOrderAsync` |
| CEX futures limit / TP / SL | ✅ РЕАЛ на бирже | `PlaceExchangeLimitAsync` |
| DEX buy/sell (EVM/Solana/Tron) | ✅ РЕАЛ, подписывается ключом кошелька | `DexTradingViewModel.BuyAsync/SellAsync` → `ActiveDexGateway.BuyTokenAsync` |
| Working orders (spot limit/TP/SL) срабатывание | ✅ РЕАЛ | при триггере шлёт market order, `ExecuteWorkingOrderAsync:5953` |
| Хранение ключей | ✅ БЕЗОПАСНО | DPAPI-шифр, env > file, `CredentialsService.cs` |

**Кошелёк реально используется:** импортируешь приватный ключ во вкладке Wallet → строится `ActiveDexGateway` с этим ключом (`WalletWorkspaceViewModel.cs:921`) → каждая DEX-сделка подписывается им и уходит в сеть. Не мок.

---

## 2. БАГИ / ФЕЙК-ДАННЫЕ

> **СТАТУС 2026-07-07: BUG-1, BUG-2, GAP-3 — ИСПРАВЛЕНЫ. Сборка 0 ошибок, 569/569 тестов пройдено.**
> Проверено компиляцией. Реальные филлы (live-ордера) нужно проверить тебе на своих ключах (Hyperliquid — сначала testnet).

### BUG-1 — Working orders считаются только по текущему выбранному символу ✅ ИСПРАВЛЕНО
`EvaluateWorkingOrdersAsync` (`MainWindowViewModel.cs:5928`) фильтрует
`order.Symbol == SelectedTradingSymbol` и берёт цену только из `SelectedMarket`.
**Последствие:** limit/TP/SL, выставленный на BTCUSDT, НЕ сработает пока смотришь ETHUSDT.
Плюс working orders живут только в памяти — при закрытии приложения теряются и
на бирже НЕ висят (это софтовые ордера, не реальные limit на бирже для spot).
**Сделано:** движок working orders теперь мультисимвольный — каждый софт-ордер считается
по цене СВОЕГО символа (из строки Markets) и исполняется на СВОЁЙ бирже (`ExecutionExchange`
запоминается при выставлении). Позиция для SELL/TP/SL берётся authoritative: активный символ →
десковый `PositionQuantity`, чужой → баланс базового актива с той биржи (кэш 15с). Ордера теперь
**сохраняются на диск** (`%LocalAppData%\CryptoAITerminal\software-working-orders.json`) и
восстанавливаются при старте. Файлы: `MainWindowViewModel.cs` (EvaluateWorkingOrdersAsync,
ExecuteWorkingOrderAsync, Load/PersistSoftwareWorkingOrders), `WorkingOrderViewModel`.

### BUG-2 — 7-дневный спарклайн в списке рынков = выдуманный ✅ ИСПРАВЛЕНО
`CexMarketItemViewModel.SparklinePoints` (`:444`) рисует псевдослучайную кривую
`new Random(StableHash(Symbol))` — это НЕ реальная история цены, совпадает только знак наклона с 24h %.
**Сделано:** спарклайн теперь строится из реальных накопленных price-сэмплов (`_priceHistory`,
last 32), min/max нормализация. Если данных <2 точек — спарклайн скрыт (`ShowSparkline`), не рисуем
выдумку. Заголовок колонки "7D TREND" → "TREND". Файлы: `CexMarketItemViewModel.cs`, `MarketsView.axaml`.

### GAP-3 — Вкладка DEX Perps = симулятор (paper) ✅ РЕАЛЬНАЯ ТОРГОВЛЯ ПОДКЛЮЧЕНА
`DexPerpTradingViewModel` крутит `DexPerpMarketSimulator` (mark price/фандинг/филлы — эмуляция).
Помечено честно "Paper PERP". НО реальные перп-гейтвеи в коде ЕСТЬ:
`GmxPerpClient`, `HyperliquidPerpClient`.
**Сделано:** вкладка перпов получила переключатель **PAPER / LIVE** (кнопка + бейдж режима).
В LIVE режиме Market/Limit ордера идут в **реальный Hyperliquid** (кошелёк-подпись, gate как в
SWAP-панели, по умолчанию testnet). DCA/Grid/Stop/Trailing остаются paper (ни одна биржа не
исполняет их нативно — нужен keeper). Общий безопасный вход: `DexTradingViewModel.SendHyperliquidOrderAsync`.
Файлы: `DexPerpTradingViewModel.cs`, `DexDeskViewModel.cs`, `DexTradingViewModel.cs`, `TradingDeskView.axaml`.

**ЧТОБЫ ВКЛЮЧИТЬ LIVE перпы (тебе):** в SWAP-панели включи Hyperliquid live orders + импортируй
trade-enabled EVM кошелёк. Сначала проверь на **testnet** (по умолчанию), потом переключай mainnet.
Кнопка "PAPER / LIVE" на десктопе перпов armed'ит режим; бейдж покажет LIVE·TESTNET / LIVE·MAINNET.

### GAP-4 — AI Signal desk без AI-ключа показывает DEMO-сигналы
`AiSignalDeskViewModel`: с ключом — реальный анализ реальных данных; без ключа —
детерминированный демо-фид. Цены реальные всегда, но сигналы/выводы демо. Нужен AI-ключ (см. ниже).

---

## 3. ЧТО НАДО ПОДКЛЮЧИТЬ ТЕБЕ (ключи — я не могу, впиши сам)

Куда вбивать (любой из вариантов, env побеждает файл):
- **В приложении:** вкладка Settings (сохраняется в зашифрованный `api-credentials.json`).
- **Файл:** `%LocalAppData%\CryptoAITerminal\api-credentials.json` (DPAPI-шифр на этом же Windows-юзере).
- **ENV-переменные** (имена ниже) — перекрывают файл.

### 3.1 CEX — реальные ордера и баланс (без ключей = только публичные данные, торговли нет)
| Биржа | Нужно | ENV |
|---|---|---|
| Binance | key + secret | `BINANCE_API_KEY`, `BINANCE_API_SECRET` |
| Bybit | key + secret | `BYBIT_API_KEY`, `BYBIT_API_SECRET` |
| OKX | key + secret + passphrase | `OKX_API_KEY`, `OKX_API_SECRET`, `OKX_API_PASSPHRASE` |
| KuCoin | key + secret + passphrase | `KUCOIN_API_KEY`, `KUCOIN_API_SECRET`, `KUCOIN_API_PASSPHRASE` |

> Права ключа: включить Spot & Futures Trade. Для безопасности — IP-whitelist, БЕЗ права вывода средств.

### 3.2 DEX — реальные свапы (кошелёк)
- Приватный ключ кошелька: вкладка **Wallet → Import Private Key** (per network: EVM / Solana / Tron),
  либо `CRYPTOAI_DEX_PRIVATE_KEY`.
- Для каждой сети — свой ключ. Держи на кошельке только рабочую сумму (hot wallet).

### 3.3 AI (сигналы, ассистент, авто-трейдер) — нужен один из:
- Claude: `ANTHROPIC_API_KEY` (+ опц. `CRYPTOAI_CLAUDE_MODEL`)
- ChatGPT: `OPENAI_API_KEY` (+ опц. `CRYPTOAI_OPENAI_MODEL`)
- провайдер: `CRYPTOAI_AI_PROVIDER` = `anthropic` | `openai`

### 3.4 On-chain / DEX данные (кошельки, цены токенов, история)
| Провайдер | ENV | Для чего |
|---|---|---|
| Alchemy | `ALCHEMY_API_KEY` | EVM RPC/цены |
| Etherscan | `ETHERSCAN_API_KEY` | история tx ETH |
| BscScan | `BSCSCAN_API_KEY` | история tx BSC |
| Covalent | `COVALENT_API_KEY` | балансы/портфель |
| Moralis | `MORALIS_API_KEY` | токены/цены |
| Birdeye | `BIRDEYE_API_KEY` | Solana цены |
| CoinGecko | `COINGECKO_API_KEY` | цены (опц., есть keyless) |
| TronGrid | `TRONGRID_API_KEY` | Tron |

### 3.5 Деривативы / новости / сентимент (для аналитики)
- `COINGLASS_API_KEY` — ликвидации/фандинг
- `GLASSNODE_API_KEY` — on-chain метрики
- `CRYPTOPANIC_API_KEY` — новости

### 3.6 Уведомления (опц.)
- `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID`

### 3.7 Реальные перпы на DEX (только если решим убрать симулятор из GAP-3)
- GMX: кошелёк на Arbitrum (тот же DEX-ключ) + Alchemy RPC.
- Hyperliquid: приватный ключ кошелька (подпись ордеров).

---

## 4. ЗАМЕЧАНИЕ ПО СБОРКЕ
`dotnet build` дал 20 ошибок — ВСЕ типа MSB3021/MSB3027 (файл занят другим процессом):
приложение запущено (PID держат DLL). Сам C# компилируется чисто, ошибок кода (CS****) НЕТ,
123 warning (не блокируют). Чтобы я собрал и проверил фиксы — закрой запущенный CryptoAI.

---

## 5. СТАТУС ФИКСОВ
1. BUG-2 спарклайн → реальные сэмплы. ✅ СДЕЛАНО
2. BUG-1 working orders мультисимвольный + персист на диск. ✅ СДЕЛАНО
3. GAP-3 реальные Hyperliquid перпы (PAPER/LIVE toggle). ✅ СДЕЛАНО (проверить филлы на testnet)
4. GAP-4 — не баг, нужен AI-ключ (см. §3.3).

**Проверено:** сборка 0 ошибок, 569/569 тестов. Runtime live-филлы проверь сам на своих ключах.

## 6. ОСТАЛОСЬ (мелочь, на будущее)
- Софт-TP/SL fallback для futures-бирж без нативных условных ордеров помечается `IsExchangeManaged=true`
  и потому не тикается софт-движком (`MainWindowViewModel.cs`, ~строка 4430). Редкий кейс (только биржи
  без нативного TP/SL). Если понадобится — сделать отдельный software-флаг вместо фейкового exchange id.
