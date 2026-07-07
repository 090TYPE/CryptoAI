# App Action Layer + Agentic Copilot — Design

**Date:** 2026-07-07
**Status:** Approved (brainstorm), pending spec review
**Scope:** Spec 1 of 3. Foundation that later Trading-AI (Spec 2) and AI-Signals (Spec 3) build on.

## Problem

The global assistant (`CopilotViewModel` + `CopilotAgentService`) is **read-only** — it can inspect
account/market via read tools but "can never trade — it is advisory." The user wants it to *act*:
navigate pages, fill fields, arm/place orders, set values from data — "do everything in the app on request."

Separately, the Trading and AI-Signals pages need new AI features (T1–T5, S1–S6). Those overlap
heavily with "assistant can do it," so both must stand on **one shared action layer** rather than
duplicating execution logic.

## Goals

- A typed, testable catalog of **app actions** covering: navigation + reads, Trading ticket + orders
  (CEX+DEX), signals + alerts, and bots/settings/wallet.
- Upgrade the global Copilot into an **agent** that plans and calls those actions.
- **Safety-first:** mutating actions are confirmed by the user by default; a separate opt-in auto-mode
  lets the agent act unattended. Existing money-gates are never bypassed.
- Full audit log of every action.

## Non-Goals

- Trading-AI features (T1–T5) and Signals features (S1–S6) — separate specs, this layer enables them.
- Free-form UI automation (manipulating arbitrary controls). Actions are an explicit, bounded catalog.
- New model/provider work — reuse `AiRuntime` agent runners (`ClaudeAgentRunner`/`OpenAiAgentRunner`).

## Architecture

```
User NL ─▶ AppAgentService ─▶ AiRuntime agent runner
                │                    │
                │      read tools ───┘  (execute immediately: get_price, get_positions, …)
                │
                │   mutating tools ─▶ proposed AppAction
                │                          │
                ▼                          ▼
        confirm mode (default)      auto mode (opt-in, OFF)
                │                          │
          Action Tray (Approve/Reject)     │
                └──────────┬───────────────┘
                           ▼
                  IAppActionContext.ExecuteAsync  (UI thread)
                           │
              existing VM commands + money-gates
              (WalletVM.TryApproveLiveExecution, RiskManager, HL testnet)
                           ▼
                     AppActionResult ─▶ back to model ─▶ chat confirmation
                           │
                           ▼
                     AppActionAuditLog
```

### Components (small, isolated units)

1. **`IAppAction`** (`Services/AppActions/IAppAction.cs`)
   - `string Id` (e.g. `nav.goto`, `trade.arm_limit`), `string Category`, `string Description` (for the model),
     `object ParamSchema` (JSON schema), `bool IsMutating`.
   - `string Preview(JsonElement args)` — human sentence shown before execution ("Arm BUY LIMIT 0.5 ETH @ 3200 on Binance").
   - `Task<AppActionResult> ExecuteAsync(JsonElement args, IAppActionContext ctx, CancellationToken ct)`.
   - Pure metadata + logic; no direct VM references (goes through `ctx`). Unit-testable.

2. **`AppActionRegistry`** — holds all actions, exposes them as agent tool definitions and by id.
   Pure catalog, unit-testable (schema present, ids unique, mutating flags correct).

3. **`AppActionResult`** — `bool Ok`, `string Message`, optional `string Detail`. Structured for model + UI.

4. **`IAppActionContext`** — the ONLY bridge to the app. Methods map 1:1 to existing operations, e.g.:
   - Navigation/read: `NavigateTo(sectionKey)`, `GetBalanceUsdt()`, `GetOpenPositions()`, `GetMarketSnapshot(symbol)`.
   - Trading ticket: `SetTradingSymbol`, `SetSide`, `SetQuantity/SetUsd`, `SetLeverage`, `SetOrderType`,
     `SetLimitPrice/SetTp/SetSl`, `ArmLimit`, `ArmTp`, `ArmSl`, `PlaceMarket`, `ClosePosition`.
   - DEX: `SelectDexToken`, `DexBuy`, `DexSell`, perp `SetPerpMode(live/paper)`, `PlacePerp`.
   - Signals/alerts: `ApplySignalToTicket(signalId)`, `AddPriceAlert(symbol, price, condition)`, `ArmWorkingOrderFromSignal(...)`.
   - Bots/settings/wallet: `ConfigureGridBot(...)`, `ConfigureDcaBot(...)`, `SetSetting(key,value)`, `SelectWallet(...)`.
   - Impl **`MainWindowAppActionContext`** — adapts to `MainWindowViewModel`, `DexDeskViewModel`, `AlertsVM`,
     `GridBotVM`, `WalletVM`. Marshals to UI thread. No new business logic — calls existing commands/methods
     (`SelectMainTab`, `SelectOrderTypeCommand`, `PlaceBuyLimit`, `ArmTakeProfit`, `ExecuteBuyMarket`,
     `AlertsVM.AddAlertCommand`, `DexDeskVM.Perp.ToggleLiveTradingCommand`, etc.).

