# Customizable Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Dashboard page customizable — add/remove widgets, drag and resize them on a 12-column snap grid, with the layout persisted across restarts.

**Architecture:** Pure placement logic (`DashboardLayoutMath`) is unit-tested in Core.Tests. A `DashboardLayoutViewModel` (exposed on `MainWindowViewModel` as `DashboardLayoutVM`) holds the widget list, edit flag, catalog, and persistence. A custom `WidgetGridPanel : Panel` arranges widgets by attached Col/Row/ColSpan/RowSpan; a `WidgetHost` container provides drag/resize in edit mode; a `WidgetTemplateSelector` maps each widget Key to a per-widget `UserControl` that binds the existing `MainWindowViewModel`.

**Tech Stack:** Avalonia 12 (.NET 8), ReactiveUI, xUnit, `Services.AtomicJsonFile` for persistence.

> **Naming note:** `DashboardVM` / `DashboardViewModel` already exist (bots/PnL). This feature uses **`DashboardLayoutViewModel`** and the property **`DashboardLayoutVM`** — do NOT reuse `DashboardVM`.

---

## File Structure

- `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/WidgetPlacement.cs` — plain record `{Key,Col,Row,ColSpan,RowSpan}` (persistence + math input/output).
- `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs` — pure static logic (default layout, add placement, overlap push-down, resize clamp, sanitize-on-load). No Avalonia deps.
- `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/WidgetCatalogEntry.cs` — `{Key,Title,DefaultColSpan,DefaultRowSpan}` + the static catalog list.
- `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardWidget.cs` — ReactiveObject wrapper (Key + Col/Row/ColSpan/RowSpan reactive props + Title).
- `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutViewModel.cs` — collection, edit flag, commands, load/save.
- `CryptoAITerminal.TerminalUI/Controls/WidgetGridPanel.cs` — custom Panel + attached props.
- `CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetTemplateSelector.cs` — Key → UserControl.
- `CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetHost.axaml(.cs)` — container with header/drag/resize.
- `CryptoAITerminal.TerminalUI/Views/Dashboard/Widgets/*.axaml` — the per-widget controls.
- `CryptoAITerminal.TerminalUI/Views/DashboardView.axaml` — reworked into the host.
- `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` — wire `DashboardLayoutVM`.
- `CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs` — unit tests.

**Grid constants (single source of truth, defined in `DashboardLayoutMath`):** `Columns = 12`, `MinColSpan = 2`, `MinRowSpan = 1`.

---

## PHASE 1 — Grid engine + edit + persist + 6 existing widgets

### Task 1: WidgetPlacement record

**Files:**
- Create: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/WidgetPlacement.cs`

- [ ] **Step 1: Create the record**

```csharp
namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

/// <summary>Plain placement of one widget on the dashboard grid. Used for math and JSON persistence.</summary>
public sealed record WidgetPlacement(string Key, int Col, int Row, int ColSpan, int RowSpan);
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: `Сборка успешно завершена` / Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/WidgetPlacement.cs
git commit -m "feat(dashboard): add WidgetPlacement record"
```

---

### Task 2: DashboardLayoutMath — default layout + grid constants (TDD)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs`
- Test: `CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using CryptoAITerminal.TerminalUI.ViewModels.Dashboard;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class DashboardLayoutMathTests
{
    [Fact]
    public void DefaultLayout_fits_in_12_columns_and_has_no_overlap()
    {
        var layout = DashboardLayoutMath.DefaultLayout();

        Assert.NotEmpty(layout);
        foreach (var w in layout)
        {
            Assert.InRange(w.Col, 0, DashboardLayoutMath.Columns - 1);
            Assert.True(w.Col + w.ColSpan <= DashboardLayoutMath.Columns, $"{w.Key} exceeds 12 cols");
            Assert.True(w.ColSpan >= DashboardLayoutMath.MinColSpan);
            Assert.True(w.RowSpan >= DashboardLayoutMath.MinRowSpan);
        }
        Assert.False(DashboardLayoutMath.HasOverlap(layout));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter DashboardLayoutMathTests`
Expected: FAIL — `DashboardLayoutMath` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

/// <summary>Pure (Avalonia-free) grid placement logic for the customizable dashboard.</summary>
public static class DashboardLayoutMath
{
    public const int Columns = 12;
    public const int MinColSpan = 2;
    public const int MinRowSpan = 1;

    /// <summary>The built-in default arrangement (mirrors the classic dashboard, top to bottom).</summary>
    public static IReadOnlyList<WidgetPlacement> DefaultLayout() => new[]
    {
        new WidgetPlacement("price-stats",    0, 0, 12, 1),
        new WidgetPlacement("tracked-coins",  0, 1,  4, 4),
        new WidgetPlacement("price-chart",    4, 1,  8, 3),
        new WidgetPlacement("liq-heatmap",    4, 4,  4, 2),
        new WidgetPlacement("order-book",     8, 4,  4, 2),
        new WidgetPlacement("sentiment",      0, 6, 12, 2),
    };

    /// <summary>True if any two placements share a cell.</summary>
    public static bool HasOverlap(IReadOnlyList<WidgetPlacement> layout)
    {
        for (int i = 0; i < layout.Count; i++)
            for (int j = i + 1; j < layout.Count; j++)
                if (Overlaps(layout[i], layout[j]))
                    return true;
        return false;
    }

