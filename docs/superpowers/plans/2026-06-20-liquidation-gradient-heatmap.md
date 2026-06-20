# Liquidation Gradient Heatmap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the bar-based liquidation chart with a gradient heat strip — contiguous full-width horizontal bands, one per price level, colored by side and shaded by magnitude.

**Architecture:** `LiquidationHeatmapViewModel.RenderHeatmap` builds an `IReadOnlyList<HeatBand>` (Y, Height, Fill brush) instead of two bar geometries. The band tiling and intensity math are pure helpers, unit-tested. `LiquidationView.axaml` and the dashboard `LiqHeatmapWidget.axaml` render the bands via a `Canvas`-panel `ItemsControl`, keeping the current-price dashed line on top.

**Tech Stack:** Avalonia 12 (.NET 8), `ImmutableSolidColorBrush`, ReactiveUI, xUnit.

> **Branch:** `feature/liq-profile` (already checked out; the gradient heatmap supersedes the just-added bar profile).
> **Current state of the two views (post-profile-commits):**
> - `LiquidationView.axaml` main canvas (≈lines 119-140): `<Path LongBarsGeometry .../>`, `<Path ShortBarsGeometry .../>`, `<Path CurrentPriceGeometry .../>`. No center line/labels (already removed). A separate price-axis label `ItemsControl` (Grid.Column 1) and a bottom legend follow.
> - `LiqHeatmapWidget.axaml` (≈lines 13-22): the two bar Paths, a STRAY `<Line StartPoint="600,0" EndPoint="600,460" .../>` (left over), and the current-price Path, inside a `Viewbox`/`Canvas 1200×460`.

---

## File Structure

- `CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs` — `HeatBand` record, `HeatBands` property, `Intensity` + `BuildBandRects` pure helpers, `RenderHeatmap` rewrite, remove bar geometries.
- `CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml` — bands ItemsControl in place of bar Paths.
- `CryptoAITerminal.TerminalUI/Views/Dashboard/Widgets/LiqHeatmapWidget.axaml` — bands ItemsControl + drop stray center line.
- `CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs` — replace BarWidth tests with `Intensity` + `BuildBandRects` tests.

---

### Task 1: Pure helpers `Intensity` + `BuildBandRects` (TDD)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs`
- Test: `CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs`

- [ ] **Step 1: Replace the test file** `CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs` with:

```csharp
using System.Collections.Generic;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class LiquidationProfileTests
{
    [Fact]
    public void Intensity_is_floor_when_zero()
        => Assert.Equal(0.06, LiquidationHeatmapViewModel.Intensity(0, 100), 3);

    [Fact]
    public void Intensity_is_one_at_max()
        => Assert.Equal(1.0, LiquidationHeatmapViewModel.Intensity(100, 100), 3);

    [Fact]
    public void Intensity_is_zero_when_max_nonpositive()
        => Assert.Equal(0.0, LiquidationHeatmapViewModel.Intensity(50, 0), 3);

    [Fact]
    public void Intensity_is_between_floor_and_one_midrange()
    {
        var v = LiquidationHeatmapViewModel.Intensity(50, 100);
        Assert.True(v > 0.06 && v < 1.0, $"expected mid value, got {v}");
    }

    [Fact]
    public void BuildBandRects_tiles_contiguously_and_covers_height()
    {
        // Three levels at canvas Y = 50, 200, 400 (top to bottom), height RH = 460.
        var ys = new List<double> { 50, 200, 400 };
        var rects = LiquidationHeatmapViewModel.BuildBandRects(ys, 460);

        Assert.Equal(3, rects.Count);
        // contiguous: each band ends where the next begins
        Assert.Equal(200, rects[0].Y + rects[0].Height, 3);
        Assert.Equal(400, rects[1].Y + rects[1].Height, 3);
        // last band reaches the bottom
        Assert.Equal(460, rects[2].Y + rects[2].Height, 3);
        // no negative heights
        foreach (var r in rects) Assert.True(r.Height >= 0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter LiquidationProfileTests`
