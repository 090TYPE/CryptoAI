# CryptoAI Terminal — Roadmap / Development Plan

_Date: 2026-07-24. Status: approved high-level design. Each phase gets its own spec → plan → implementation cycle._

## Decisions (locked)

- **3Commas** — integrate the **real 3Commas API** (pull their signals / SmartTrade / bots), then enrich each signal with our existing `AIEngine` (Claude/OpenAI). "AI 3Commas" = their signals + our AI verdict layer.
- **Signal automation** — **configurable per signal source**: suggestion-only · one-click-to-ticket · semi-auto (executed under risk limits + confirmation).
- **Server migration** — **server-first from now**: new logic lives in the server projects; the desktop becomes a thin client.

## 0. Architectural principle: Server-first

Everything new is built as: logic on the server, desktop = thin client.

- **Foundation already exists:** `Server.Api` (ASP.NET Core), `Server.Data` (PostgreSQL/TimescaleDB), `Executor` (order/bot execution), `CandleWorker`, `WebApi` (webhooks), Vault (keys). New services go here, not in `TerminalUI`.
- **Blocker to fix first:** trading logic is currently trapped in the god-object `MainWindowViewModel` (~385 edges). For server-first it must be extracted into reusable services behind interfaces (extend the existing `IExchangeGateway` pattern). Otherwise "move to server" = rewrite.
- **Client↔server contract:** REST + WebSocket/SignalR for live streams (prices, signals, order status, notifications). Desktop subscribes; it does not compute.
- **Multi-user from the start:** the server already resolves `uid` from the license — all new tables/queues are per-user, exchange keys only in Vault via the server-side key-proxy.

## 1. Phase A — 3Commas AI assistant for manual trading (customer priority)

- **`Server.Api` → new `ThreeCommasService`:** 3Commas API keys in Vault per-user. Server polls/subscribes to 3Commas (marketplace signals, SmartTrade/DCA bot state, 3Commas webhooks) and normalizes to an internal `SignalDto` (symbol, side, entry, TP/SL, size-hint, confidence, source, rationale).
- **AIEngine enrichment:** each incoming 3Commas signal runs through `AIEngine` → take/skip verdict, risk score, plain-language explanation. This is "AI 3Commas": their signals + our AI layer.
- **Delivery:** server pushes signals over SignalR → desktop shows them on the **manual desk** (a signal panel next to the ticket) and mirrors them into dashboard notifications.
- **Configurable automation (per signal source):**
  - *Suggestion* — signal card, user enters the trade manually.
  - *One-click* — signal fills the ticket (symbol/side/size/TP/SL), user confirms. Reuses the (now-fixed) `PlaceCexMarketOrderAsync` + gateways.
  - *Semi-auto* — server `Executor` executes under risk limits (`RiskManager`, kill-switch/LiveGate) + confirmation.
  - Mode is chosen **per source** (e.g. premium channel = one-click, experimental = suggestion-only).
- **Why server-first matters here:** 3Commas polling + AI enrichment must run 24/7 even with the desktop closed → background worker in `Executor`, not the UI.

## 2. Phase B — Dashboard: notifications + size editing

- **Clearer notifications:** one server-side notification stream (SignalR) with typed events (fill, TP/SL hit, signal, risk-block, key error) + priority/color. Desktop renders a filterable feed; critical events (kill-switch, risk limit) are modal. Server can also route to Telegram (existing `LicenseBot`/notifier infra).
- **Inline size editing:** in the dashboard/positions view — direct edit of position/order size (partial close %, working-order qty change, 25/50/75/100% presets). The money-math foundation is now healthy (partial-close margin, contract multipliers, signed positions were all fixed in the perp audit).
- **Size/risk presets:** "risk per trade %", auto qty from balance and stop (server-side calc so it matches the bots).

## 3. New trading technologies & methods

All built as server-first services.

**Methods / strategies**
- **Grid v2:** ATR-adaptive step; hedge-grid (long+short) on the now-fixed one-way/hedge foundation.
- **Trailing/chandelier exit, breakeven-mover** as reusable server modules (partly in `TpSlManager` today).
- **Server portfolio/risk engine:** unify `CorrelationMatrixService` + `RiskManager` into a server `PortfolioRiskService` (VaR, correlations, per-account max exposure).
- **Market-making / passive maker** for DEX-perp (Hyperliquid) — post-only ladders.

**Technologies**
- **TimescaleDB** (already in stack) — hypertables for candles/trades/PnL, continuous aggregates for the dashboard.
- **Server-side backtest** as a service (today `BacktestEngine` is in the UI) — job queue, results in DB.
- **Order event-sourcing** — journal of all orders/fills in DB (needed for the stuck-withdrawal reaper and grid idempotency flagged in the audit).
- **Observability:** OpenTelemetry + execution metrics (gateway latency, slippage).

## 4. Phases

| Phase | Content |
|---|---|
| **P0. Server-first foundation** | REST+SignalR contract; extract trading logic out of `MainWindowViewModel` into services; order event-log |
| **P1. 3Commas AI assistant** | `ThreeCommasService` + AI enrichment + signal panel + configurable automation |
| **P2. Dashboard** | Notification stream + inline size editing + risk presets |
| **P3. Trading methods** | Grid v2, server risk engine, server backtest |
| **P4. Close audit follow-ups** | Grid idempotency (`clientOrderId`), stuck-withdrawal reaper, cross-exchange inventory model |

## Next step

Detailed design (full spec → implementation plan) starts with **P0 (server-first foundation)** — without extracting logic from the god-object, the 3Commas feature can't sit cleanly on the server. Each subsequent phase gets its own spec.