    private static bool Overlaps(WidgetPlacement a, WidgetPlacement b)
        => a.Col < b.Col + b.ColSpan && b.Col < a.Col + a.ColSpan
        && a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter DashboardLayoutMathTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs
git commit -m "feat(dashboard): DashboardLayoutMath default layout + overlap check"
```

---

### Task 3: DashboardLayoutMath.ClampSize (resize clamp, TDD)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs`
- Test: `CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
// col, requestedColSpan, requestedRowSpan -> expectedColSpan, expectedRowSpan
[InlineData(0, 1, 0, 2, 1)]    // below minimums -> clamped up
[InlineData(0, 99, 99, 12, 99)] // colspan capped to fit 12 cols
[InlineData(10, 5, 3, 2, 3)]    // col 10: max colspan = 2
public void ClampSize_keeps_widget_within_grid_and_minimums(
    int col, int reqCol, int reqRow, int expCol, int expRow)
{
    var (cs, rs) = DashboardLayoutMath.ClampSize(col, reqCol, reqRow);
    Assert.Equal(expCol, cs);
    Assert.Equal(expRow, rs);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter DashboardLayoutMathTests`
Expected: FAIL — `ClampSize` not defined.

- [ ] **Step 3: Write the implementation (add to DashboardLayoutMath)**

```csharp
    /// <summary>Clamp a requested span to minimums and to the 12-column right edge for the given column.</summary>
    public static (int ColSpan, int RowSpan) ClampSize(int col, int reqColSpan, int reqRowSpan)
    {
        int maxColSpan = Columns - col;
        int colSpan = System.Math.Clamp(reqColSpan, MinColSpan, maxColSpan < MinColSpan ? MinColSpan : maxColSpan);
        int rowSpan = System.Math.Max(reqRowSpan, MinRowSpan);
        return (colSpan, rowSpan);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter DashboardLayoutMathTests`
Expected: PASS (all DashboardLayoutMathTests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs
git commit -m "feat(dashboard): ClampSize for resize bounds"
```

---

### Task 4: DashboardLayoutMath.ClampPosition + ResolveDrop (drag snap + push-down, TDD)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs`
- Test: `CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void ClampPosition_keeps_widget_inside_grid()
{
    Assert.Equal(0, DashboardLayoutMath.ClampCol(-3, colSpan: 4));   // no negative
    Assert.Equal(8, DashboardLayoutMath.ClampCol(20, colSpan: 4));   // 8+4 == 12
    Assert.Equal(0, DashboardLayoutMath.ClampRow(-5));               // no negative row
}

[Fact]
public void ResolveDrop_pushes_row_down_when_target_is_occupied()
{
    var others = new[] { new WidgetPlacement("a", 0, 0, 6, 2) };
    var moved  = new WidgetPlacement("b", 0, 0, 6, 2); // dropped onto "a"

    var result = DashboardLayoutMath.ResolveDrop(moved, others);

    Assert.Equal(0, result.Col);
    Assert.Equal(2, result.Row);  // pushed below "a"
    Assert.False(DashboardLayoutMath.HasOverlap(new[] { others[0], result }));
}

[Fact]
public void ResolveDrop_leaves_free_target_untouched()
{
    var others = new[] { new WidgetPlacement("a", 0, 0, 6, 2) };
    var moved  = new WidgetPlacement("b", 6, 0, 6, 2); // free area

    var result = DashboardLayoutMath.ResolveDrop(moved, others);

    Assert.Equal(6, result.Col);
    Assert.Equal(0, result.Row);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter DashboardLayoutMathTests`
Expected: FAIL — `ClampCol`/`ClampRow`/`ResolveDrop` not defined.

- [ ] **Step 3: Write the implementation (add to DashboardLayoutMath)**

```csharp
    public static int ClampCol(int col, int colSpan)
        => System.Math.Clamp(col, 0, System.Math.Max(0, Columns - colSpan));

    public static int ClampRow(int row) => System.Math.Max(0, row);

    /// <summary>Snap a dropped widget into the grid, then push its Row down until it no longer
    /// overlaps any of <paramref name="others"/>.</summary>
    public static WidgetPlacement ResolveDrop(WidgetPlacement moved, IReadOnlyList<WidgetPlacement> others)
    {
        int col = ClampCol(moved.Col, moved.ColSpan);
        int row = ClampRow(moved.Row);
        var candidate = moved with { Col = col, Row = row };

        while (others.Any(o => Overlaps(candidate, o)))
            candidate = candidate with { Row = candidate.Row + 1 };

        return candidate;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter DashboardLayoutMathTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs
git commit -m "feat(dashboard): drag snap + push-down drop resolution"
```

---

### Task 5: DashboardLayoutMath.PlaceNew + Sanitize (add placement + load cleanup, TDD)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs`
- Test: `CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void PlaceNew_drops_widget_at_first_free_row_in_col0()
{
    var existing = new[]
    {
        new WidgetPlacement("a", 0, 0, 12, 2),
        new WidgetPlacement("b", 0, 2, 12, 1),
    };

    var placed = DashboardLayoutMath.PlaceNew("c", colSpan: 6, rowSpan: 2, existing);

    Assert.Equal("c", placed.Key);
    Assert.Equal(0, placed.Col);
    Assert.Equal(3, placed.Row); // below the row-2 widget
    Assert.False(DashboardLayoutMath.HasOverlap(new[] { existing[0], existing[1], placed }));
}

[Fact]
public void Sanitize_drops_unknown_keys_and_clamps_bad_spans()
{
    var known = new HashSet<string> { "price-chart", "order-book" };
    var loaded = new[]
    {
        new WidgetPlacement("price-chart", 0, 0, 99, 0), // bad spans -> clamped
        new WidgetPlacement("ghost",       0, 0, 4, 2),  // unknown -> dropped
    };

    var clean = DashboardLayoutMath.Sanitize(loaded, known);

    Assert.Single(clean);
    Assert.Equal("price-chart", clean[0].Key);
    Assert.True(clean[0].Col + clean[0].ColSpan <= DashboardLayoutMath.Columns);
    Assert.True(clean[0].RowSpan >= DashboardLayoutMath.MinRowSpan);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter DashboardLayoutMathTests`
Expected: FAIL — `PlaceNew`/`Sanitize` not defined.

- [ ] **Step 3: Write the implementation (add to DashboardLayoutMath; add `using System.Collections.Generic;` already present)**

```csharp
    /// <summary>Place a new widget at column 0, first row where it doesn't overlap anything.</summary>
    public static WidgetPlacement PlaceNew(string key, int colSpan, int rowSpan, IReadOnlyList<WidgetPlacement> existing)
    {
        var (cs, rs) = ClampSize(0, colSpan, rowSpan);
        var candidate = new WidgetPlacement(key, 0, 0, cs, rs);
        while (existing.Any(o => Overlaps(candidate, o)))
            candidate = candidate with { Row = candidate.Row + 1 };
        return candidate;
    }

    /// <summary>Clean a loaded layout: drop unknown keys, clamp spans/positions into the grid.</summary>
    public static IReadOnlyList<WidgetPlacement> Sanitize(
        IReadOnlyList<WidgetPlacement> loaded, ISet<string> knownKeys)
    {
        var result = new List<WidgetPlacement>();
        foreach (var w in loaded)
        {
            if (!knownKeys.Contains(w.Key)) continue;
            var (cs, rs) = ClampSize(System.Math.Clamp(w.Col, 0, Columns - 1), w.ColSpan, w.RowSpan);
            int col = ClampCol(w.Col, cs);
            int row = ClampRow(w.Row);
            result.Add(new WidgetPlacement(w.Key, col, row, cs, rs));
        }
        return result;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter DashboardLayoutMathTests`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutMath.cs CryptoAITerminal.Core.Tests/DashboardLayoutMathTests.cs
git commit -m "feat(dashboard): PlaceNew + Sanitize layout logic"
```

---

### Task 6: WidgetCatalogEntry + catalog

**Files:**
- Create: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/WidgetCatalogEntry.cs`

- [ ] **Step 1: Create the catalog**

```csharp
using System.Collections.Generic;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

public sealed record WidgetCatalogEntry(string Key, string Title, int DefaultColSpan, int DefaultRowSpan);

public static class WidgetCatalog
{
    /// <summary>All widgets available on the dashboard. Phase 1 = first six.</summary>
    public static readonly IReadOnlyList<WidgetCatalogEntry> All = new[]
    {
        new WidgetCatalogEntry("price-stats",   "Цена / Bid / Ask / Spread", 12, 1),
        new WidgetCatalogEntry("tracked-coins", "Отслеживаемые монеты",       4, 4),
        new WidgetCatalogEntry("price-chart",   "График цены",                8, 3),
        new WidgetCatalogEntry("liq-heatmap",   "Карта ликвидаций",           4, 2),
        new WidgetCatalogEntry("order-book",    "Стакан (Bids/Asks)",         4, 2),
        new WidgetCatalogEntry("sentiment",     "Настроение рынка",          12, 2),
    };

    public static WidgetCatalogEntry? Find(string key)
    {
        foreach (var e in All) if (e.Key == key) return e;
        return null;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/WidgetCatalogEntry.cs
git commit -m "feat(dashboard): widget catalog (phase 1 widgets)"
```

---

### Task 7: DashboardWidget VM

**Files:**
- Create: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardWidget.cs`

- [ ] **Step 1: Create the reactive widget VM**

```csharp
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

/// <summary>One placed widget on the dashboard grid. Col/Row/spans are reactive so the
/// WidgetGridPanel re-arranges live during drag/resize.</summary>
public sealed class DashboardWidget : ReactiveObject
{
    public string Key { get; }
    public string Title { get; }

    private int _col, _row, _colSpan, _rowSpan;

    public DashboardWidget(WidgetPlacement p, string title)
    {
        Key = p.Key;
        Title = title;
        _col = p.Col; _row = p.Row; _colSpan = p.ColSpan; _rowSpan = p.RowSpan;
    }

    public int Col     { get => _col;     set => this.RaiseAndSetIfChanged(ref _col, value); }
    public int Row     { get => _row;     set => this.RaiseAndSetIfChanged(ref _row, value); }
    public int ColSpan { get => _colSpan; set => this.RaiseAndSetIfChanged(ref _colSpan, value); }
    public int RowSpan { get => _rowSpan; set => this.RaiseAndSetIfChanged(ref _rowSpan, value); }

    public WidgetPlacement ToPlacement() => new(Key, Col, Row, ColSpan, RowSpan);
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardWidget.cs
git commit -m "feat(dashboard): DashboardWidget reactive VM"
```

---

### Task 8: DashboardLayoutViewModel (collection + edit + commands + persist)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutViewModel.cs`

- [ ] **Step 1: Create the layout VM**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

public sealed class DashboardLayoutViewModel : ReactiveObject
{
    private static readonly string LayoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "dashboard-layout.json");

    public ObservableCollection<DashboardWidget> Widgets { get; } = new();

    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set => this.RaiseAndSetIfChanged(ref _isEditMode, value);
    }

    public IReadOnlyList<WidgetCatalogEntry> Catalog => WidgetCatalog.All;

    /// <summary>Catalog entries not currently placed — what "+ Add widget" offers.</summary>
    public IEnumerable<WidgetCatalogEntry> AddableCatalog =>
        WidgetCatalog.All.Where(e => Widgets.All(w => w.Key != e.Key));

    public ReactiveCommand<Unit, Unit> ToggleEditCommand { get; }
    public ReactiveCommand<string, Unit> AddWidgetCommand { get; }
    public ReactiveCommand<DashboardWidget, Unit> RemoveWidgetCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLayoutCommand { get; }

    public DashboardLayoutViewModel()
    {
        ToggleEditCommand   = ReactiveCommand.Create(() => { IsEditMode = !IsEditMode; });
        AddWidgetCommand    = ReactiveCommand.Create<string>(AddWidget);
        RemoveWidgetCommand = ReactiveCommand.Create<DashboardWidget>(RemoveWidget);
        ResetLayoutCommand  = ReactiveCommand.Create(ResetLayout);

        Load();
    }

    private void AddWidget(string key)
    {
        if (Widgets.Any(w => w.Key == key)) return;
        var entry = WidgetCatalog.Find(key);
        if (entry is null) return;

        var placement = DashboardLayoutMath.PlaceNew(
            key, entry.DefaultColSpan, entry.DefaultRowSpan, CurrentPlacements());
        Widgets.Add(new DashboardWidget(placement, entry.Title));
        this.RaisePropertyChanged(nameof(AddableCatalog));
        Save();
    }

    private void RemoveWidget(DashboardWidget widget)
    {
        if (widget is null) return;
        Widgets.Remove(widget);
        this.RaisePropertyChanged(nameof(AddableCatalog));
        Save();
    }

    private void ResetLayout()
    {
        Widgets.Clear();
        foreach (var p in DashboardLayoutMath.DefaultLayout())
            Widgets.Add(new DashboardWidget(p, WidgetCatalog.Find(p.Key)?.Title ?? p.Key));
        this.RaisePropertyChanged(nameof(AddableCatalog));
        Save();
    }

    /// <summary>Re-snap a widget after a drag and persist. Called by WidgetHost on drop.</summary>
    public void CommitDrag(DashboardWidget widget, int newCol, int newRow)
    {
        var others = Widgets.Where(w => !ReferenceEquals(w, widget)).Select(w => w.ToPlacement()).ToList();
        var dropped = DashboardLayoutMath.ResolveDrop(
            widget.ToPlacement() with { Col = newCol, Row = newRow }, others);
        widget.Col = dropped.Col;
        widget.Row = dropped.Row;
        Save();
    }

    /// <summary>Re-clamp a widget after a resize and persist. Called by WidgetHost on resize-end.</summary>
    public void CommitResize(DashboardWidget widget, int newColSpan, int newRowSpan)
    {
        var (cs, rs) = DashboardLayoutMath.ClampSize(widget.Col, newColSpan, newRowSpan);
        widget.ColSpan = cs;
        widget.RowSpan = rs;
        Save();
    }

    private List<WidgetPlacement> CurrentPlacements() => Widgets.Select(w => w.ToPlacement()).ToList();

    private void Load()
    {
        IReadOnlyList<WidgetPlacement> placements;
        try
        {
            placements = File.Exists(LayoutPath)
                ? (AtomicJsonFile.Read<List<WidgetPlacement>>(LayoutPath) ?? new())
                : DashboardLayoutMath.DefaultLayout();
        }
        catch { placements = DashboardLayoutMath.DefaultLayout(); }

        var known = WidgetCatalog.All.Select(e => e.Key).ToHashSet();
        var clean = DashboardLayoutMath.Sanitize(placements, known);
        if (clean.Count == 0) clean = DashboardLayoutMath.DefaultLayout();

        Widgets.Clear();
        foreach (var p in clean)
            Widgets.Add(new DashboardWidget(p, WidgetCatalog.Find(p.Key)?.Title ?? p.Key));
    }

    public void Save()
    {
        try { AtomicJsonFile.Write(LayoutPath, CurrentPlacements()); }
        catch { /* best-effort */ }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/DashboardLayoutViewModel.cs
git commit -m "feat(dashboard): DashboardLayoutViewModel with persistence + commands"
```

---

### Task 9: Wire DashboardLayoutVM into MainWindowViewModel

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add the property** (near the other VM properties around line 3276, next to `DashboardVM`)

```csharp
    public ViewModels.Dashboard.DashboardLayoutViewModel DashboardLayoutVM { get; } = new();
```

(An auto-initialized property is fine — `DashboardLayoutViewModel`'s ctor only reads a JSON file and needs no constructor args. Do NOT reuse the existing `DashboardVM`.)

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat(dashboard): expose DashboardLayoutVM on MainWindowViewModel"
```

---

### Task 10: WidgetGridPanel custom panel

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Controls/WidgetGridPanel.cs`

- [ ] **Step 1: Create the panel with attached Col/Row/ColSpan/RowSpan**

```csharp
using System;
using Avalonia;
using Avalonia.Controls;

namespace CryptoAITerminal.TerminalUI.Controls;

/// <summary>Arranges children on a 12-column snap grid by attached Col/Row/ColSpan/RowSpan.
/// Cell width = panel width / 12; cell height is a fixed constant.</summary>
public class WidgetGridPanel : Panel
{
    public const int Columns = 12;
    public const double CellHeight = 84;
    public const double Gap = 10;

    public static readonly AttachedProperty<int> ColProperty =
        AvaloniaProperty.RegisterAttached<WidgetGridPanel, Control, int>("Col");
    public static readonly AttachedProperty<int> RowProperty =
        AvaloniaProperty.RegisterAttached<WidgetGridPanel, Control, int>("Row");
    public static readonly AttachedProperty<int> ColSpanProperty =
        AvaloniaProperty.RegisterAttached<WidgetGridPanel, Control, int>("ColSpan", 4);
    public static readonly AttachedProperty<int> RowSpanProperty =
        AvaloniaProperty.RegisterAttached<WidgetGridPanel, Control, int>("RowSpan", 1);

    public static void SetCol(Control c, int v) => c.SetValue(ColProperty, v);
    public static int GetCol(Control c) => c.GetValue(ColProperty);
    public static void SetRow(Control c, int v) => c.SetValue(RowProperty, v);
    public static int GetRow(Control c) => c.GetValue(RowProperty);
    public static void SetColSpan(Control c, int v) => c.SetValue(ColSpanProperty, v);
    public static int GetColSpan(Control c) => c.GetValue(ColSpanProperty);
    public static void SetRowSpan(Control c, int v) => c.SetValue(RowSpanProperty, v);
    public static int GetRowSpan(Control c) => c.GetValue(RowSpanProperty);

    static WidgetGridPanel()
    {
        AffectsParentArrange<WidgetGridPanel>(ColProperty, RowProperty, ColSpanProperty, RowSpanProperty);
        AffectsParentMeasure<WidgetGridPanel>(ColProperty, RowProperty, ColSpanProperty, RowSpanProperty);
    }

    private double CellWidth(double panelWidth) => panelWidth / Columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
        double cellW = CellWidth(width);
        int maxRowBottom = 0;

        foreach (var child in Children)
        {
            int colSpan = Math.Max(1, GetColSpan(child));
            int rowSpan = Math.Max(1, GetRowSpan(child));
            child.Measure(new Size(Math.Max(0, cellW * colSpan - Gap), Math.Max(0, CellHeight * rowSpan - Gap)));
            maxRowBottom = Math.Max(maxRowBottom, GetRow(child) + rowSpan);
        }

        return new Size(width, maxRowBottom * CellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double cellW = CellWidth(finalSize.Width);

        foreach (var child in Children)
        {
            int col = Math.Max(0, GetCol(child));
            int row = Math.Max(0, GetRow(child));
            int colSpan = Math.Max(1, GetColSpan(child));
            int rowSpan = Math.Max(1, GetRowSpan(child));

            double x = col * cellW;
            double y = row * CellHeight;
            double w = Math.Max(0, cellW * colSpan - Gap);
            double h = Math.Max(0, CellHeight * rowSpan - Gap);
            child.Arrange(new Rect(x, y, w, h));
        }

        return finalSize;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Controls/WidgetGridPanel.cs
git commit -m "feat(dashboard): WidgetGridPanel 12-col snap layout"
```

---

### Task 11: The six widget UserControls

Extract today's `DashboardView.axaml` panels into six self-contained `UserControl`s. Each has `x:DataType="vm:MainWindowViewModel"` and `x:CompileBindings="False"` (matching the current DashboardView), so existing bindings (`SelectedMarket`, `SentimentVM`, `LiquidationHeatmapVM`, `Markets`) work unchanged. Copy the XAML for each panel from the current `DashboardView.axaml` (lines noted) into the new file's root `Border`.

**Files (create each):**
- `Views/Dashboard/Widgets/PriceStatsWidget.axaml` — the 4 stat cards (current lines 84–117).
- `Views/Dashboard/Widgets/TrackedCoinsWidget.axaml` — Tracked Coins list (current lines 39–76).
- `Views/Dashboard/Widgets/PriceChartWidget.axaml` — Live Price Chart border (current lines 119–155).
- `Views/Dashboard/Widgets/LiqHeatmapWidget.axaml` — mini liq heatmap (current lines 131–149) as a standalone panel.
- `Views/Dashboard/Widgets/OrderBookWidget.axaml` — Bids + Asks grid (current lines 157–201).
- `Views/Dashboard/Widgets/SentimentWidget.axaml` — Market Sentiment border (current lines 207–301).

- [ ] **Step 1: Create each widget file** using this template (example shown for `PriceStatsWidget`; repeat for the others, pasting the matching XAML body):

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:CryptoAITerminal.TerminalUI.ViewModels"
             xmlns:ctrl="clr-namespace:CryptoAITerminal.TerminalUI.Controls"
             x:Class="CryptoAITerminal.TerminalUI.Views.Dashboard.Widgets.PriceStatsWidget"
             x:DataType="vm:MainWindowViewModel"
             x:CompileBindings="False">
  <Grid ColumnDefinitions="*,*,*,*" ColumnSpacing="16">
    <!-- paste current DashboardView.axaml lines 84-117 contents here -->
  </Grid>
</UserControl>
```

Each `.axaml` needs a code-behind `.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CryptoAITerminal.TerminalUI.Views.Dashboard.Widgets;

public partial class PriceStatsWidget : UserControl
{
    public PriceStatsWidget() => AvaloniaXamlLoader.Load(this);
}
```

(Repeat code-behind per widget with the matching class name.)

- [ ] **Step 2: Build to verify all six compile**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/Dashboard/Widgets/
git commit -m "feat(dashboard): extract six dashboard widgets into UserControls"
```

---

### Task 12: WidgetTemplateSelector

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetTemplateSelector.cs`

- [ ] **Step 1: Create the Key → control selector**

```csharp
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CryptoAITerminal.TerminalUI.ViewModels.Dashboard;
using CryptoAITerminal.TerminalUI.Views.Dashboard.Widgets;

namespace CryptoAITerminal.TerminalUI.Views.Dashboard;

/// <summary>Maps a DashboardWidget.Key to the concrete widget control.</summary>
public sealed class WidgetTemplateSelector : IDataTemplate
{
    public bool Match(object? data) => data is DashboardWidget;

    public Control? Build(object? data) => (data as DashboardWidget)?.Key switch
    {
        "price-stats"   => new PriceStatsWidget(),
        "tracked-coins" => new TrackedCoinsWidget(),
        "price-chart"   => new PriceChartWidget(),
        "liq-heatmap"   => new LiqHeatmapWidget(),
        "order-book"    => new OrderBookWidget(),
        "sentiment"     => new SentimentWidget(),
        _               => new TextBlock { Text = "Unknown widget" },
    };
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetTemplateSelector.cs
git commit -m "feat(dashboard): widget template selector"
```

---

### Task 13: WidgetHost (container with header, drag, resize)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetHost.axaml`
- Create: `CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetHost.axaml.cs`

The host renders a widget's chrome and, in edit mode, handles drag (on the ☰ handle) and resize (on the corner thumb). It finds its owning `WidgetGridPanel` ancestor to convert pixels → cells, and calls `DashboardLayoutViewModel.CommitDrag/CommitResize`.

- [ ] **Step 1: Create the AXAML**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vmd="clr-namespace:CryptoAITerminal.TerminalUI.ViewModels.Dashboard"
             xmlns:dash="clr-namespace:CryptoAITerminal.TerminalUI.Views.Dashboard"
             x:Class="CryptoAITerminal.TerminalUI.Views.Dashboard.WidgetHost"
             x:DataType="vmd:DashboardWidget"
             x:CompileBindings="False">
  <UserControl.Resources>
    <dash:WidgetTemplateSelector x:Key="WidgetSelector" />
  </UserControl.Resources>
  <Border Classes="Panel" Margin="0">
    <Grid RowDefinitions="Auto,*">
      <!-- Header: drag handle + title + remove (handle/✕ only in edit mode) -->
      <Grid Grid.Row="0" ColumnDefinitions="Auto,*,Auto" Margin="0,0,0,6">
        <TextBlock Grid.Column="0" x:Name="DragHandle" Text="☰" Margin="0,0,8,0"
                   Foreground="#8FA3B8" Cursor="SizeAll"
                   IsVisible="{Binding $parent[dash:WidgetHost].IsEditMode}" />
        <TextBlock Grid.Column="1" Text="{Binding Title}" Classes="H2" />
        <Button Grid.Column="2" Content="✕" Classes="GhostButton"
                IsVisible="{Binding $parent[dash:WidgetHost].IsEditMode}"
                Command="{Binding $parent[dash:WidgetHost].RemoveCommand}" />
      </Grid>

      <!-- Body -->
      <ContentControl Grid.Row="1" Content="{Binding}"
                      ContentTemplate="{StaticResource WidgetSelector}" />

      <!-- Resize thumb -->
      <Thumb Grid.Row="1" x:Name="ResizeThumb" Width="16" Height="16"
             HorizontalAlignment="Right" VerticalAlignment="Bottom"
             Background="#7C5CFF" Cursor="BottomRightCorner"
             IsVisible="{Binding $parent[dash:WidgetHost].IsEditMode}" />
    </Grid>
  </Border>
</UserControl>
```

- [ ] **Step 2: Create the code-behind**

```csharp
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using CryptoAITerminal.TerminalUI.Controls;
using CryptoAITerminal.TerminalUI.ViewModels;
using CryptoAITerminal.TerminalUI.ViewModels.Dashboard;
using ReactiveUI;
using System.Reactive;

namespace CryptoAITerminal.TerminalUI.Views.Dashboard;

public partial class WidgetHost : UserControl
{
    public static readonly StyledProperty<bool> IsEditModeProperty =
        AvaloniaProperty.Register<WidgetHost, bool>(nameof(IsEditMode));

    public bool IsEditMode
    {
        get => GetValue(IsEditModeProperty);
        set => SetValue(IsEditModeProperty, value);
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    private bool _dragging;
    private Point _dragStart;
    private int _startCol, _startRow;

    public WidgetHost()
    {
        AvaloniaXamlLoader.Load(this);
        RemoveCommand = ReactiveCommand.Create(RemoveSelf);

        var handle = this.FindControl<TextBlock>("DragHandle")!;
        handle.PointerPressed += OnHandlePressed;
        handle.PointerMoved += OnHandleMoved;
        handle.PointerReleased += OnHandleReleased;

        var thumb = this.FindControl<Thumb>("ResizeThumb")!;
        thumb.DragDelta += OnResizeDelta;
        thumb.DragCompleted += OnResizeCompleted;
    }

    private DashboardWidget? Widget => DataContext as DashboardWidget;
    private WidgetGridPanel? Grid => this.FindAncestorOfType<WidgetGridPanel>();
    private DashboardLayoutViewModel? Layout =>
        (this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel)?.DashboardLayoutVM;

    private void RemoveSelf()
    {
        if (Widget is { } w) Layout?.RemoveWidgetCommand.Execute(w).Subscribe();
    }

    // ---- Drag ----
    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Widget is null) return;
        _dragging = true;
        _dragStart = e.GetPosition(Grid);
        _startCol = Widget.Col;
        _startRow = Widget.Row;
        e.Pointer.Capture((IInputElement?)sender);
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Widget is null || Grid is null) return;
        var pos = e.GetPosition(Grid);
        double cellW = Grid.Bounds.Width / WidgetGridPanel.Columns;
        int dCol = (int)Math.Round((pos.X - _dragStart.X) / cellW);
        int dRow = (int)Math.Round((pos.Y - _dragStart.Y) / WidgetGridPanel.CellHeight);
        Widget.Col = Math.Clamp(_startCol + dCol, 0, WidgetGridPanel.Columns - Widget.ColSpan);
        Widget.Row = Math.Max(0, _startRow + dRow);
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || Widget is null) { _dragging = false; return; }
        _dragging = false;
        e.Pointer.Capture(null);
        Layout?.CommitDrag(Widget, Widget.Col, Widget.Row);
    }

    // ---- Resize ----
    private void OnResizeDelta(object? sender, VectorEventArgs e)
    {
        if (Widget is null || Grid is null) return;
        double cellW = Grid.Bounds.Width / WidgetGridPanel.Columns;
        int dCol = (int)Math.Round(e.Vector.X / cellW);
        int dRow = (int)Math.Round(e.Vector.Y / WidgetGridPanel.CellHeight);
        if (dCol != 0) Widget.ColSpan = Math.Max(DashboardLayoutMath.MinColSpan, Widget.ColSpan + dCol);
        if (dRow != 0) Widget.RowSpan = Math.Max(DashboardLayoutMath.MinRowSpan, Widget.RowSpan + dRow);
    }

    private void OnResizeCompleted(object? sender, VectorEventArgs e)
    {
        if (Widget is not null) Layout?.CommitResize(Widget, Widget.ColSpan, Widget.RowSpan);
    }
}
```

> Note: `Thumb.DragDelta`/`DragCompleted` use `VectorEventArgs` in Avalonia. If the IDE reports a different args type for the installed Avalonia version, match the handler signature to the event — the body stays the same.

- [ ] **Step 3: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetHost.axaml CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetHost.axaml.cs
git commit -m "feat(dashboard): WidgetHost with drag + resize in edit mode"
```

---

### Task 14: Rework DashboardView into the host

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/DashboardView.axaml`

- [ ] **Step 1: Replace the body** with the toolbar + grid host. The `ItemContainerStyle` binds the attached grid props from each `DashboardWidget`, and pushes edit mode into each `WidgetHost`.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:CryptoAITerminal.TerminalUI.ViewModels"
             xmlns:ctrl="clr-namespace:CryptoAITerminal.TerminalUI.Controls"
             xmlns:dash="clr-namespace:CryptoAITerminal.TerminalUI.Views.Dashboard"
             x:Class="CryptoAITerminal.TerminalUI.Views.DashboardView"
             x:DataType="vm:MainWindowViewModel"
             x:CompileBindings="False">
  <DockPanel LastChildFill="True">
    <!-- Toolbar -->
    <Grid DockPanel.Dock="Top" ColumnDefinitions="*,Auto,Auto,Auto" Margin="0,0,0,12">
      <TextBlock Grid.Column="0" Classes="H1" Text="Dashboard" VerticalAlignment="Center" />
      <ComboBox Grid.Column="1" Width="220" Margin="0,0,8,0"
                PlaceholderText="+ Добавить виджет"
                ItemsSource="{Binding DashboardLayoutVM.AddableCatalog}"
                IsVisible="{Binding DashboardLayoutVM.IsEditMode}"
                SelectionChanged="OnAddWidgetSelected">
        <ComboBox.ItemTemplate>
          <DataTemplate x:DataType="vm:Dashboard.WidgetCatalogEntry">
            <TextBlock Text="{Binding Title}" />
          </DataTemplate>
        </ComboBox.ItemTemplate>
      </ComboBox>
      <Button Grid.Column="2" Content="↺ Сброс" Margin="0,0,8,0"
              IsVisible="{Binding DashboardLayoutVM.IsEditMode}"
              Command="{Binding DashboardLayoutVM.ResetLayoutCommand}" />
      <Button Grid.Column="3" Content="✎ Edit"
              Command="{Binding DashboardLayoutVM.ToggleEditCommand}" />
    </Grid>

    <!-- Grid host -->
    <ScrollViewer Classes="PageScroll">
      <ItemsControl ItemsSource="{Binding DashboardLayoutVM.Widgets}">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate>
            <ctrl:WidgetGridPanel />
          </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.Styles>
          <Style Selector="ContentPresenter">
            <Setter Property="(ctrl:WidgetGridPanel.Col)"     Value="{Binding Col}" />
            <Setter Property="(ctrl:WidgetGridPanel.Row)"     Value="{Binding Row}" />
            <Setter Property="(ctrl:WidgetGridPanel.ColSpan)" Value="{Binding ColSpan}" />
            <Setter Property="(ctrl:WidgetGridPanel.RowSpan)" Value="{Binding RowSpan}" />
          </Style>
        </ItemsControl.Styles>
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:Dashboard.DashboardWidget">
            <dash:WidgetHost IsEditMode="{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).DashboardLayoutVM.IsEditMode}" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </ScrollViewer>
  </DockPanel>
</UserControl>
```

- [ ] **Step 2: Add code-behind handler for the Add-widget combo** in `DashboardView.axaml.cs` (create the file if the view had none; otherwise add the method)

```csharp
using Avalonia.Controls;
using CryptoAITerminal.TerminalUI.ViewModels;
using CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

namespace CryptoAITerminal.TerminalUI.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void OnAddWidgetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo
            && combo.SelectedItem is WidgetCatalogEntry entry
            && DataContext is MainWindowViewModel vm)
        {
            vm.DashboardLayoutVM.AddWidgetCommand.Execute(entry.Key).Subscribe();
            combo.SelectedItem = null; // reset so the same item can be re-picked later
        }
    }
}
```

> If `DashboardView.axaml.cs` already exists with a different constructor, keep the existing constructor and only add `OnAddWidgetSelected`. Add `using System;` if `.Subscribe()` needs it.

- [ ] **Step 3: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 4: Manual smoke test**

Run the app:
```bash
"./CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe"
```
Verify, on the Dashboard page:
1. Widgets render in the default arrangement.
2. Click **✎ Edit** → drag handles, ✕, resize thumbs, "+ Добавить виджет", "↺ Сброс" appear.
3. Drag a widget by ☰ → it snaps to a new cell.
4. Resize via the corner thumb → span changes, min 2×1 respected.
5. Remove a widget with ✕ → it disappears and reappears in "+ Добавить виджет".
6. Add it back from the combo.
7. Close and relaunch → layout is restored (persistence works).
8. Click **↺ Сброс** → default layout returns.

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/DashboardView.axaml CryptoAITerminal.TerminalUI/Views/DashboardView.axaml.cs
git commit -m "feat(dashboard): customizable grid host with edit mode + add/reset"
```

---

### Task 15: Phase 1 verification — full test + build

- [ ] **Step 1: Run the whole test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj`
Expected: all tests pass (411 prior + new DashboardLayoutMathTests).

- [ ] **Step 2: Confirm clean build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: 0 errors.

- [ ] **Step 3: Commit (if any fixups were needed)**

```bash
git add -A
git commit -m "test(dashboard): phase 1 green"
```

---

## PHASE 2 — Market + portfolio widgets

Phase 2 adds five compact widgets binding existing VMs, then registers them in the catalog and selector. Each follows the same shape as a Phase 1 widget.

### Task 16: Add Phase 2 widgets to catalog

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/Dashboard/WidgetCatalogEntry.cs`

- [ ] **Step 1: Append catalog entries** to `WidgetCatalog.All`:

```csharp
        new WidgetCatalogEntry("news",      "Новости",         6, 3),
        new WidgetCatalogEntry("whales",    "Whale Tracker",   6, 3),
        new WidgetCatalogEntry("funding",   "Funding Rate",    4, 2),
        new WidgetCatalogEntry("portfolio", "Портфель",        6, 2),
        new WidgetCatalogEntry("positions", "Открытые позиции",6, 3),
```

- [ ] **Step 2: Build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/Dashboard/WidgetCatalogEntry.cs
git commit -m "feat(dashboard): catalog entries for phase 2 widgets"
```

---

### Task 17: Create the five Phase 2 widget controls

**Files (create each `.axaml` + `.axaml.cs`):**
- `Views/Dashboard/Widgets/NewsWidget` — bind `NewsFeedVM` (confirm the exact VM property name on `MainWindowViewModel` before binding; grep for `NewsFeedVM`/`NewsVM`).
- `Views/Dashboard/Widgets/WhaleWidget` — bind the whale-tracker VM (grep `Whale` on MainWindowViewModel for the property name).
- `Views/Dashboard/Widgets/FundingWidget` — bind the funding-rate VM.
- `Views/Dashboard/Widgets/PortfolioWidget` — bind `PnlDashboardVM` / portfolio VM.
- `Views/Dashboard/Widgets/PositionsWidget` — bind `AllPositionsVM`.

- [ ] **Step 1: For each widget, find the source view and VM** — open the corresponding existing view (e.g. `NewsView.axaml`, `WhaleTrackerView.axaml`, `FundingRateView.axaml`, `PortfolioView.axaml`, `PositionsView.axaml`) and copy a compact subset (a list/summary, not the full page) into the widget. Use the same `UserControl` template as Task 11 (`x:DataType="vm:MainWindowViewModel"`, `x:CompileBindings="False"`).

- [ ] **Step 2: Build after each widget** to catch binding/namespace issues early.

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit each widget**

```bash
git add CryptoAITerminal.TerminalUI/Views/Dashboard/Widgets/
git commit -m "feat(dashboard): add <widget-name> widget"
```

---

### Task 18: Register Phase 2 widgets in the selector

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetTemplateSelector.cs`

- [ ] **Step 1: Add the new cases** to the `switch` in `Build`:

```csharp
        "news"      => new NewsWidget(),
        "whales"    => new WhaleWidget(),
        "funding"   => new FundingWidget(),
        "portfolio" => new PortfolioWidget(),
        "positions" => new PositionsWidget(),
```

- [ ] **Step 2: Build + manual smoke** — add each new widget from "+ Добавить виджет", confirm it renders and persists.

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Then launch and add each widget.
Expected: each renders without binding errors; layout persists on restart.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/Dashboard/WidgetTemplateSelector.cs
git commit -m "feat(dashboard): wire phase 2 widgets into selector"
```

---

## Self-Review notes (already applied)

- **Name collision:** `DashboardVM`/`DashboardViewModel` already exist; this plan uses `DashboardLayoutViewModel` / `DashboardLayoutVM` throughout.
- **Spec coverage:** layout model (Tasks 2–4,10), edit mode (8,13,14), one persisted layout (8), custom panel approach (10), catalog incl. existing+market+portfolio (6,16), default-on-missing-file + skip-unknown (5,8), drag/resize/overlap/clamp (3,4,13), tests (2–5,15).
- **Type consistency:** `CommitDrag`/`CommitResize`/`AddWidget`/`RemoveWidget`/`ResetLayout` names match between VM (Task 8) and host/view callers (13,14). `ResolveDrop`/`ClampSize`/`ClampCol`/`ClampRow`/`PlaceNew`/`Sanitize`/`HasOverlap` names match between math (2–5) and VM (8).
- **Avalonia caveat noted:** `Thumb` drag event args type may vary by Avalonia version (Task 13 note).