Expected: FAIL — `Intensity` / `BuildBandRects` not defined (compile error). (The old `BarWidth` tests are gone; `BarWidth` itself may remain in the VM, unused — that's fine.)

- [ ] **Step 3: Add the helpers** to `LiquidationHeatmapViewModel` (near the other helpers; both `public static`):

```csharp
    /// <summary>Opacity for a band: floor 0.06 so tiny levels stay faintly visible, 1.0 at the max cluster.</summary>
    public static double Intensity(double mag, double max)
        => max <= 0 ? 0.0 : 0.06 + 0.94 * System.Math.Clamp(mag / max, 0.0, 1.0);

    /// <summary>Given band top-Y values sorted top→bottom, produce contiguous (Y, Height) rects that tile [0, height].</summary>
    public static IReadOnlyList<(double Y, double Height)> BuildBandRects(IReadOnlyList<double> sortedYs, double height)
    {
        var result = new List<(double, double)>(sortedYs.Count);
        for (int i = 0; i < sortedYs.Count; i++)
        {
            var top = System.Math.Clamp(sortedYs[i], 0, height);
            var bottom = i + 1 < sortedYs.Count ? System.Math.Clamp(sortedYs[i + 1], 0, height) : height;
            result.Add((top, System.Math.Max(0, bottom - top)));
        }
        return result;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter LiquidationProfileTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs
git commit -m "feat(liq): Intensity + BuildBandRects helpers for heat bands" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 2: `HeatBand` model + `HeatBands` property + `RenderHeatmap` rewrite

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs`

- [ ] **Step 1: Add the record** at the bottom of the file (after the class, same namespace) or near `PriceAxisLabel`:

```csharp
public sealed record HeatBand(double Y, double Height, Avalonia.Media.IBrush Fill);
```

- [ ] **Step 2: Add the property + backing field.** Near the existing geometry properties, add a backing field and a public list (replacing the role of the bar geometries; keep `CurrentPriceGeometry`):

```csharp
    private IReadOnlyList<HeatBand> _heatBands = [];
    public IReadOnlyList<HeatBand> HeatBands
    {
        get => _heatBands;
        private set => this.RaiseAndSetIfChanged(ref _heatBands, value);
    }
```
You may leave the `LongBarsGeometry` / `ShortBarsGeometry` properties in place (now unused) OR delete them. If you delete them, also remove their backing fields `_longBarsGeometry` (if present) and `_shortBarsGeometry` and the line that assigns them in `RenderHeatmap` — but the SAFE minimal change is to leave the properties and simply stop assigning them. Choose leaving them to reduce churn; note your choice.

- [ ] **Step 3: Rewrite the bar-drawing block in `RenderHeatmap`.** Replace the block that computes `maxL`/`maxS` and opens the two `StreamGeometry` contexts (everything from `var maxL = …` down to the assignment `LongBarsGeometry = geoL; ShortBarsGeometry = geoS;`) with band building:

```csharp
        var maxL = (double)(inRange.Where(l => l.LongLiqUsd  > 0).Select(l => l.LongLiqUsd ).DefaultIfEmpty(0m).Max());
        var maxS = (double)(inRange.Where(l => l.ShortLiqUsd > 0).Select(l => l.ShortLiqUsd).DefaultIfEmpty(0m).Max());
        var maxAll = System.Math.Max(maxL, maxS);
        if (maxAll <= 0)
        {
            HeatBands = [];
            // still draw the current-price line below; continue past the early-out by not returning here
        }

        // Sort levels top (high price) → bottom (low price); tile the canvas height contiguously.
        var ordered = inRange.OrderByDescending(l => l.Price).ToList();
        var ys = ordered.Select(l => System.Math.Clamp(
            (1.0 - ((double)l.Price - minPrice) / range) * RH, 0, RH)).ToList();
        var rects = BuildBandRects(ys, RH);

        var bands = new List<HeatBand>(ordered.Count);
        for (int i = 0; i < ordered.Count && maxAll > 0; i++)
        {
            var lvl = ordered[i];
            var isShort = (double)lvl.Price >= (double)_currentPrice;
            var mag = (double)(isShort ? lvl.ShortLiqUsd : lvl.LongLiqUsd);
            if (mag <= 0) continue;
            if (isShort && !_showShorts) continue;
            if (!isShort && !_showLongs) continue;

            var alpha = (byte)System.Math.Clamp(Intensity(mag, maxAll) * 255.0, 0, 255);
            var color = isShort
                ? Avalonia.Media.Color.FromArgb(alpha, 0xFF, 0x44, 0x44)   // red short
                : Avalonia.Media.Color.FromArgb(alpha, 0x21, 0xE6, 0xC1);  // teal long
            var brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color);
            bands.Add(new HeatBand(rects[i].Y, rects[i].Height, brush));
        }
        HeatBands = bands;
```

> Important: if `maxAll <= 0` you set `HeatBands = []` but must NOT `return` before the current-price line + labels are built. The loop guard `maxAll > 0` keeps `bands` empty in that case while letting execution fall through to the existing current-price line code. Ensure the original `LongBarsGeometry = geoL; ShortBarsGeometry = geoS;` lines are removed (or, if you kept the properties, simply don't assign them).

- [ ] **Step 4: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors. (`using System.Linq;`, `Avalonia.Media`, and `System.Collections.Generic` are already imported in this file.)

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs
git commit -m "feat(liq): build gradient heat bands in RenderHeatmap" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 3: Render bands in `LiquidationView.axaml`

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml`

- [ ] **Step 1: Replace the two bar `Path`s** inside the main heatmap `<Canvas Width="1200" Height="460" …>` (the `<Path LongBarsGeometry .../>` and `<Path ShortBarsGeometry .../>`, ≈lines 122-132) with a bands `ItemsControl`. Keep the `<Path CurrentPriceGeometry .../>` AFTER it (so the dashed line draws on top):

```xml
                  <!-- Gradient heat bands -->
                  <ItemsControl ItemsSource="{Binding LiquidationHeatmapVM.HeatBands}">
                    <ItemsControl.ItemsPanel>
                      <ItemsPanelTemplate>
                        <Canvas Width="1200" Height="460" />
                      </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate>
                      <DataTemplate x:DataType="vm:HeatBand">
                        <Rectangle Width="1200" Height="{Binding Height}" Fill="{Binding Fill}"
                                   Canvas.Top="{Binding Y}" />
                      </DataTemplate>
                    </ItemsControl.ItemTemplate>
                  </ItemsControl>
```

> The `ItemsControl` itself sits on the parent `Canvas`; give it `Canvas.Left="0" Canvas.Top="0"` if Avalonia warns about attached position (optional). The inner `Canvas` panel positions each `Rectangle` by `Canvas.Top`.

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml
git commit -m "feat(liq): render heat bands on the Liquidation page" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 4: Render bands in the dashboard widget

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/Dashboard/Widgets/LiqHeatmapWidget.axaml`

- [ ] **Step 1: Replace the widget's canvas content.** In `LiqHeatmapWidget.axaml`, inside `<Canvas Width="1200" Height="460">`, remove the two bar `<Path>`s and the stray `<Line StartPoint="600,0" EndPoint="600,460" …/>`, and add the bands `ItemsControl` before the current-price `Path`. Result:

```xml
        <Canvas Width="1200" Height="460">
          <ItemsControl ItemsSource="{Binding LiquidationHeatmapVM.HeatBands}">
            <ItemsControl.ItemsPanel>
              <ItemsPanelTemplate>
                <Canvas Width="1200" Height="460" />
              </ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
              <DataTemplate x:DataType="vm:HeatBand">
                <Rectangle Width="1200" Height="{Binding Height}" Fill="{Binding Fill}"
                           Canvas.Top="{Binding Y}" />
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
          <Path Data="{Binding LiquidationHeatmapVM.CurrentPriceGeometry}"
                Stroke="#F4B860" StrokeThickness="2" StrokeDashArray="8,4" />
        </Canvas>
```
(The `vm:` xmlns already maps to `CryptoAITerminal.TerminalUI.ViewModels`, where `HeatBand` lives.)

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/Dashboard/Widgets/LiqHeatmapWidget.axaml
git commit -m "feat(liq): heat bands in dashboard Liq-map widget" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 5: Verify — tests, build, smoke

**Files:**
- None (verification only)

- [ ] **Step 1: Full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj`
Expected: all pass (prior count adjusted: −4 BarWidth tests +5 new = net +1 vs the 422 baseline before the profile work, i.e. 423; the exact number isn't critical — 0 failures is).

- [ ] **Step 2: Clean build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: 0 errors.

- [ ] **Step 3: Manual smoke**

Launch:
```bash
cd /c/Users/090/Documents/GitHub/CryptoAI
nohup "./CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe" >/tmp/liq.log 2>&1 &
PID=$!; sleep 7; ps -p $PID >/dev/null && echo ALIVE || echo DEAD; head -30 /tmp/liq.log; kill $PID 2>/dev/null
```
Confirm on the Liquidation page:
1. A smooth vertical heat strip fills the chart — bright bands at big clusters, faint elsewhere, no discrete bars, no black outlines.
2. Teal bands below / red bands above the orange dashed current-price line.
3. Toggling Longs / Shorts hides that side's bands.
4. Price-axis labels + legend intact.
5. Dashboard "Карта ликвидаций" widget shows the same heat strip (no stray center line).

- [ ] **Step 4: Commit any fixup**

```bash
git add -A
git commit -m "test(liq): gradient heatmap green" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

## Self-Review notes (already applied)

- **Spec coverage:** `HeatBand` + `HeatBands` (Task 2); `Intensity` + band tiling helpers with tests (Task 1); `RenderHeatmap` band build incl. side/intensity/toggles + `maxAll<=0` guard (Task 2); LiquidationView bands render (Task 3); widget bands render + stray center line removed (Task 4); current-price line/labels/legend preserved (Tasks 3-4); smoke incl. toggles + widget (Task 5).
- **Placeholder scan:** none — concrete code and exact elements.
- **Consistency:** `Intensity(mag, max)` and `BuildBandRects(sortedYs, height)` defined in Task 1 are used in Task 2; `HeatBand(Y, Height, Fill)` defined in Task 2 is bound identically in Tasks 3-4 (`{Binding Height}`, `{Binding Fill}`, `Canvas.Top="{Binding Y}"`). `maxAll<=0` empties `HeatBands` without skipping the current-price line.
- **Scope:** one VM + two views + one test file — single cohesive plan.
