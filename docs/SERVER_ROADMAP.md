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
- 🔴 **Billing / subscriptions** (Stripe / CryptoBot / ЮKassa) → issue license on payment; tiers Free/Pro/VIP
- 🔴 **Plan limits** (rate limit, #bots, feature gating) per tier
- 🔴 **MPC / threshold signer** for withdrawals (replace StubWithdrawalSigner) — before real funds
- 🔴 **Deploy to Timeweb** (2-node Amsterdam) + domain + Cloudflare
- 🔴 **2FA** on login + withdrawal

## AI
- 🔴 Real-time AI alerts (Claude flags anomalies on favorites)
- 🔴 Server-side morning AI briefing (push to all)
- 🔴 AI token scoring (rug risk / narrative) cached for everyone
- 🔴 AI pre-trade review before autonomous trades
- 🔴 RAG over collected data (Claude answers grounded in our DB)

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
- 🔴 Push notifications (Telegram/ntfy/mobile) from server

## Client & UX
- 🔴 Web dashboard (account, subscription, keys, bots, history)
- 🔴 Mobile app (view + alerts)
- 🔴 Referral program

## Ops / reliability
- 🔴 Monitoring + status page (Grafana over collector_runs)
- 🔴 Encrypted DB backups to S3
- 🟡 Health dashboard in console (live status done — extend)

## Recommended order
1. Deploy what exists (Timeweb + domain)
2. Billing + tiers
3. Server-side bots 24/7 + price alerts ← "works with PC off"
4. MPC + 2FA (before real money)
5. AI alerts, streaming, web dashboard
