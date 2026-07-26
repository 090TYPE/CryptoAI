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

> **Тестовая среда.** В **Settings → Exchange keys** у каждой биржи есть тумблер `TESTNET`:
> Binance Testnet, Bybit Testnet, OKX Demo trading. Ключи там свои — мейннет-ключ тестовая
> среда отвергает, поэтому после переключения вставь ключ, выданный тестовой средой, и
> перезапусти терминал (окружение гейтвея фиксируется при старте). У KuCoin песочницы нет:
> биржа её закрыла, в клиентской библиотеке тестового окружения не осталось.

### [ ] DEX кошелёк (реальные свапы)
- [ ] Импортировать приватный ключ: вкладка **Wallet → Import Private Key** (для каждой сети своя: EVM / Solana / Tron), либо `CRYPTOAI_DEX_PRIVATE_KEY`.
- [ ] Держать на hot-кошельке только рабочую сумму.
- [ ] **Settings → DEX & networks**: свой RPC на сеть (пусто = встроенная публичная нода) и
      MEV-защита (Flashbots Protect для ETH/Base, Jito-бандлы для Solana). Читается в момент
      импорта кошелька — после изменения переимпортировать сессию.

---

## 2. AI-функции (нужен один провайдер)

- [ ] **Claude:** `ANTHROPIC_API_KEY` (+ опц. `CRYPTOAI_CLAUDE_MODEL`)
- [ ] **или ChatGPT:** `OPENAI_API_KEY` (+ опц. `CRYPTOAI_OPENAI_MODEL`)
- [ ] Провайдер: `CRYPTOAI_AI_PROVIDER` = `anthropic` | `openai`

> Без AI-ключа страница **AI Signals** показывает DEMO-сигналы (цены реальные, выводы демо). Ассистент отвечает заглушкой.

### [ ] AI-агент (Copilot умеет ДЕЙСТВОВАТЬ)
С AI-ключом (§выше) глобальный Copilot из read-only становится **агентом**: по твоему запросу
навигирует по страницам, читает баланс/позиции/рынок, заполняет тикет, армит/выставляет ордера,
ставит алерты, применяет сигналы, конфигит ботов.
- **По умолчанию CONFIRM:** любое действие с деньгами показывает карточку в Action Tray → ты жмёшь ✓/✕.
- **AUTO (тумблер, ВЫКЛ по умолчанию):** агент действует сам. Даже в AUTO риск/кошелёк/testnet-гейты
  остаются — агент их не обходит. Включай осознанно.
- Где: панель AI Copilot (вкладка Bots). Полный аудит действий: `%LocalAppData%\CryptoAITerminal\agent-actions.json`.

### [ ] Автономный агент (Copilot торгует сам)
Карточка **AUTONOMOUS AGENT** (вкладка Bots, под Copilot): задаёшь цель, интервал, PAPER/LIVE,
бюджет сессии ($), макс. сделок, allowlist символов → **ARM**. Агент сам крутит цикл и торгует.
- **PAPER по умолчанию** — ничего реального не размещает (симуляция).
- **LIVE** требует явного consent (красная плашка) + тумблер LIVE. Гейты риск/кошелёк/testnet остаются.
- **Guardrails:** превышение бюджета/лимита сделок → сессия сама останавливается. Символы вне allowlist отклоняются.
- **STOP (kill-switch)** — мгновенно останавливает цикл.
- Нужен AI-ключ (§2). Для LIVE CEX — ключи бирж (§1); для LIVE Hyperliquid — §3.

---

## 3. LIVE перпы (Hyperliquid) — если нужна реальная перп-торговля на десктопе перпов

- [ ] Включить **Hyperliquid live orders** — в SWAP-панели либо в **Settings → DEX & networks**.
- [ ] Импортировать trade-enabled EVM кошелёк (не watch-режим).
- [ ] **Сначала протестировать на TESTNET** (по умолчанию включён).
- [ ] На десктопе перпов нажать **PAPER / LIVE** — бейдж покажет `LIVE · TESTNET`.
- [ ] Только после успешного теста — переключить на mainnet (реальные деньги).

---

## 4. On-chain / DEX данные (кошельки, цены токенов, история сделок)

> Ключи из §4 и §5 больше не требуют переменных окружения: их можно вписать прямо в
> **Settings → Data providers**. Значения шифруются DPAPI в `api-credentials.json`.
> Если переменная окружения всё же выставлена — она перекрывает сохранённое значение,
> и карточка ключа помечается бейджем `ENV VAR`.

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

Заполняются в **Settings → Notifications** и сохраняются на диск (DPAPI) — переживают перезапуск.

- [ ] **Telegram** — `TELEGRAM_BOT_TOKEN` + `TELEGRAM_CHAT_ID`
- [ ] **Discord** — `DISCORD_WEBHOOK_URL`
- [ ] **ntfy.sh** — `NTFY_TOPIC`
- [ ] **Email (SMTP)** — `EMAIL_SMTP_HOST` / `EMAIL_SMTP_PORT` / `EMAIL_SMTP_SSL` / `EMAIL_SMTP_USER` / `EMAIL_SMTP_PASS` / `EMAIL_FROM` / `EMAIL_TO`

---

## Приоритет (с чего начать)

1. **AI-ключ** (§2) — сразу оживит AI Signals + ассистента + новые AI-функции ниже.
2. **1 CEX биржа** (§1) — самый быстрый путь к реальной торговле.
3. **Alchemy + Moralis** (§4) — оживят DEX/кошельки.
4. Остальное по мере надобности.

---
_Детальный аудит торговли и что уже работает — в `TRADING_AUDIT.md`._
