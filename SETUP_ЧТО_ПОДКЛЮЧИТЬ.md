# CryptoAI — Что подключить (чеклист для тебя)

Отмечай `[x]` по мере готовности. Все ключи вбиваются одним из 3 способов:
- **В приложении:** вкладка Settings (сохраняется зашифровано, DPAPI).
- **Файл:** `%LocalAppData%\CryptoAITerminal\api-credentials.json`.
- **ENV-переменные** (имена в скобках) — перекрывают файл.

---

## 1. ОБЯЗАТЕЛЬНО для реальной торговли

### [ ] CEX биржи (реальные ордера + баланс)
Без ключей — только публичные данные (котировки/стакан работают, торговли нет).

- [ ] **Binance** — key + secret (`BINANCE_API_KEY`, `BINANCE_API_SECRET`)
- [ ] **Bybit** — key + secret (`BYBIT_API_KEY`, `BYBIT_API_SECRET`)
- [ ] **OKX** — key + secret + passphrase (`OKX_API_KEY`, `OKX_API_SECRET`, `OKX_API_PASSPHRASE`)
- [ ] **KuCoin** — key + secret + passphrase (`KUCOIN_API_KEY`, `KUCOIN_API_SECRET`, `KUCOIN_API_PASSPHRASE`)

> **Права ключа:** включить Spot + Futures Trade. IP-whitelist ДА. Право на вывод средств — НЕТ.

### [ ] DEX кошелёк (реальные свапы)
- [ ] Импортировать приватный ключ: вкладка **Wallet → Import Private Key** (для каждой сети своя: EVM / Solana / Tron), либо `CRYPTOAI_DEX_PRIVATE_KEY`.
- [ ] Держать на hot-кошельке только рабочую сумму.

---

## 2. AI-функции (нужен один провайдер)

- [ ] **Claude:** `ANTHROPIC_API_KEY` (+ опц. `CRYPTOAI_CLAUDE_MODEL`)
- [ ] **или ChatGPT:** `OPENAI_API_KEY` (+ опц. `CRYPTOAI_OPENAI_MODEL`)
- [ ] Провайдер: `CRYPTOAI_AI_PROVIDER` = `anthropic` | `openai`

> Без AI-ключа страница **AI Signals** показывает DEMO-сигналы (цены реальные, выводы демо). Ассистент отвечает заглушкой.

---

## 3. LIVE перпы (Hyperliquid) — если нужна реальная перп-торговля на десктопе перпов

- [ ] В SWAP-панели: включить **Hyperliquid live orders**.
- [ ] Импортировать trade-enabled EVM кошелёк (не watch-режим).
- [ ] **Сначала протестировать на TESTNET** (по умолчанию включён).
- [ ] На десктопе перпов нажать **PAPER / LIVE** — бейдж покажет `LIVE · TESTNET`.
- [ ] Только после успешного теста — переключить на mainnet (реальные деньги).

---

## 4. On-chain / DEX данные (кошельки, цены токенов, история сделок)

- [ ] **Alchemy** (`ALCHEMY_API_KEY`) — EVM RPC/цены
- [ ] **Etherscan** (`ETHERSCAN_API_KEY`) — история tx ETH
- [ ] **BscScan** (`BSCSCAN_API_KEY`) — история tx BSC
- [ ] **Covalent** (`COVALENT_API_KEY`) — балансы/портфель
- [ ] **Moralis** (`MORALIS_API_KEY`) — токены/цены
- [ ] **Birdeye** (`BIRDEYE_API_KEY`) — Solana цены
- [ ] **CoinGecko** (`COINGECKO_API_KEY`) — цены (опц., есть keyless)
- [ ] **TronGrid** (`TRONGRID_API_KEY`) — Tron

---

## 5. Аналитика / новости / сентимент (опционально)

- [ ] **Coinglass** (`COINGLASS_API_KEY`) — ликвидации/фандинг
- [ ] **Glassnode** (`GLASSNODE_API_KEY`) — on-chain метрики
- [ ] **CryptoPanic** (`CRYPTOPANIC_API_KEY`) — новости

---

## 6. Уведомления (опционально)

- [ ] **Telegram** — `TELEGRAM_BOT_TOKEN` + `TELEGRAM_CHAT_ID`

---

## Приоритет (с чего начать)

1. **AI-ключ** (§2) — сразу оживит AI Signals + ассистента + новые AI-функции ниже.
2. **1 CEX биржа** (§1) — самый быстрый путь к реальной торговле.
3. **Alchemy + Moralis** (§4) — оживят DEX/кошельки.
4. Остальное по мере надобности.

---
_Детальный аудит торговли и что уже работает — в `TRADING_AUDIT.md`._
