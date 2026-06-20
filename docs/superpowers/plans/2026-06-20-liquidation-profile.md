# Liquidation Profile Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the center-diverging liquidation bar chart into a clean left-anchored "liquidation profile": all bars grow rightward from `X=0`, positioned vertically by price, colored by side (teal long / red short).

**Architecture:** Rewrite the bar geometry in `LiquidationHeatmapViewModel.RenderHeatmap` to draw both long and short bars from a shared left baseline using a shared magnitude scale (extracted into a pure `BarWidth` helper that is unit-tested). Remove the now-meaningless center divider and "LONGS/SHORTS" overlay labels from `LiquidationView.axaml`. The dashboard mini widget renders the same geometries and updates automatically.

**Tech Stack:** Avalonia 12 (.NET 8), `StreamGeometry`, ReactiveUI, xUnit.

> **Branch:** `feature/liq-profile` (already checked out).

---

## File Structure

- `CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs` — `BarWidth` helper (new, pure) + `RenderHeatmap` bar drawing rewrite + constants.
- `CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml` — remove center divider line + the two `◀ LONGS` / `SHORTS ▶` overlay labels.
- `CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs` — `BarWidth` unit tests.

### Relevant current code (LiquidationHeatmapViewModel.cs)
Constants (lines ~21-27):
```csharp
    private const double RW        = 1200.0; // canvas width
    private const double Cx        =  600.0; // center X (longs left, shorts right)
    private const double MaxHalf   =  590.0; // max half-bar width
    private const double BarH      =   16.0; // bar height (thicker = more visible)
    private const double RH        =  460.0; // canvas height
    private const double RangeF    =   0.20; // ±20 % price range
    private const double LabelStep =  0.025; // axis label every 2.5 %
```
Current bar drawing inside `RenderHeatmap` (lines ~325-360):
```csharp
        var maxL = (double)(inRange.Where(l => l.LongLiqUsd  > 0).Select(l => l.LongLiqUsd ).DefaultIfEmpty(1m).Max());
        var maxS = (double)(inRange.Where(l => l.ShortLiqUsd > 0).Select(l => l.ShortLiqUsd).DefaultIfEmpty(1m).Max());

        var geoL = new StreamGeometry();
        var geoS = new StreamGeometry();

        using (var ctxL = geoL.Open())
        using (var ctxS = geoS.Open())
        {
            foreach (var lvl in inRange)
            {
                var y  = (1.0 - ((double)lvl.Price - minPrice) / range) * RH;
                var y2 = y + BarH;

                if (_showLongs && lvl.LongLiqUsd > 0)
                {
                    var w = (double)lvl.LongLiqUsd / maxL * MaxHalf;
                    ctxL.BeginFigure(new Point(Cx,     y),  isFilled: true);
                    ctxL.LineTo(new Point(Cx - w, y));
                    ctxL.LineTo(new Point(Cx - w, y2));
                    ctxL.LineTo(new Point(Cx,     y2));
                    ctxL.EndFigure(true);
                }

                if (_showShorts && lvl.ShortLiqUsd > 0)
                {
                    var w = (double)lvl.ShortLiqUsd / maxS * MaxHalf;
                    ctxS.BeginFigure(new Point(Cx,     y),  isFilled: true);
                    ctxS.LineTo(new Point(Cx + w, y));
                    ctxS.LineTo(new Point(Cx + w, y2));
                    ctxS.LineTo(new Point(Cx,     y2));
                    ctxS.EndFigure(true);
                }
            }
        }
```

---

### Task 1: `BarWidth` pure helper (TDD)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs`
- Test: `CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class LiquidationProfileTests
{
    [Fact]
    public void BarWidth_is_zero_when_usd_is_zero()
        => Assert.Equal(0.0, LiquidationHeatmapViewModel.BarWidth(0, maxUsd: 100, usableWidth: 1000), 3);

    [Fact]
    public void BarWidth_is_full_usable_width_at_max()
        => Assert.Equal(1000.0, LiquidationHeatmapViewModel.BarWidth(100, maxUsd: 100, usableWidth: 1000), 3);

    [Fact]
    public void BarWidth_is_half_at_half_max()
        => Assert.Equal(500.0, LiquidationHeatmapViewModel.BarWidth(50, maxUsd: 100, usableWidth: 1000), 3);

    [Fact]
    public void BarWidth_is_zero_when_max_is_nonpositive()
        => Assert.Equal(0.0, LiquidationHeatmapViewModel.BarWidth(50, maxUsd: 0, usableWidth: 1000), 3);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter LiquidationProfileTests`
Expected: FAIL — `BarWidth` not defined (compile error).

- [ ] **Step 3: Add the helper** to `LiquidationHeatmapViewModel` (place it near the other private helpers; make it `public static` so the test can call it):

```csharp
    /// <summary>Bar length for a liquidation level: proportional to USD, clamped to the usable width.</summary>
    public static double BarWidth(double usd, double maxUsd, double usableWidth)
        => maxUsd <= 0 ? 0.0 : System.Math.Clamp(usd / maxUsd, 0.0, 1.0) * usableWidth;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter LiquidationProfileTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs CryptoAITerminal.Core.Tests/LiquidationProfileTests.cs
git commit -m "feat(liq): BarWidth helper for left-anchored profile" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 2: Rewrite the bar geometry to a left-anchored profile

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs`

- [ ] **Step 1: Add the usable-width constant.** In the constants block (lines ~21-27), after the `BarH` line, add:

