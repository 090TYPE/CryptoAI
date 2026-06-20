# Customizable Dashboard — Design

**Date:** 2026-06-20
**Project:** CryptoAI Terminal (Avalonia .NET 8 desktop)
**Goal:** Let the user customize the Dashboard page: add/remove widgets, drag them around, and resize them on a snap grid. Layout persists across restarts.

## Decisions (locked)

- **Layout model:** 12-column snap grid. Widgets occupy whole cells (Col/Row + ColSpan/RowSpan). Drag + resize, snap to grid.
- **Edit mode:** an "Edit" toggle button. Normal mode = locked. Edit mode = drag handles, resize corner, ✕ remove, "+ Add widget" appear.
- **One layout**, persisted to JSON. A "Reset layout" action restores the default.
- **Engine approach:** custom Avalonia `Panel` + `ItemsControl` + pointer behaviors. No third-party docking library.
- **Widget catalog:** existing-dashboard widgets + market panels + portfolio/positions widgets.

## Non-goals (YAGNI)

- Multiple named layout presets (deferred; only one layout in v1).
- Free-pixel placement / overlap.
- Auto-compaction algorithms beyond a simple no-overlap-on-drop rule.
- Per-widget settings panels.

## Architecture

### Data model

`DashboardWidget` (ReactiveObject):
```
string Key                       // catalog key, e.g. "price-chart"
int Col, Row                     // grid position, Col in 0..11
int ColSpan, RowSpan             // size in cells, min 2x1
```

`WidgetCatalogEntry`:
```
string Key
string Title
int DefaultColSpan, DefaultRowSpan
```

`DashboardLayoutViewModel`:
```
ObservableCollection<DashboardWidget> Widgets
bool IsEditMode
IReadOnlyList<WidgetCatalogEntry> Catalog
IEnumerable<WidgetCatalogEntry> AddableCatalog   // catalog minus keys already present
ReactiveCommand ToggleEditCommand
ReactiveCommand<string> AddWidgetCommand          // by Key
ReactiveCommand<DashboardWidget> RemoveWidgetCommand
ReactiveCommand ResetLayoutCommand
void Save()                                        // persist after any change
```

Exposed on `MainWindowViewModel` as `DashboardVM`. Widget content controls bind the existing `MainWindowViewModel` (x:CompileBindings=False, as the current DashboardView already does), so widgets reach `SelectedMarket`, `SentimentVM`, `NewsVM`, etc. with no new plumbing.

### Render / host

