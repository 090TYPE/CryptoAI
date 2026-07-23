# P0 — Server-first Foundation (Vertical Slice)

_Date: 2026-07-24. Status: approved design, ready for implementation plan. Parent: [roadmap](2026-07-24-cryptoai-roadmap-design.md)._

## Goal

Prove the client↔server architecture end-to-end with ONE money flow — a **manual CEX futures market order** — so every later flow (bots, DEX, 3Commas signals) can be migrated by copying the same pattern. The desktop stops calling the exchange gateway directly for this flow; it calls the server.

## Scope

**In:** manual CEX **futures market order** (open/close), for the desktop's active exchange+symbol:
- place a market order (open long/short, or reduce-only close);
- receive order status / fill over a live stream;
- read open positions;
- cancel an order.

**Out (YAGNI — migrated later by the same pattern):** bots (Trading/Grid/DCA), DEX/perp DEX, arbitrage, sniper, limit/TP/SL working orders, spot. No further god-object extraction beyond this one flow. 3Commas (P1) and dashboard (P2) are separate phases.

## Components

### 1. `TradingService` (server, in `Server.Api` or a new `Server.Trading` library)
A pure, injectable class — the extracted trading logic, unit-testable.

```csharp
public interface ITradingService
{
    Task<PlaceOrderResult> PlaceMarketAsync(Guid uid, PlaceMarketCommand cmd, CancellationToken ct);
    Task<CancelResult>     CancelAsync(Guid uid, string exchange, string orderId, CancellationToken ct);
    Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(Guid uid, string exchange, CancellationToken ct);
}
```
- Resolves the per-user `IExchangeGateway` (futures) from a `IGatewayFactory` that pulls keys from Vault by `uid` (server-side key-proxy — never returns raw keys to the client).
- Validates the command, runs `RiskManager.CanPlaceOrder`, calls `gateway.PlaceOrderAsync`, journals the order (below), and returns a synchronous ack. Actual fills arrive asynchronously and are pushed over SignalR.
- Idempotency: `PlaceMarketCommand.ClientOrderId` (required). `TradingService` checks the order journal for an existing `ClientOrderId` before placing → safe retries (closes the grid-idempotency gap from the audit for this path).

### 2. Contract

**REST** (`Server.Api`, all under `/api/trade`, auth = `X-License` → `uid`):
- `POST /api/trade/order` — body `PlaceMarketCommand`, returns `PlaceOrderResult`.
- `POST /api/trade/order/{orderId}/cancel?exchange=` — returns `CancelResult`.
- `GET  /api/trade/positions?exchange=` — returns `FuturesPositionDto[]`.

**SignalR hub** `/hubs/trade` (auth = same token; each connection joins group `uid`):
- server → client events: `orderStatus` (`OrderStatusDto`), `positionUpdate` (`FuturesPositionDto`), `notification` (`NotificationDto`).
- prices reuse the existing market-data path for now (a `priceUpdate` event can be added but is not required for the slice — the ticket already has a price source).

**DTOs** (shared contract library `Server.Common` so client and server agree):
```csharp
record PlaceMarketCommand(string Exchange, string Symbol, OrderSide Side, decimal Quantity,
                          bool ReduceOnly, int Leverage, FuturesMarginMode MarginMode,
                          FuturesPositionSide PositionSide, string ClientOrderId);
record PlaceOrderResult(bool Accepted, string? OrderId, string? RejectReason);
record OrderStatusDto(string OrderId, string ClientOrderId, string Status, decimal FilledQty,
                      decimal AvgPrice, DateTime UpdatedUtc);
record NotificationDto(string Kind, string Severity, string Message, DateTime AtUtc);
```
Command `Quantity` is a **positive base-asset magnitude**; direction comes from `Side` (Buy/Sell) + `PositionSide`. Position DTO `Quantity` is **signed** (long +, short −) — the convention fixed in the perp audit. The server converts base→contracts internally per gateway (OKX ctVal, KuCoin multiplier); the client never deals in contracts.

### 3. `IServerTradingClient` (desktop, thin)
```csharp
public interface IServerTradingClient
{
    Task<PlaceOrderResult> PlaceMarketAsync(PlaceMarketCommand cmd);
    Task<CancelResult> CancelAsync(string exchange, string orderId);
    Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(string exchange);
    IObservable<OrderStatusDto>    OrderStatus { get; }
    IObservable<FuturesPositionDto> PositionUpdates { get; }
    IObservable<NotificationDto>    Notifications { get; }
}
```
`MainWindowViewModel`'s manual futures market path calls this client instead of `ActiveFuturesGateway` directly. A feature flag (`UseServerTrading`) lets the desktop fall back to the current in-process path during rollout.

## Data flow

1. Desktop ticket → `IServerTradingClient.PlaceMarketAsync(cmd)` → `POST /api/trade/order`.
2. `TradingService`: idempotency check → validate → `RiskManager` gate → `gateway.PlaceOrderAsync` → journal order (`accepted`/`rejected`) → return `PlaceOrderResult`.
3. Fill/status: a server-side order-status poller (or gateway stream) updates the journal and emits `orderStatus` + `positionUpdate` over SignalR to group `uid`.
4. Desktop updates position/PnL from the stream and renders a `notification`.

## Auth & keys

- `X-License` → `uid` (existing middleware). SignalR handshake carries the same token.
- Exchange keys stay in Vault; `IGatewayFactory` builds a gateway from them server-side. Keys never cross the wire to the client (server-side key-proxy, already a documented pattern).

## Error handling

- Validation / risk-block / gateway rejection → `PlaceOrderResult.Accepted=false` with `RejectReason` (sync) AND a `notification` event (async, feeds P2's notification stream).
- `RiskManager` runs server-side so bots and manual share one risk budget.
- Gateway exceptions are caught in `TradingService`, journaled as `rejected`, surfaced as a typed notification — never crash the hub.

## Persistence

Order journal table in `Server.Data` (TimescaleDB): `(uid, exchange, client_order_id UNIQUE per uid, exchange_order_id, symbol, side, qty, reduce_only, status, reject_reason, created_utc, updated_utc)`. Backs idempotency, status history, and later the audit follow-ups (stuck-order reaper, event log).

## Testing

`TradingService` unit tests (fake `IExchangeGateway` + real `RiskManager`):
- happy path → `Accepted=true`, journal `accepted`, status stream emits fill;
- risk-block → `Accepted=false`, correct `RejectReason`, no gateway call;
- gateway throws → journaled `rejected`, notification emitted, no crash;
- duplicate `ClientOrderId` → second call returns the first result, places nothing (idempotency);
- close path → reduce-only, correct signed qty for long vs short.
Contract test: desktop `IServerTradingClient` (in-memory server) round-trips a place→status flow.

## Migration note

Every other flow (bots, DEX, working orders) migrates by: extract its logic into a server service behind an interface → expose REST command(s) → stream status over the same hub → point the desktop at a thin client. P0 establishes the template; later phases reuse it.