```csharp
    private const double RightPad = 24.0;          // keep the longest bar off the right edge
    private const double UsableW  = RW - RightPad;  // bars span 0 … UsableW
```
Bump bar height for readability — change `BarH` from `16.0` to `18.0`:
```csharp
    private const double BarH      =   18.0; // bar height (thicker = more visible)
```
(Leave `Cx` and `MaxHalf` declared even if now unused, to avoid touching unrelated code; or delete them if the build warns as error — it won't, unused private consts only warn.)

- [ ] **Step 2: Replace the bar-drawing block** (lines ~325-360, from the `var maxL = …` line through the closing `}` of the `using` block) with the shared-scale, left-anchored version:

```csharp
        var maxL = (double)(inRange.Where(l => l.LongLiqUsd  > 0).Select(l => l.LongLiqUsd ).DefaultIfEmpty(0m).Max());
        var maxS = (double)(inRange.Where(l => l.ShortLiqUsd > 0).Select(l => l.ShortLiqUsd).DefaultIfEmpty(0m).Max());
        var maxAll = System.Math.Max(maxL, maxS);
        if (maxAll <= 0)
        {
            LongBarsGeometry  = new StreamGeometry();
            ShortBarsGeometry = new StreamGeometry();
            return;
        }

        var geoL = new StreamGeometry();
        var geoS = new StreamGeometry();

        using (var ctxL = geoL.Open())
        using (var ctxS = geoS.Open())
        {
            foreach (var lvl in inRange)
            {
                // Y: higher price → smaller Y (top = maxPrice, bottom = minPrice)
                var y  = (1.0 - ((double)lvl.Price - minPrice) / range) * RH;
                var y2 = y + BarH;

                if (_showLongs && lvl.LongLiqUsd > 0)
                {
                    var w = BarWidth((double)lvl.LongLiqUsd, maxAll, UsableW);
                    ctxL.BeginFigure(new Point(0, y),  isFilled: true);
                    ctxL.LineTo(new Point(w, y));
                    ctxL.LineTo(new Point(w, y2));
                    ctxL.LineTo(new Point(0, y2));
                    ctxL.EndFigure(true);
                }

                if (_showShorts && lvl.ShortLiqUsd > 0)
                {
                    var w = BarWidth((double)lvl.ShortLiqUsd, maxAll, UsableW);
                    ctxS.BeginFigure(new Point(0, y),  isFilled: true);
                    ctxS.LineTo(new Point(w, y));
                    ctxS.LineTo(new Point(w, y2));
                    ctxS.LineTo(new Point(0, y2));
                    ctxS.EndFigure(true);
                }
            }
        }
```

(The lines after this block — `LongBarsGeometry = geoL; ShortBarsGeometry = geoS;` and the current-price line + price-axis label code — stay exactly as they are.)

- [ ] **Step 3: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/LiquidationHeatmapViewModel.cs
git commit -m "feat(liq): left-anchored shared-scale liquidation profile" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 3: Remove center-split decorations from LiquidationView

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml`

- [ ] **Step 1: Delete the centre divider line.** Remove this element (around line 134-137):

```xml
                  <!-- Centre divider at X=600 -->
                  <Line StartPoint="600,0" EndPoint="600,460"
                        Stroke="..." StrokeThickness="..." />
```
(Delete the comment line and the `<Line …/>` element. Match on `StartPoint="600,0" EndPoint="600,460"` to find it; remove its full element regardless of the exact Stroke attributes.)

- [ ] **Step 2: Delete the two overlay labels** (around lines 145-148):

```xml
                  <TextBlock Canvas.Left="4"   Canvas.Top="4"
                             Text="◀  LONGS" Foreground="#6021E6C1" FontSize="14" FontWeight="Bold" />
                  <TextBlock Canvas.Left="610" Canvas.Top="4"
                             Text="SHORTS  ▶" Foreground="#60FF4444" FontSize="14" FontWeight="Bold" />
```
Remove both `<TextBlock>`s. Leave the long/short bar Paths, the current-price Path, the price-axis label Canvas, and the bottom legend untouched.

- [ ] **Step 3: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml
git commit -m "feat(liq): drop center divider + LONGS/SHORTS labels" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 4: Verify — tests, build, smoke

**Files:**
- None (verification only)

- [ ] **Step 1: Full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj`
Expected: all pass (prior count + 4 new LiquidationProfileTests).

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
Open the Liquidation page and confirm:
1. Bars form a single left-anchored profile — every bar starts at the left edge, grows rightward.
2. No empty top-left / bottom-right quadrants, no center vertical line, no `◀ LONGS` / `SHORTS ▶` overlay text.
3. Teal = longs (below price), red = shorts (above price); the orange dashed current-price line spans the full width; price-axis labels still on the side.
4. Open the Dashboard "Карта ликвидаций" widget → same left-anchored look.

- [ ] **Step 4: Commit (only if a fixup was needed)**

```bash
git add -A
git commit -m "test(liq): liquidation profile green" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

## Self-Review notes (already applied)

- **Spec coverage:** shared-scale left-anchored bars + maxAll guard (Task 2); `BarWidth` pure helper + tests (Task 1); remove center divider + LONGS/SHORTS labels (Task 3); dashboard widget auto-updates (no task needed — same geometries); verify + smoke (Task 4).
- **Placeholder scan:** none — exact code and exact elements to delete.
- **Consistency:** `BarWidth(usd, maxUsd, usableWidth)` signature defined in Task 1 is called identically in Task 2 with `UsableW`. `UsableW = RW - RightPad` constant introduced in Task 2 Step 1 before use. `maxAll` guard prevents divide-by-zero (spec error-handling).
- **Scope:** one VM + one view + one test file — single cohesive plan.