5. **Action implementations by domain** (each a small file):
   - `NavigationActions` — `nav.goto`, `read.balance`, `read.positions`, `read.market`, `read.portfolio`.
   - `TradingActions` — `trade.set_ticket`, `trade.arm_limit`, `trade.arm_tp`, `trade.arm_sl`,
     `trade.place_market`, `trade.close`, `dex.buy`, `dex.sell`, `perp.set_mode`, `perp.place`.
   - `SignalAlertActions` — `signal.apply_to_ticket`, `alert.add`, `signal.arm_working_order`.
   - `BotWalletActions` — `bot.configure_grid`, `bot.configure_dca`, `settings.set`, `wallet.select`.

6. **`AppAgentService`** — evolution of `CopilotAgentService`. Builds the tool set (read tools execute
   immediately; mutating tools resolve to a *proposal*). Routes proposals by mode. Keeps offline fallback.

7. **`AgentActionTrayViewModel`** + view — pending proposals (Approve / Reject / Approve-all) + activity log.

8. **Copilot UI upgrade** — chat renders proposed-action cards; a **mode toggle**: `CONFIRM` (default) vs
   `AUTO` (opt-in). Enabling AUTO shows an explicit consent dialog.

## Safety Model

- **Default = CONFIRM.** Every `IsMutating` action becomes a tray proposal with a `Preview` string.
  Nothing with side effects runs without an explicit Approve.
- **AUTO mode** — persisted toggle, default **false**. Turning it on requires an explicit consent dialog.
  Even in AUTO:
  - Real CEX orders still pass `RiskManager.CanPlaceOrder` + `WalletVM.TryApproveLiveExecution`.
  - DEX/Hyperliquid live still needs a trade-enabled wallet and defaults to **testnet**.
  - The agent calls the *same* gated code paths as the manual UI — it cannot bypass them.
- **Audit log** (`AppActionAuditLog`, persisted to `%LocalAppData%\CryptoAITerminal\agent-actions.json`):
  every proposal, approval/rejection, execution result, and error, timestamped.
- Navigation and read actions are non-mutating → run without confirmation.

## Data Flow (worked example)

User: "открой трейдинг, поставь лонг BTC на 500$ 5x и стоп -2%"
1. `AppAgentService` → model.
2. Model calls `nav.goto{section:"trading"}` (non-mutating) → executes → Trading page shown.
3. Model calls `read.market{symbol:"BTCUSDT"}` → returns price.
4. Model proposes `trade.set_ticket{symbol:BTCUSDT, side:long, usd:500, leverage:5, mode:futures}` and
   `trade.arm_sl{price:<-2% from entry>}` → tray shows two cards with previews.
5. User Approves → `MainWindowAppActionContext` sets ticket fields + arms SL via existing commands
   (subject to risk gates) → results returned.
6. Model confirms in chat: "Готово: лонг BTC 500$ 5x, стоп на …".

## Error Handling

- Param validation against schema before execution; invalid → `AppActionResult.Ok=false` with reason to model.
- Gate rejections (risk/wallet) surface as normal failed results (not exceptions) so the model can explain.
- All execution wrapped in try/catch; UI-thread marshalling failures reported, never crash the agent loop.

## Testing

- **Pure/unit (xUnit, matches existing `CryptoAITerminal.Core.Tests` pattern):**
  - Registry: unique ids, every action has schema + description, mutating flags correct.
  - Param validation + `Preview` text for each action (table-driven).
  - `AppAgentService` routing: mutating tool → proposal in CONFIRM mode; executes in AUTO mode — verified
    with a **fake `IAppActionContext`** that records calls instead of touching real VMs/exchanges.
- **Manual e2e (user, after keys):** NL command → tray → approve → observe the real ticket/nav change;
  AUTO mode on testnet.

## Rollout / Phasing (within this spec)

1. Registry + `IAppAction` + `AppActionResult` + `IAppActionContext` interface + fake context + tests.
2. `NavigationActions` + read actions wired through `MainWindowAppActionContext` (lowest risk, immediately useful).
3. `AppAgentService` + tray VM/UI + CONFIRM mode; Copilot upgraded to show/approve proposals.
4. `TradingActions` (CEX+DEX+perp), then `SignalAlertActions`, then `BotWalletActions`.
5. AUTO-mode toggle + consent dialog + audit log.

## Follow-on specs (not this one)

- **Spec 2 — Trading AI (T1–T5):** AI TP/SL (wire `DynamicTpSlAiProvider` to the ticket), AI second-opinion
  pre-order, NL order entry, position copilot, explain-this-move. Built as actions + existing providers.
- **Spec 3 — AI Signals (S1–S6):** "trade this signal", signal→alert/working-order, composite score
  (AI+funding+whale+sentiment), confidence calibration, portfolio-aware ranking, scheduled Telegram briefing.

## Open questions (resolve during planning)

- Exact section keys for `nav.goto` (reuse `NormalizeSectionKey`).
- Whether AUTO mode needs a per-session $ cap in addition to existing risk limits (default: rely on RiskManager; revisit).
