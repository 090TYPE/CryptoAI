# Crypto AI Terminal — License Bot

Telegram bot that **sells licenses and issues activation keys** for the terminal.
Customers pay in **Telegram Stars** (built-in, no external payment provider), and the
bot signs a license key with your private key and sends it back instantly. Customer
and order data is stored in a local SQLite database.

Keys are signed with the **same RSA format the terminal validates** — a cross-project
test (`LicenseBotCompatibilityTests`) guarantees bot-issued keys activate in the app.

---

## Setup

1. **Create a bot** with [@BotFather](https://t.me/BotFather) → get the bot token.
   - Stars payments work out of the box (no provider token needed).
2. **Private key** — the bot signs licenses with `.license-signing/private.pem`
   (the same key documented in `КАК-ВЫПУСКАТЬ-ЛИЦЕНЗИИ.md`). Never commit it.
3. **Configure** via env vars (recommended) or `appsettings.json`
   (copy `appsettings.example.json`):

| Env var | Meaning |
|---------|---------|
| `BOT_TOKEN` | BotFather token (required) |
| `LICENSE_PRIVATE_KEY_PATH` | path to `private.pem` (required) — or `LICENSE_PRIVATE_KEY` with the PEM inline |
| `BOT_ADMIN_IDS` | comma-separated Telegram user IDs with admin rights |
| `BOT_DB_PATH` | SQLite file path (default `licensebot.db`) |
| `BOT_CURRENCY` | `XTR` (Telegram Stars, default) |
| `BOT_PROVIDER_TOKEN` | empty for Stars; set only for a fiat provider |
| `CRYPTOPAY_TOKEN` | Crypto Pay app token (enables crypto payment) — from [@CryptoBot](https://t.me/CryptoBot) → Crypto Pay → Create App |
| `CRYPTOPAY_ASSETS` | accepted crypto assets, e.g. `USDT,TON` |
| `CRYPTOPAY_API_BASE` | `https://pay.crypt.bot/api/` (mainnet) or `https://testnet-pay.crypt.bot/api/` |
| `CRYPTO_FIAT` | fiat the price is quoted in (default `RUB`) |

4. **Run**:

```bash
BOT_TOKEN=123:abc \
LICENSE_PRIVATE_KEY_PATH=../.license-signing/private.pem \
BOT_ADMIN_IDS=11111111 \
dotnet run --project CryptoAITerminal.LicenseBot
```

---

## Hardware binding

Keys are **bound to the customer's machine**. The terminal's *Activate* dialog shows a
16-character *Machine ID*; the customer sends it to the bot (or the bot asks for it before
the first purchase). The issued key carries that machine and the terminal refuses to
activate it on any other PC.

- The bot prompts for the Machine ID automatically when you tap a plan and none is on file.
- `/bind <MachineID>` sets/updates it; `/mymachine` shows the current one.
- Note: the Machine ID derives from machine name + user + OS, so it changes on a new PC /
  fresh Windows account — reissue with `/issue` if a customer migrates.

## Customer commands

- `/start`, `/help` — intro
- `/buy` — show plans, pay with Stars or crypto, receive the key
- `/mykeys` — re-show purchased keys
- `/mymachine` — show your bound Machine ID
- `/bind <MachineID>` — set/update your Machine ID

## Admin commands (only `BOT_ADMIN_IDS`)

- `/stats` — customers, paid orders, Stars collected
- `/recent` — last orders
- `/issue <edition> <days|0> <machineId|none> <customer name>` — mint a key manually
  (no payment), e.g. `/issue Pro 365 none Acme Trading` or `/issue Pro 0 A1B2C3D4E5F6A7B8 Bob`

---

## Plans & pricing

Defined in `BotConfig.DefaultPlans` (code · title · Stars price · **RUB price** · days · edition).
Edit that list to change tiers. `Days = 0` means a perpetual license. Default Pro·month
is **2000 ₽** (paid in crypto equivalent) or 600 ⭐.

## Two payment methods

Every plan shows two buttons:
- **Stars** — Telegram's built-in Stars (instant, in-app).
- **Crypto** — a Crypto Pay invoice priced in `CRYPTO_FIAT` (RUB); the customer pays the
  crypto equivalent (USDT/TON/…). The bot polls the invoice and, once confirmed, signs and
  sends the key automatically. Crypto buttons appear only when `CRYPTOPAY_TOKEN` is set.

---

## Data

SQLite (`licensebot.db`):
- **customers** — telegram id, username, full name, first-seen
- **orders** — plan, edition, Stars paid, Telegram charge id, the issued license key,
  expiry, machine binding, status

The DB file and any real `appsettings.json` are git-ignored.

---

## How payment → key works

1. `/buy` → inline plan buttons.
2. Tap a plan → `SendInvoice` (currency `XTR`).
3. Telegram collects Stars → `PreCheckoutQuery` → bot approves.
4. `SuccessfulPayment` → bot signs `LicenseInfo{name, edition, expires}` with the
   private key, stores the order, and sends the key. Customer pastes it into the
   terminal under **Activate**.
