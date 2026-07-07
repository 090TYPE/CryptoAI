# DEX Trading Terminal — Full Redesign & Paper-PERP Design

**Date:** 2026-07-03
**Branch:** feat/trading-desk-redesign
**Status:** Draft for review

## Goal

Rebuild the DEX side of the trading desk (`TradingDeskView.axaml`, currently the
plain 3-column block at lines ~867–1174) into the same polished terminal shell the
CEX side already uses, faithful to the reference mockup's DEX design — but with a
**purple** accent and **all modes fully interactive**:

- **SWAP** — real spot AMM execution (already backed by `DexTradingViewModel`).
- **PERP** — perpetual futures on a **paper / simulation engine** (mark price,
  funding, order book, positions, uPnL/ROE, liquidation) mirroring the CEX paper
  futures desk. Paper only; no on-chain perp protocol in this scope.
- **Order types**: MARKET, LIMIT, STOP, plus **DCA** and **GRID** plans.
- DEX-specific controls: slippage tolerance, **gas priority**, **MEV protection**,
  on-chain badge, funding / protocol info, SWAP preview.

Honesty rule: controls that move real value (SWAP execution, gas oracle) are wired
to real services. PERP, order book, funding, positions are clearly **paper**
(labelled as such in the UI, gated behind the existing paper-trading guard). No
control is a dead no-op — everything responds and updates.

## Non-goals (this scope)

- Real on-chain perpetuals (GMX/Hyperliquid/dYdX). Deferred to a later spec.
- Real MEV bundle submission (Flashbots). The MEV toggle sets a real preference
  flag consumed by the paper engine; live relay is out of scope.
- Sniper / Copy-trading modes (already have dedicated views; not folded in here).

## Design system

Reuse `TradingDeskStyles.axaml` `Td*` classes. Add a **purple DEX accent** variant:

- accent text `#a855f7`, border `#6a2a9a`, bg `#150a20`, badge border `#3a1f6e`.
- New style selectors: `Button.TdSeg.active.dex` (purple active), `Border.TdDexBadge`.

## Architecture

### Core / engine (new, headless, unit-tested)

1. **`DexPerpPaperEngine`** (`CryptoAITerminal.AIEngine` or `Core`) — deterministic
   paper perpetuals engine. Pure C#, no UI, fully unit-testable (mirrors
   `AiSignalDeskProvider` / `AiSignalDeskTests` pattern).
   - State: open positions (side, size, entry, leverage, margin mode, margin,
     liquidation price, uPnL, ROE), working orders (LIMIT/STOP/DCA/GRID), fills,
     realised session PnL, accrued funding.
   - `Tick(decimal markPrice)`: recompute uPnL/ROE, fire working orders whose
     trigger the price crossed, check liquidation, accrue funding on interval.
   - Actions: `PlaceMarket`, `PlaceLimit`, `PlaceStop`, `CreateDcaPlan`,
     `CreateGridPlan`, `Close`, `Reverse`, `CancelOrder`.
   - Liquidation price from leverage + margin mode (cross/isolated), maintenance
     margin constant.

2. **`DexPerpMarketSimulator`** — seeded random-walk mark price anchored to the
   selected token's real spot price; funding-rate model (8h settlement); synthetic
   L2 order book (bids/asks with cumulative totals + depth bars) around the mark.

3. **`GasOracleService`** (`TerminalUI/Services`) — real gas price per chain from
   the wallet's RPC (`eth_gasPrice`) with a short cache; maps to four priority
   tiers (slow/standard/fast/instant) with gwei + rough USD cost. Falls back to a
   sensible simulated ladder if the RPC is unavailable.

### ViewModels

4. **`DexPerpTradingViewModel`** (new) — owns `DexPerpPaperEngine` +
   `DexPerpMarketSimulator`; a `DispatcherTimer` drives ticks. Exposes bindable
   state for the PERP terminal: side, order type, leverage, margin mode, TP/SL,
   size %, funding/protocol info, positions, working orders, fills, order book
   levels, mark/liq/margin/ROE telemetry. Reuses existing row VMs where shapes
   match (`OrderBookLevelViewModel`, and new `DexPerpPositionViewModel` /
   `DexPerpOrderViewModel` / `DexPerpFillViewModel`).

