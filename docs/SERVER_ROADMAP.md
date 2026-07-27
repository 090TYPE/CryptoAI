# CryptoAI Server — Feature Roadmap

Status: ✅ done · 🟡 partial · 🔴 todo. Server backend lives in `CryptoAITerminal.Server.*`,
`CryptoAITerminal.CandleWorker`, `CryptoAITerminal.Executor`, `CryptoAITerminal.AdminCli`.

## Already built (this backend)
- ✅ TimescaleDB schema (favorites-driven 24/7 candles, DEX data, custodial, audit) — `db/*.sql`
- ✅ 13 data collectors 24/7 (market, security, socials, holders, deployer, whales, onchain, gas, liquidations, news, sentiment + keyed birdeye/coingecko/cryptopanic)
- ✅ Editable provider keys (`ProviderKeyStore`) + admin console
- ✅ Server.Api: favorites sync, data reads, custodial endpoints — RSA license-signature auth
- ✅ Custodial: secrets (envelope-encrypted, Vault or local AES), withdrawals (delay+cancel+audit), bots config, audit log
- ✅ Withdrawal executor (delay → decrypt → **stub** signer)
- ✅ Server-side AI proxy (`/api/ai/message`, `/api/ai/openai`) + client + agent runners routed through it
- ✅ Rate limiting
- ✅ Menu-driven admin console
- ✅ Client favorites sync
- ✅ docker-compose whole contour

## 🔴 Critical for selling (do first)
- ✅ **Billing** — `CryptoAITerminal.LicenseBot`: Telegram Stars + Crypto Pay (USDT/TON), signs the
  RSA licence the terminal validates (cross-project test), hardware-bound, admin `/issue`, SQLite
  order history. Long-polling, so it needs no public endpoint, no webhook idempotency and no
  inbound firewall rule. **In the compose contour**: own network, no published port, signing key
  bind-mounted read-only from `.license-signing/`, order history on the `licensebot_data` volume
  and backed up alongside Postgres.
- 🔴 **Plan limits** (rate limit, #bots, feature gating) per tier — the licence token carries
  `Edition`, and **no server endpoint reads it**. Every paying tier gets the same thing.
- 🔴 **MPC / threshold signer** for withdrawals (replace StubWithdrawalSigner) — before real funds
- 🟡 **Deploy** — one VPS, not the old 2-node Amsterdam plan: trading and bots run on the customer's
  machine, so no node here ever holds `CRYPTOAI_KEK_B64` and there is nothing to isolate. Contour is
  ready (`docker compose up -d --build`); domain + Cloudflare + firewall are the remaining work.
  Runbook: `docs/DEPLOY.md`.
- 🟡 **2FA** (TOTP) — server done (Totp RFC6238 + /api/2fa/setup|enable|verify + withdrawal gated; secret envelope-encrypted); client UI + login gate TODO

## AI
- ✅ Real-time AI alerts (AiAnomalyCollector: SQL pre-filter → Claude verdict → push to everyone holding the token)
- ✅ Shared AI digests via `AiDigestJob` base (one call → all users, read at /api/digests):
  - ✅ `daily` — daily market briefing across everything tracked (24h)
  - ✅ `movers` — why the biggest 24h gainers/losers moved (6h)
  - ✅ `narratives` — clusters tracked tokens into narratives getting flow (12h)
  - ✅ `news_impact` — which headlines actually matter for tracked tokens (3h)
  - ✅ `weekly` — week in review (7d)
  - ✅ `new_listings` — fresh tokens: early opportunity vs trap, from security+holders (6h)
  - ✅ `whales` — what the big transfers likely mean (6h)
  - ✅ `gas` — transaction-timing advice from 24h gas stats per chain (6h)
  - ✅ `rug_postmortem` — what killed the collapsed tokens + the tell to spot next time (12h)
  - Risk lens: ✅ `liquidity_watch` `volume_anomaly` `holder_concentration` `security_roundup`
    `deployer_blacklist` `worst_scored` `top_scored` `dead_tokens` `risk_dashboard`
  - Market lens: ✅ `chain_rotation` `onchain_pulse` `sentiment_read` `liquidations`
    `stablecoin_flows` `volatility` `momentum` `reversals` `liquidity_leaders` `microcaps`
    `news_price_gap` `gas_vs_activity` `token_of_the_day`
  - **30 broadcast AI streams total.** New AI feature = one small subclass (prompt + facts).
    Each publishes nothing when it has no facts — no spend, no invented content.
- ✅ AI token scoring cached for everyone (AiScoreCollector + token_ai_score, surfaced in /api/dex/token; needs anthropic key)
- ✅ AI pre-trade review before autonomous trades (AiPreTradeReviewer gate in BotExecutorService; explicit reject blocks + inbox notice; fail-open unless AI_PRETRADE_REQUIRED=true)
- ✅ RAG Q&A — POST /api/ai/ask: answers grounded ONLY in our DB (user watchlist + digests + news)
- ✅ Personal portfolio review (PortfolioReviewJob → user inbox, weekly, cost-bounded)

## Data & server
- ✅ Server-side price alerts 24/7 (price_alerts + AlertCollector + /api/alerts; fires vs token_snapshot, audits 'alert_triggered')
- 🔴 WebSocket/SSE streaming (replace polling)
- 🔴 Public REST/webhook API for clients (TradingView etc.)
- 🔴 Long historical candles as a service

## Trading
- 🟡 Server-side bots 24/7 — DCA done (BotExecutorService + bot_orders, paper stub via IBotOrderExecutor); grid/trailing + real exchange exec TODO
- 🔴 Server-side copy-trading
- 🔴 Cloud backtesting
- 🔴 Multi-exchange order routing (best price CEX/DEX)

## Custodial security
- 🔴 MPC / threshold signing (main)
- 🔴 Withdrawal address whitelist + velocity caps (delay done)
- 🔴 Hot/cold split
- 🟡 Vault in prod (code ready, enable via VAULT_ADDR)
- 🔴 Dedicated executor node + firewall (2-node topology)

## Realtime & sync
- ✅ Favorites PULL (server→client) for multi-device (FavoritesSyncService.PullAsync + LoadWatchlist merge)
- 🔴 Settings/watchlist sync across devices
- ✅ In-terminal inbox for ALL users (inbox + /api/inbox) — primary channel; optional phone push (ntfy/Telegram) when a channel is configured

## Client & UX
- 🔴 Web dashboard (account, subscription, keys, bots, history)
- 🔴 Mobile app (view + alerts)
- 🔴 Referral program

## Ops / reliability
- 🟡 Monitoring — `GET /api/admin/collectors` (X-Admin) reports failing and stale collectors out of
  `collector_runs`; `/health` reports the process + database. Nothing polls either yet, and there is
  no status page or Grafana.
- 🟡 Encrypted backups to S3 — `docker/backup` streams `pg_dump` and the bot's `sqlite3 .dump` into
  restic as two separately-retained snapshots (client-side encryption, no plaintext on disk).
  Ships **off**: it logs the risk and idles until `RESTIC_REPOSITORY` + `RESTIC_PASSWORD` are set.
  **Restore never tested.**
- 🟡 Health dashboard in console (live status done — extend)

## Recommended order
1. Deploy what exists (one VPS + domain + Cloudflare + the licence bot) — `docs/DEPLOY.md`
2. Turn backups on and **test a restore** — before the first sale
3. Plan limits per `Edition` ← the bot sells four priced tiers and the server serves them identically
4. Server-side bots 24/7 + price alerts ← "works with PC off"
5. MPC + 2FA (before real money)
6. AI alerts, streaming, web dashboard
