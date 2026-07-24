# P0 Vertical Slice — Smoke Checklist

_Date: 2026-07-24. Branch: `feat/p0-server-first-slice`. Companion to the
[plan](2026-07-24-p0-server-first-vertical-slice.md) and [spec](../specs/2026-07-24-p0-server-first-vertical-slice-design.md)._

## What is verified automatically

- **724/724 unit tests pass** (`dotnet test CryptoAITerminal.Core.Tests -c Release`), up from 713 at the
  start of the slice.
- `Core`, `Executor`, `Server.Data`, `TerminalUI`, `Server.Api` all build with 0 errors.
- `TradingService` is covered by 5 tests: happy path, risk block, gateway throw, duplicate
  `ClientOrderId` (idempotency), and reduce-only close.

## What still needs a manual smoke run

The slice cannot be exercised end to end by unit tests: it needs a live Postgres and a real
exchange key. Run this once before trusting the path.

### Prerequisites
1. Apply the migration: `db/018_trade_orders.sql` (creates `trade_orders`).
2. Set `CRYPTOAI_KEK_B64` on the API — without it the trade endpoints answer
   **503 `encryption_not_configured`** by design.
3. Store a **trade-only** exchange key for the test user (keys with withdraw permission are
   refused on purpose).
4. Optional: `MANUAL_MAX_NOTIONAL_USD` (defaults to 5000) to size the risk gate.

### Checks

| # | Action | Expected |
|---|---|---|
| 1 | `POST /api/trade/order` with a small futures market order | `Accepted=true`, an `OrderId`, one `trade_orders` row with `status='placed'` |
| 2 | Same request again, **same `ClientOrderId`** | `Accepted=true`, **no second exchange order**, still one row (idempotency) |
| 3 | Order with notional above the cap | `Accepted=false`, reason mentions the cap, **no** gateway call, no `placed` row |
| 4 | Connect a SignalR client to `/hubs/trade` with `X-License`, then place an order | `orderStatus` event arrives for that user only |
| 5 | Order that the exchange rejects | `Accepted=false`, row `status='rejected'`, a `notification` event, **no 500** |
| 6 | `GET /api/trade/positions?exchange=…` | Positions with **signed** quantity (long +, short −) in base asset |
| 7 | `POST /api/trade/order/{id}/cancel?exchange=…` | `Ok=true`, order gone from the exchange |
| 8 | Unset `CRYPTOAI_KEK_B64`, retry #1 | **503 `encryption_not_configured`** (not a 500) |

### Desktop routing
The desktop flag `USE_SERVER_TRADING` is **off by default** and `_serverTrading` is not
constructed yet, so the terminal still trades through the in-process gateway. Wiring the client
into DI is the first task of the next slice — until then the flag alone changes nothing.

## Known gaps (deliberate, carried forward)

- **No background fill poller.** The API emits `orderStatus` on accept and `notification` on
  reject; real fills coming back from the exchange are not streamed yet.
- **`OrderJournalRepository` has no automated test** — it needs a live database.
- Untested service paths: empty `ClientOrderId`, missing key, withdraw-permission refusal,
  `CancelAsync`, `GetPositionsAsync`.
- Only the manual futures **market** order is migrated. Bots, DEX, spot, and working
  orders still run in-process and follow later using this same template.