5. **`DexDeskViewModel`** (new, thin) — coordinates the two sub-modes and shared
   DEX chrome: `VenueMode` (SWAP|PERP), `OrderType`, gas priority, MEV toggle,
   slippage. Holds references to the existing `DexTradingViewModel` (swap) and the
   new `DexPerpTradingViewModel`. Exposed on `MainWindowViewModel` as `DexDeskVM`.
   The existing `DexTradingVM` stays for SWAP bindings; `DexDeskVM` adds the
   PERP/mode/gas/MEV surface. (Alternative considered: cram everything into
   `DexTradingViewModel` — rejected; that file is already 1963 lines.)

6. Small addition to `DexTradingViewModel`: `SelectSlippagePresetCommand(string)`
   setting the existing real `SlippagePercent` (0.1/0.5/1.0/3.0), with active-state
   helpers.

### View

7. Replace the DEX block in `TradingDeskView.axaml` with a 3-zone terminal:
   - **Top status bar**: token identity + purple `SWAP·DEX` / `PERP·DEX` badge;
     live price/liquidity/high/low/vol; **CEX/DEX** venue toggle + **SWAP/PERP**
     mode toggle; wallet equity + route/paper guard dot; gas indicator (DEX only).
   - **Left ticket** (mode-aware): BUY/SELL; order-type row
     (MARKET/LIMIT/STOP/DCA/GRID); SWAP fields (quote asset, amount, swap preview)
     or PERP fields (leverage slider, cross/isolated, size %, TP/SL, liquidation
     preview); shared DEX cards: slippage, gas priority (4 tiers), MEV toggle;
     LIMIT/STOP trigger price; DCA (interval/count/total); GRID (range/levels);
     execution-guard chips; primary BUY/SELL buttons + status.
   - **Center**: metric row + timeframe toolbar + `CexCandlestickChart` + trade
     tape; blotter tabs — SWAP: Trades / Wallet / Chart Info; PERP: Positions /
     Orders / Fills / Chart Info.
   - **Right rail** (mode-aware): SWAP → token market list + honeypot AI verdict;
     PERP → simulated order book + position/AI card + risk/wallets.
8. Purple style variants added to `TradingDeskStyles.axaml`.

## Data flow

`DexTradingViewModel.SelectedToken` (real price/liquidity) is the anchor.
`DexDeskVM` feeds the selected token's spot price into `DexPerpMarketSimulator`,
which produces the mark-price stream + order book the `DexPerpPaperEngine` ticks
against. SWAP execution flows unchanged through `DexTradingViewModel` → wallet
gateway. Gas oracle reads the wallet's active RPC.

## Testing

- `DexPerpPaperEngineTests` (new, in `CryptoAITerminal.Core.Tests`): market fill
  opens a position at mark; leverage → correct margin & liquidation price; a limit
  order fires when price crosses; a stop fires on the opposite cross; liquidation
  closes the position and books loss; DCA plan schedules N child orders; grid plan
  lays N buy/sell rungs across the range; close/reverse math; funding accrual sign.
- `DexPerpMarketSimulatorTests`: mark price stays positive and anchored within a
  bounded band of spot; order book is monotonic with positive cumulative totals.
- Existing suite must stay green.

## Phasing (incremental, each phase compiles + tests + runs)

- **Phase 1 — Shell + SWAP redesign.** Terminal shell, purple accent, top-bar
  venue + mode toggles, SWAP fully wired to `DexTradingVM`, slippage presets, gas
  priority + MEV controls (state real, gas oracle live). PERP toggle visible but
  shows a "paper engine warming up" placeholder. Immediate visual parity with CEX.
- **Phase 2 — Paper PERP core.** `DexPerpPaperEngine` + `DexPerpMarketSimulator` +
  `GasOracleService` with unit tests. Headless; no PERP UI yet.
- **Phase 3 — PERP terminal UI.** Leverage/margin/TP-SL ticket, simulated order
  book, positions/orders/fills blotter, funding/protocol info, liquidation
  preview — bound to the engine and ticking live.
- **Phase 4 — LIMIT/STOP + DCA/GRID.** Order-type modes in the ticket, working-
  order management + cancel, DCA/grid plan builders, blotter rows for scheduled
  orders.

## Open questions

- Preferred anchor chain/network for the paper PERP demo (default: follow the
  selected token's chain; mark simulator is chain-agnostic).
- Gas oracle API preference (default: wallet RPC `eth_gasPrice`; no third-party key).
