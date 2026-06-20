# Liquidation Gradient Heatmap — Design

**Date:** 2026-06-20
**Project:** CryptoAI Terminal (Avalonia .NET 8 desktop)
**Goal:** Replace the blocky left-anchored bar profile with a true gradient heatmap: full-width horizontal bands, one per price level, where color = side and brightness/opacity = liquidation magnitude. This is the iconic "liquidation heatmap" look and removes the crude-bars / messy-overlay appearance.

## Problem

The current `LiquidationHeatmapViewModel.RenderHeatmap` draws discrete bars (two `StreamGeometry` Paths). Even left-anchored, the result looks blocky: thick uneven bars with gaps, plus a `ClusterOverlay` of black-outlined rectangles near the price line that overlap into a mess. It does not read like a heatmap.

## Decision (locked)

A vertical **heat strip**: the chart area is tiled, top to bottom, by contiguous full-width horizontal bands — one per price level in the ±20% range. Each band's hue encodes the side (teal `#21E6C1` for long levels, which sit below price; red `#FF4444` for short levels, above price). Each band's opacity encodes magnitude (`liqUsd / maxAll`), so big clusters glow bright and empty levels fade into the background. No bars. The cluster-overlay black outlines are removed.

## Architecture

### 1. Data: a `HeatBand` model + bands list on the VM

New tiny record (in the VM file or `Models`):
```csharp
public sealed record HeatBand(double Y, double Height, Avalonia.Media.IBrush Fill);
```

`LiquidationHeatmapViewModel` gains:
```csharp
public IReadOnlyList<HeatBand> HeatBands { get; private set; }  // raised via RaiseAndSetIfChanged
```
and drops the public `LongBarsGeometry` / `ShortBarsGeometry` (the `CurrentPriceGeometry` stays).

### 2. `RenderHeatmap` rewrite

- Keep the existing range computation (`minPrice`, `maxPrice`, `range`, `inRange`) and `maxAll = max(maxL, maxS)` with the `maxAll <= 0 → empty, return` guard.
- Sort `inRange` by price descending (top = highest price).
- For each level, compute its top Y from price (same mapping as today: `y = (1 - (price - minPrice)/range) * RH`). The band's `Height` is the distance to the next (lower-price) level's Y, so bands are **contiguous and gap-free**; the last (lowest) band extends to `RH`. Clamp Y/Height into `[0, RH]`.
- Side & magnitude: a level is a "short" level if its price ≥ current price (use `ShortLiqUsd`), else a "long" level (use `LongLiqUsd`). `mag` = the side's USD. `intensity = Intensity(mag, maxAll)`.
- Respect the `_showLongs` / `_showShorts` toggles: skip a band whose side is toggled off (it renders as background).
- Fill = an `ImmutableSolidColorBrush` of the side hue with alpha = `intensity` (byte `0..255`). Pure helper:
  ```csharp
  public static double Intensity(double mag, double max)
      => max <= 0 ? 0.0 : 0.06 + 0.94 * System.Math.Clamp(mag / max, 0.0, 1.0);
  ```
  (floor 0.06 so even tiny levels are faintly visible; 1.0 at the max cluster.)
- Assign `HeatBands = list;`. Keep building `CurrentPriceGeometry` and the price-axis labels exactly as today.

### 3. Remove the cluster overlay rendering

- The `ClusterOverlay` collection drives the black-outlined rectangles. Remove its rendering from the views (Task in plan). The VM's `RebuildClusterOverlay` may stay (harmless) or be left unused; do not delete unrelated alert logic that reads the same data. Only the **visual** overlay is dropped.

### 4. `Views/LiquidationView.axaml`

- Replace the two bar `<Path>` elements on the heatmap `Canvas` with an `ItemsControl ItemsSource="{Binding LiquidationHeatmapVM.HeatBands}"` whose `ItemsPanel` is a `Canvas` and whose item template is a `Rectangle Width="1200" Height="{Binding Height}" Fill="{Binding Fill}"` with `Canvas.Top="{Binding Y}"`.
- Keep the `CurrentPriceGeometry` `Path` (dashed price line) ON TOP of the bands, the price-axis label Canvas, the bottom legend.
- Remove the center divider + `LONGS`/`SHORTS` overlay labels (already done in the prior profile commits) and any `ClusterOverlay` ItemsControl on this canvas.

### 5. Dashboard mini widget `Views/Dashboard/Widgets/LiqHeatmapWidget.axaml`

- It currently renders `LongBarsGeometry` / `ShortBarsGeometry` Paths (which are being removed). Replace them with the same `HeatBands` `ItemsControl` (Canvas 1200×460 inside its existing Viewbox-less scaled container), keeping the current-price Path. Keep it compact.

## What does NOT change

- Range (±20%), price→Y mapping, current-price line, price-axis labels, leverage model, proximity alerts, stats cards, AI magnet, symbol chips, Longs/Shorts toggles (now gate band visibility).

## Error handling

- `maxAll <= 0` (no data) → `HeatBands = []` and return (existing guard pattern).
- Degenerate `range <= 0` already guarded by the early `_levels.Count == 0 || _currentPrice <= 0` return.
- Band heights clamped to `≥ 0` and within `RH`.

## Testing

- **Unit (Core.Tests):**
  - `Intensity(0, max)` → 0.06 (floor); `Intensity(max, max)` → 1.0; midpoint in range; `Intensity(x, 0)` → 0.
  - Band tiling: given a small set of levels, the produced bands are contiguous (each band's `Y + Height` ≈ next band's `Y`) and the union covers `[0, RH]` within a small epsilon. If asserting on `HeatBands` directly is awkward (it builds brushes), extract the tiling math into a pure helper `BuildBandRects(sortedYs, RH) -> IReadOnlyList<(double Y, double Height)>` and test that.
- **Manual smoke:** Liquidation page shows a smooth vertical heat strip — bright bands at big clusters, faint elsewhere, teal below / red above the dashed price line, no bars, no black-outlined overlap. Dashboard "Карта ликвидаций" widget shows the same. Toggling Longs/Shorts hides that side's bands.

## Files

- `CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs` — `HeatBand` record, `HeatBands` property, `Intensity` + `BuildBandRects` helpers, `RenderHeatmap` rewrite, drop bar geometries.
- `CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml` — bands ItemsControl, remove bar Paths + cluster overlay.
- `CryptoAITerminal.TerminalUI/Views/Dashboard/Widgets/LiqHeatmapWidget.axaml` — bands ItemsControl.
- `CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs` — extend with `Intensity` + `BuildBandRects` tests (the `BarWidth` tests can be removed since `BarWidth` is no longer used, or kept if `BarWidth` is left in place).
