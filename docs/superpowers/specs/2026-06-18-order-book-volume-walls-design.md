# Order-Book Volume Walls — Highlight Large Resting Orders

**Date:** 2026-06-18
**Status:** Approved design, pending implementation plan
**Area:** CryptoAI Terminal — Trading page (CEX order book + candlestick chart)

## Overview

Highlight large resting limit orders ("walls") both in the CEX order book
(depth-of-market) on the Trading page and on the candlestick chart next to it.
The user sets, per trading symbol, the minimum order size above which an order is
highlighted, choosing whether that threshold is measured in **USD notional**
(`price × quantity`) or in **base-asset quantity**.

## Goals

- In the order book, visually mark every Bid/Ask level whose size is at or above
  the user's threshold (row background tint + bold size text + an icon).
- On the candlestick chart, draw a horizontal "wall" line at each large level's
  price (green for bids, red for asks), thickness/opacity scaled by relative size.
- A panel directly above the order book lets the user set the threshold for the
  currently selected symbol and toggle the global USD / Quantity mode.
- Thresholds persist per symbol between launches.

## Non-Goals (YAGNI)

- No alerts/sound/notification when a wall appears.
- No deepening of the order book beyond the depth currently fetched (16 levels
  per side via REST). Walls are detected among displayed levels only.
- No DEX support (DEX has no classic order book).

## Data Model

New types in `CryptoAITerminal.Core`:

- `enum WallHighlightMode { Usd, Qty }` — global toggle (one mode for all symbols).
- `BookWallSettings { decimal UsdThreshold; decimal QtyThreshold; }` — stored per
  symbol. A threshold of `0` means "highlighting off" for that mode.
- `readonly record struct BookWall(decimal Price, bool IsBid, double Intensity,
  decimal Notional, decimal Quantity)` (`Core.Models`) — one wall handed to the
  chart. `Intensity` is `0..1`, used for line thickness/opacity.

## Core Logic (pure, testable)

`OrderBookWallDetector` (static, in `CryptoAITerminal.Core`):

- `bool IsLarge(decimal price, decimal qty, WallHighlightMode mode, decimal usdThreshold, decimal qtyThreshold)`
  - `Usd`: `usdThreshold > 0 && price * qty >= usdThreshold`
  - `Qty`: `qtyThreshold > 0 && qty >= qtyThreshold`
- `double Intensity(decimal notional, decimal maxNotional)` →
  `maxNotional <= 0 ? 0 : clamp((double)(notional / maxNotional), 0, 1)`.

Unit tests (`CryptoAITerminal.Core.Tests`):
- USD-mode boundary (just below / equal / above threshold).
- Qty-mode boundary.
- Threshold `0` ⇒ never large, in both modes.
- Intensity scaling incl. `maxNotional = 0` guard.

## Persistence

`BookWallSettingsStore` (TerminalUI/Services):
- In-memory `Dictionary<string, BookWallSettings>` keyed by symbol + a global
  `WallHighlightMode`.
- Persisted to `%LOCALAPPDATA%\CryptoAITerminal\book-walls.json` via the existing
  `AtomicJsonFile` helper. Loaded on startup, saved on change.
- `Get(symbol)` returns the stored settings or a default
  (`UsdThreshold = 250000`, `QtyThreshold = 0`).

## View-Model Changes

`OrderBookLevelViewModel`:
- Add `bool IsLarge`, `decimal Notional`.
- Add presentation getters: `RowBackground` (blends the existing selected tint
  with a wall tint when `IsLarge`), `SizeFontWeight` (`Bold` when large else
  `Normal`), `WallIconVisible` (`IsLarge`).

`CexMarketItemViewModel`:
- Holds the current symbol's `BookWallSettings` (read from the store) and reads
  the global `WallHighlightMode`.
- In `UpdateOrderBook`, after building `BidLevels`/`AskLevels`: run the detector
  over each side, set `IsLarge`/`Notional` per level, compute `maxNotional` for
  intensity, build the `LargeWalls` collection (each tagged with side), and update
  `BidWallCount`/`AskWallCount`.
- Exposes `LargeWalls` (for the chart), `BidWallCount`, `AskWallCount`, and the
  bound threshold properties (`WallUsdThreshold`, `WallQtyThreshold`). Editing a
  threshold recomputes the current book and saves to the store.

The global `WallHighlightMode` lives on `MainWindowViewModel` (or a small shared
settings object the markets read); changing it recomputes the selected market.

## UI

**Threshold panel** (in `TradingDeskView`, directly above the Bid/Ask grid):
- Title (e.g. "Крупные заявки").
- `USD | Объём` toggle bound to the global `WallHighlightMode`.
- `NumericUpDown` bound to the active threshold for the selected symbol; unit
  label shows `$` or the base symbol.
- Small readout: "Стен: {BidWallCount + AskWallCount}".

**Order book rows** (Bid/Ask `DataTemplate`s): bind row background to
`RowBackground`, size `FontWeight` to `SizeFontWeight`, and add an icon
`TextBlock` whose `IsVisible` is `WallIconVisible`.

**Chart** (`CexCandlestickChart`):
- New `StyledProperty<IReadOnlyList<BookWall>?> Walls` (registered in
  `AffectsRender`).
- `DrawWalls(context)` called in `Render` after `DrawDrawings`: for each wall whose
  price is inside `[_minVisiblePrice, _maxVisiblePrice]`, map price→Y via `MapY`,
  draw a horizontal line across `_chartBounds` (bid `#42F5B1` / ask `#FF6B6B`),
  thickness `1.5 + Intensity * 2.5`, semi-transparent, with a small size label near
  the price axis.
- In `TradingDeskView.axaml`: `Walls="{Binding SelectedMarket.LargeWalls}"`.

## Data Flow

Trading page polls the order book (REST, depth 16) →
`MainWindowViewModel` calls `market.UpdateOrderBook(orderBook)` →
`CexMarketItemViewModel` runs `OrderBookWallDetector` with the symbol's stored
thresholds + global mode → sets `IsLarge` on level VMs (book updates) and rebuilds
`LargeWalls` → chart's `Walls` binding fires `AffectsRender` → walls redrawn.
Threshold panel edits write through to `BookWallSettingsStore` and trigger a
recompute on the current book.

## Files Touched

- `CryptoAITerminal.Core/Models/` — `BookWall`, `WallHighlightMode`, `BookWallSettings`.
- `CryptoAITerminal.Core/OrderBookWallDetector.cs` (new).
- `CryptoAITerminal.Core.Tests/OrderBookWallDetectorTests.cs` (new).
- `CryptoAITerminal.TerminalUI/Services/BookWallSettingsStore.cs` (new).
- `CryptoAITerminal.TerminalUI/ViewModels/CexMarketItemViewModel.cs` (level VM +
  market VM changes).
- `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` (global mode +
  wiring).
- `CryptoAITerminal.TerminalUI/Controls/CexCandlestickChart.cs` (`Walls` + `DrawWalls`).
- `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml` (panel + row template +
  chart binding).
