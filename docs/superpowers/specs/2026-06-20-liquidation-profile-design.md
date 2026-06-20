# Liquidation Heatmap → Profile Redesign — Design

**Date:** 2026-06-20
**Project:** CryptoAI Terminal (Avalonia .NET 8 desktop)
**Goal:** Replace the confusing center-diverging liquidation bar chart with a clean "liquidation profile": all bars grow one direction from a single left baseline, positioned vertically by price, color-coded by side. Removes the empty quadrants that made the chart look broken.

## Problem

Today `LiquidationHeatmapViewModel.RenderHeatmap` draws a diverging bar chart on a 1200×460 canvas: long bars extend LEFT from center `Cx=600`, short bars extend RIGHT from center. Vertical position = price (top = +20%, bottom = −20%); the orange dashed line is current price. Because longs only liquidate BELOW price and shorts only ABOVE price, the top-left and bottom-right quadrants are always empty → a lopsided, "broken-looking" chart. The center line reads like an axis but is just a divider, and comparing opposite-direction bar lengths is hard.

## Decision (locked — variant A)

A horizontal liquidation **profile**: every bar starts at the left edge (`X=0`) and extends rightward by a length proportional to its liquidation USD; vertical position stays the price level; color encodes side (teal = long, red = short). Longs (below price) and shorts (above price) occupy different Y bands, so they never overlap — the result is a continuous, gap-free profile.

## Architecture

### 1. `LiquidationHeatmapViewModel.RenderHeatmap` (the core change)

Current per-level drawing:
- long: rectangle from `(Cx, y)` to `(Cx - w, y2)`, `w = LongLiqUsd / maxL * MaxHalf`
- short: rectangle from `(Cx, y)` to `(Cx + w, y2)`, `w = ShortLiqUsd / maxS * MaxHalf`

New drawing:
- Compute a **shared** scale: `maxAll = max(maxL, maxS)` (so long and short bar lengths are directly comparable).
- Usable width: `UsableW = RW - RightPad` with `RightPad = 24` (so the longest bar doesn't touch the right edge).
- long: rectangle from `(0, y)` to `(wL, y2)`, `wL = LongLiqUsd / maxAll * UsableW`.
- short: rectangle from `(0, y)` to `(wS, y2)`, `wS = ShortLiqUsd / maxAll * UsableW`.
- `y` (price→vertical), `BarH`, the current-price line (full-width horizontal), and the price-axis labels are unchanged.
- `Cx` and `MaxHalf` constants are no longer used for bars; remove `MaxHalf` (or repurpose) and keep/remove `Cx` as needed. Add `RightPad`/`UsableW` constants.
- Bar height: bump `BarH` from 16 to ~18 and ensure no negative coordinates, for readability. (Minor; keep within canvas.)

The two separate geometries (`LongBarsGeometry`, `ShortBarsGeometry`) remain, each still filled by its own color in XAML — so the long Path stays teal and the short Path stays red. Both now grow rightward from `X=0`.

### 2. `Views/LiquidationView.axaml` (remove center-split decorations)

- Delete the centre divider `<Line StartPoint="600,0" EndPoint="600,460" … />` (around line 135).
- Delete the two overlay labels `◀ LONGS` (≈145-146) and `SHORTS ▶` (≈147-148).
- Keep: the two bar `Path`s, the current-price `Path`, the separate price-axis label Canvas, and the bottom legend ("Long liquidations (bears buy here)" teal / "Short liquidations (bulls sell here)" red) — the legend now carries the long/short meaning that the removed center labels used to.

### 3. Dashboard mini widget (`Views/Dashboard/Widgets/LiqHeatmapWidget.axaml`)

No change needed — it renders the same `LongBarsGeometry`/`ShortBarsGeometry`/`CurrentPriceGeometry` and updates automatically. (It already has no center divider/labels.)

## What does NOT change

- Data model, `LiquidationDataService`, leverage model, proximity alerts, cluster overlay, price-axis labels, stats cards (Top Long/Short Liq Level), AI Liquidity Magnet, symbol chips, Longs/Shorts toggles.
- The canvas size (1200×460) and the Path/Canvas binding structure.

## Error handling

- Empty/zero data: `RenderHeatmap` already early-returns when `_levels.Count == 0 || _currentPrice <= 0`. With `maxAll` possibly 0 when both sides empty, guard `maxAll <= 0 → return` to avoid divide-by-zero.

## Testing

- **Unit (Core.Tests):** after a render with a known set of levels, both long and short bars start at `X=0` (left baseline) and every bar's right edge ≤ `UsableW` (within canvas). Concretely: expose enough to assert the geometries' bounds — assert `LongBarsGeometry.Bounds.X == 0` (or ≈0) and `LongBarsGeometry.Bounds.Right <= UsableW`, same for shorts; and that a larger-USD level yields a wider bar (monotonic). If geometry bounds are awkward to assert, extract the width math into a pure helper `BarWidth(usd, maxAll, usableW)` and unit-test that (0 at usd=0, usableW at usd=maxAll, half at usd=maxAll/2).
- **Manual smoke:** open the Liquidation page — bars form a single left-anchored profile, no empty quadrants, no center line/labels; current-price dashed line spans full width; the dashboard Liq-map widget shows the same.

## Files

- `CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs` — `RenderHeatmap` rewrite + constants; extract `BarWidth` helper.
- `CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml` — remove center divider + LONGS/SHORTS labels.
- `CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs` — `BarWidth` unit tests.