`DashboardView` becomes a host:
- Root `ItemsControl ItemsSource={Binding DashboardVM.Widgets}`.
- `ItemsControl.ItemsPanel` → `WidgetGridPanel : Panel`.
  - 12 columns. Cell width = `Bounds.Width / 12`. Cell height fixed (~84 px), tunable constant.
  - Reads attached properties `WidgetGridPanel.Col/Row/ColSpan/RowSpan` on each child (set via the item container's style from the bound `DashboardWidget`).
  - `MeasureOverride` measures each child at its cell footprint; `ArrangeOverride` positions by Col*cellW, Row*cellH, sized by span.
  - Total height = max(Row+RowSpan) * cellH.
- `ItemsControl.ItemContainerStyle`/`ItemTemplate` → `WidgetHost` (Border):
  - Header: title text + drag handle ☰ + ✕ (handle/✕ visible only in edit mode).
  - Body: `ContentControl` whose content is chosen by a `WidgetTemplateSelector : IDataTemplate` mapping `Key` → the concrete widget `UserControl`.
  - Resize thumb (corner, bottom-right), visible only in edit mode.

Attached Col/Row/ColSpan/RowSpan on the container are bound to the `DashboardWidget` via the container style/setters so the panel can read them and they update live.

### Drag + resize

Handled in `WidgetHost` (code-behind or a small behavior):
- **Drag:** pointer-press on ☰ handle → capture → track pointer delta → on release, convert pixel position to nearest Col/Row (clamp Col so Col+ColSpan ≤ 12, Row ≥ 0) → write to the `DashboardWidget` → `DashboardVM.Save()`. Optional ghost highlight of the target cell during drag.
- **Resize:** pointer-drag on the corner Thumb → compute new ColSpan/RowSpan from pixel size, snap to cells, clamp min 2×1 and Col+ColSpan ≤ 12 → write to VM → Save.
- **Overlap rule (v1):** on drop, if the target cells overlap another widget, push the dragged widget's Row down until it no longer overlaps. No global re-pack.

### Catalog + Edit mode

- "Edit" button in the dashboard header toggles `IsEditMode`. Handles/✕/resize-thumb/"+ Add widget" bind their `IsVisible` to it.
- "+ Add widget ▾": a dropdown/flyout listing `AddableCatalog` (catalog entries whose Key is not already present). Selecting one runs `AddWidgetCommand(key)`: creates a `DashboardWidget` at the first free Row (Col 0) with the catalog default span, adds to `Widgets`, Save.
- "Reset layout": clears `Widgets`, repopulates the default layout, Save.

### Persistence

- File: `%LocalAppData%\CryptoAITerminal\dashboard-layout.json` via `Services.AtomicJsonFile`.
- Shape: `List<{ Key, Col, Row, ColSpan, RowSpan }>`.
- Load in ctor; if the file is missing/corrupt, fall back to `DefaultLayout()` (mirrors today's dashboard arrangement).
- Save after every drag, resize, add, remove, and reset.
- Unknown keys in a loaded file are skipped (forward/backward compatibility when the catalog changes).

### Widget breakdown (by effort)

Split the current `DashboardView` content into individual widget `UserControl`s, each binding the same `MainWindowViewModel`:

**Phase 1 — wrap existing code:**
- `PriceChartWidget` (CexPriceChart + last-tick line)
- `OrderBookWidget` (Bids + Asks)
- `SentimentWidget` (Market Sentiment / Fear & Greed)
- `LiqHeatmapWidget` (mini liquidation heatmap)
- `TrackedCoinsWidget` (Markets list + select)
- `PriceStatsWidget` (Last / Bid / Ask / Spread cards)

Phase 1 deliverable: working customizable dashboard (grid engine + edit + persist + the 6 widgets above).

**Phase 2 — new compact widgets binding existing VMs:**
- `NewsWidget` (NewsVM), `WhaleWidget`, `FundingWidget`, `PortfolioWidget`, `PositionsWidget`.

Phase 2 deliverable: those keys added to the catalog.

## Error handling

- Corrupt/missing layout file → silently fall back to default layout (best-effort, same pattern as `custom-markets.json`).
- Unknown widget Key on load → skipped.
- Drag/resize math clamps to valid grid bounds; never lets a widget leave the 12-col area or go negative.
- Save failures swallowed (best-effort), consistent with existing `AtomicJsonFile` usage.

## Testing

- Unit tests (xUnit, Core.Tests pattern) for the layout logic that is UI-independent:
  - Default layout has no overlaps and stays within 12 columns.
  - `AddWidget` places a new widget at a free row without overlap.
  - Drop-overlap rule pushes Row down correctly.
  - Resize clamps to min 2×1 and Col+ColSpan ≤ 12.
  - Load skips unknown keys and survives a corrupt file (returns default).
- Manual smoke: launch app, toggle Edit, drag/resize/add/remove a widget, restart, confirm layout restored.

## Files (anticipated)

- `ViewModels/Dashboard/DashboardWidget.cs`
- `ViewModels/Dashboard/WidgetCatalogEntry.cs`
- `ViewModels/Dashboard/DashboardLayoutViewModel.cs`
- `Controls/WidgetGridPanel.cs` (+ attached properties)
- `Views/Dashboard/WidgetHost.axaml(.cs)` and the `WidgetTemplateSelector`
- `Views/Dashboard/Widgets/*Widget.axaml` (the per-widget controls)
- Rework `Views/DashboardView.axaml` into the host
- `MainWindowViewModel` wiring (`DashboardVM`, load/save)
