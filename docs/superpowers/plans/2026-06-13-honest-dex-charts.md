# Honest DEX Charts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the DEX chart from fabricating data: show only real candles (live OHLCV / on-chain / real ticks) or an honest empty state.

**Architecture:** Extract the real candle builder (`BucketizeSamples` + `AlignTime`) into a pure, testable `DexCandleBuilder`. Strip the synthetic-seeding/bootstrap/interpolation paths from `DexTradingViewModel` so `BuildLocalCandles` uses only real samples and `LoadChartAsync` shows an honest empty state when no real data exists. Delete the now-dead synthetic helpers.

**Tech Stack:** C# / .NET 8 / Avalonia / xUnit.

---

## File Structure

- **Create** `CryptoAITerminal.TerminalUI/Services/DexCandleBuilder.cs` — public `DexPriceSample` record + pure `Bucketize` + `AlignTime`.
- **Modify** `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs` — use the builder; drop synthetic seeding, synthetic local-candle branches, Source-4 bootstrap, and display interpolation; delete dead synthetic methods + the private `DexPriceSample`.
- **Create** `CryptoAITerminal.Core.Tests/DexCandleBuilderTests.cs`

Verified facts:
- `DexPriceSample` is currently `private sealed record DexPriceSample(DateTime TimestampUtc, decimal PriceUsd);` inside `DexTradingViewModel` (~line 2321). `_priceHistory` is `Dictionary<string, List<DexPriceSample>>`.
- `DexOhlcvPoint` is in `CryptoAITerminal.Core.Models` (public). `DexTradingViewModel` already has `using CryptoAITerminal.TerminalUI.Services;`.
- `BucketizeSamples` (~1625-1704) and `AlignTime` (~2011-2015) are private static; `AlignTime` is used only by `BucketizeSamples`.
- After the behavior changes these become unused: `SeedSyntheticHistory`, `BuildSyntheticWindow`, `BuildSyntheticAnchors`, `ExpandSyntheticSamples`, `InterpolateAnchoredPrice`, `ReverseChange`, `BuildCompoundedPrice`, `Interpolate`.

---

## Task 1: Extract `DexCandleBuilder` (pure, testable)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/DexCandleBuilder.cs`
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs`
- Test: `CryptoAITerminal.Core.Tests/DexCandleBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// CryptoAITerminal.Core.Tests/DexCandleBuilderTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DexCandleBuilderTests
{
    private static readonly DateTime T0 = new(2026, 6, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmptySamples_ReturnsEmpty()
    {
        var r = DexCandleBuilder.Bucketize(new List<DexPriceSample>(), T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5), 100);
        Assert.Empty(r);
    }

    [Fact]
    public void BucketsOhlcCorrectly_AndFillsEmptyBucketsFlat()
    {
        var samples = new List<DexPriceSample>
        {
            new(T0.AddMinutes(0), 10m),
            new(T0.AddMinutes(1), 12m),
            new(T0.AddMinutes(2), 8m),   // bucket 0 [0,5): O10 C8 H12 L8
            new(T0.AddMinutes(6), 9m),   // bucket 1 [5,10): O9 C9
        };

        var r = DexCandleBuilder.Bucketize(samples, T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5), 100);

        Assert.Equal(3, r.Count); // buckets at 0,5,10
        Assert.Equal(10m, r[0].Open);
        Assert.Equal(8m,  r[0].Close);
        Assert.Equal(12m, r[0].High);
        Assert.Equal(8m,  r[0].Low);
        Assert.Equal(9m,  r[1].Open);
        Assert.Equal(9m,  r[1].Close);
        // bucket 2 has no samples → flat at last close (9)
        Assert.Equal(9m,  r[2].Open);
        Assert.Equal(9m,  r[2].Close);
    }

    [Fact]
    public void MaxCandles_LimitsCount()
    {
        var samples = new List<DexPriceSample>
        {
            new(T0.AddMinutes(0), 10m),
            new(T0.AddMinutes(6), 11m),
            new(T0.AddMinutes(12), 12m),
        };

        var r = DexCandleBuilder.Bucketize(samples, T0, T0.AddMinutes(20), TimeSpan.FromMinutes(5), 2);

        Assert.Equal(2, r.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexCandleBuilderTests`
Expected: FAIL — `DexCandleBuilder` / `DexPriceSample` do not exist in `Services` (compile error).

- [ ] **Step 3: Create `DexCandleBuilder.cs`**

```csharp
// CryptoAITerminal.TerminalUI/Services/DexCandleBuilder.cs
using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>A single observed price tick for a DEX pair.</summary>
public sealed record DexPriceSample(DateTime TimestampUtc, decimal PriceUsd);

/// <summary>
/// Builds OHLCV candles from raw price samples by time-bucketing. Empty buckets
/// repeat the previous close (flat candle). Pure and synthetic-free.
/// </summary>
public static class DexCandleBuilder
{
    public static IReadOnlyList<DexOhlcvPoint> Bucketize(
        IReadOnlyList<DexPriceSample> samples,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan bucketSize,
        int maxCandles)
    {
        if (samples.Count == 0)
        {
            return Array.Empty<DexOhlcvPoint>();
        }

        var ordered = samples
            .Where(sample => sample.PriceUsd > 0)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        if (ordered.Count == 0)
        {
            return Array.Empty<DexOhlcvPoint>();
        }

        var candles = new List<DexOhlcvPoint>();
        var bucketStart = AlignTime(fromUtc, bucketSize);
        var cursor = 0;
        var lastClose = ordered[0].PriceUsd;

        while (bucketStart <= toUtc && candles.Count < maxCandles)
        {
            var bucketEnd = bucketStart + bucketSize;
            var bucketSamples = new List<DexPriceSample>();

            while (cursor < ordered.Count && ordered[cursor].TimestampUtc < bucketEnd)
            {
                if (ordered[cursor].TimestampUtc >= bucketStart)
                {
                    bucketSamples.Add(ordered[cursor]);
                }

                cursor++;
            }

            if (bucketSamples.Count == 0)
            {
                candles.Add(new DexOhlcvPoint
                {
                    Timestamp = bucketStart.ToLocalTime(),
                    Open = lastClose,
                    High = lastClose,
                    Low = lastClose,
                    Close = lastClose,
                    Volume = 0
                });
            }
            else
            {
                var open = bucketSamples.First().PriceUsd;
                var close = bucketSamples.Last().PriceUsd;
                var high = bucketSamples.Max(sample => sample.PriceUsd);
                var low = bucketSamples.Min(sample => sample.PriceUsd);
                lastClose = close;

                candles.Add(new DexOhlcvPoint
                {
                    Timestamp = bucketStart.ToLocalTime(),
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = 0
                });
            }

            bucketStart = bucketEnd;
        }

        return candles.Count > maxCandles
            ? candles[^maxCandles..]
            : candles;
    }

    private static DateTime AlignTime(DateTime timestampUtc, TimeSpan bucketSize)
    {
        var ticks = bucketSize.Ticks == 0 ? 1 : bucketSize.Ticks;
        return new DateTime(timestampUtc.Ticks - (timestampUtc.Ticks % ticks), DateTimeKind.Utc);
    }
}
```

- [ ] **Step 4: Point the view-model at the builder**

In `DexTradingViewModel.cs`:

(a) Delete the private record near the bottom of the class:
```csharp
    private sealed record DexPriceSample(DateTime TimestampUtc, decimal PriceUsd);
```

(b) Delete the whole private `BucketizeSamples` method (signature `private static IReadOnlyList<DexOhlcvPoint> BucketizeSamples(IReadOnlyList<DexPriceSample> samples, DateTime fromUtc, DateTime toUtc, TimeSpan bucketSize, int maxCandles)`).

(c) Delete the whole private `AlignTime` method (signature `private static DateTime AlignTime(DateTime timestampUtc, TimeSpan bucketSize)`).

(d) In `BuildLocalCandles`, change the call:
```csharp
        var candles = BucketizeSamples(samples, fromUtc, nowUtc, request.BucketSize, request.MaxCandles);
```
to:
```csharp
        var candles = DexCandleBuilder.Bucketize(samples, fromUtc, nowUtc, request.BucketSize, request.MaxCandles);
```

(`DexPriceSample` now resolves to `CryptoAITerminal.TerminalUI.Services.DexPriceSample` through the existing `using`.)

- [ ] **Step 5: Run tests + build to verify**

Make sure the app is not running: `Get-Process | Where-Object { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -ErrorAction SilentlyContinue`

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexCandleBuilderTests`
Expected: PASS (3 tests).

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug -v minimal`
Expected: `Ошибок: 0`.

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/DexCandleBuilder.cs CryptoAITerminal.Core.Tests/DexCandleBuilderTests.cs CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs
git commit -m "refactor: extract pure DexCandleBuilder from DexTradingViewModel"
```

---

## Task 2: Make the chart pipeline honest

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs`

- [ ] **Step 1: Stop seeding synthetic history**

In `RecordPriceSample`, delete this block:
```csharp
        if (history.Count == 0)
        {
            SeedSyntheticHistory(history, token, sampleTimeUtc);
        }
```

- [ ] **Step 2: Real-only `BuildLocalCandles`**

Replace the body from `var samples = history...` through the synthetic branches with real-only logic. Replace this region:

```csharp
        var samples = history
            .Where(sample => sample.TimestampUtc >= fromUtc)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        if (samples.Count < 2)
        {
            samples = BuildSyntheticWindow(token, request, nowUtc);
            diagnostics = "Chart was bootstrapped from current pair stats because live local history is still short.";
        }
        else if (samples[0].TimestampUtc > fromUtc)
        {
            var syntheticPrefix = BuildSyntheticWindow(token, request, nowUtc)
                .Where(sample => sample.TimestampUtc < samples[0].TimestampUtc)
                .ToList();

            if (syntheticPrefix.Count > 0)
            {
                samples.InsertRange(0, syntheticPrefix);
                diagnostics = "Chart mixes sampled prices with a synthetic backfill based on the pair's 5m/1h/24h changes.";
            }
        }
        else
        {
            diagnostics = $"Chart built from {samples.Count} locally collected price samples.";
        }

        var candles = DexCandleBuilder.Bucketize(samples, fromUtc, nowUtc, request.BucketSize, request.MaxCandles);
```

with:

```csharp
        var samples = history
            .Where(sample => sample.TimestampUtc >= fromUtc)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        if (samples.Count < 2)
        {
            diagnostics = "Local samples are still too sparse to form candles.";
            return Array.Empty<DexOhlcvPoint>();
        }

        diagnostics = $"Chart built from {samples.Count} locally collected price samples.";
        var candles = DexCandleBuilder.Bucketize(samples, fromUtc, nowUtc, request.BucketSize, request.MaxCandles);
```

- [ ] **Step 3: Honest empty state instead of Source 4**

In `LoadChartAsync`, replace the Source-4 `else` block:

```csharp
                    else
                    {
                        // ── Source 4: synthetic bootstrap ─────────────────────
                        candles = BuildLocalCandles(selectedToken.TokenInfo, chartRange, out diagnostics);
                        candles = NormalizeDexCandlesForDisplay(candles, selectedToken.TokenInfo, chartRange);
                        sourceLabel = "Synthetic bootstrap";
                        statusLabel = $"Synthetic chart (no live data yet). On-chain scan running...";

                        // Kick off background on-chain scan if not already running
                        TriggerBackgroundOnChainScan(selectedToken.TokenInfo);
                    }
```

with:

```csharp
                    else
                    {
                        // ── No real data available: honest empty state ───────
                        ClearChart(
                            "No live chart data for this pair yet — collecting live ticks; on-chain scan running.",
                            "No live OHLCV, on-chain history or local samples available yet.");
                        TriggerBackgroundOnChainScan(selectedToken.TokenInfo);
                        return;
                    }
```

- [ ] **Step 4: Remove display interpolation**

Replace the entire `NormalizeDexCandlesForDisplay` method body with a filter + order pass (no synthetic gap-fill). Replace the whole method:

```csharp
    private static IReadOnlyList<DexOhlcvPoint> NormalizeDexCandlesForDisplay(
        IReadOnlyList<DexOhlcvPoint> candles,
        DexTokenInfo token,
        string range)
    {
        return candles
            .Where(static candle => candle.Open > 0 && candle.High > 0 && candle.Low > 0 && candle.Close > 0)
            .OrderBy(candle => candle.Timestamp)
            .ToList();
    }
```

(Keep the `token`/`range` parameters so the call sites are unchanged.)

- [ ] **Step 5: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug -v minimal`
Expected: `Ошибок: 0` (synthetic helpers are now unused but still present — compiles).

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs
git commit -m "feat: honest DEX charts — real data only, empty state otherwise"
```

---

## Task 3: Delete dead synthetic code

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs`

- [ ] **Step 1: Delete the now-unused synthetic methods**

Delete each of these whole methods (find by signature):
- `private static void SeedSyntheticHistory(List<DexPriceSample> history, DexTokenInfo token, DateTime nowUtc)`
- `private static List<DexPriceSample> BuildSyntheticWindow(...)`
- `private static List<DexPriceSample> BuildSyntheticAnchors(DexTokenInfo token, DateTime nowUtc)`
- `private static List<DexPriceSample> ExpandSyntheticSamples(...)`
- `private static decimal InterpolateAnchoredPrice(IReadOnlyList<DexPriceSample> anchors, DateTime timestampUtc)`
- `private static decimal ReverseChange(decimal currentPrice, decimal percentChange)`
- `private static decimal BuildCompoundedPrice(decimal startPrice, decimal dailyPercentChange, int extraDays)`
- `private static decimal Interpolate(decimal start, decimal end, decimal factor)`

- [ ] **Step 2: Build — the compiler confirms nothing references them**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug -v minimal`
Expected: `Ошибок: 0`. (If a deleted method is still referenced, the build fails naming it — restore that one method and investigate; otherwise the deletions are safe.)

- [ ] **Step 3: Confirm no leftover references**

Run: `git grep -n "SeedSyntheticHistory\|BuildSyntheticWindow\|BuildSyntheticAnchors\|ExpandSyntheticSamples\|InterpolateAnchoredPrice\|BuildCompoundedPrice" -- "*.cs"`
Expected: no matches.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs
git commit -m "chore: remove dead synthetic DEX chart generators"
```

---

## Task 4: Full verification

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests`
Expected: PASS — all prior tests plus the 3 new `DexCandleBuilderTests`.

- [ ] **Step 2: Smoke-run the app**

Launch `CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe`.
Manual check (demo, no keys):
1. Trading page → Venue = **DEX**.
2. Select an established token (good liquidity) → a **real** candlestick chart renders (varied candles), source shows live OHLCV.
3. Select two different tokens → their charts **differ** (no identical synthetic ramp).
4. Select a brand-new / illiquid token with no data → the chart shows the honest **"No live chart data for this pair yet…"** message instead of a fabricated ramp.

- [ ] **Step 3: Final commit (if smoke fixes were needed)**

```bash
git add -A
git commit -m "chore: finalize honest DEX charts"
```

---

## Self-Review (completed during authoring)

- **Spec coverage:** (1) remove synthetic seeding → Task 2 Step 1; (2) real-only BuildLocalCandles → Task 2 Step 2; (3) extract DexCandleBuilder → Task 1; (4) honest empty Source 4 → Task 2 Step 3; (5) no display interpolation → Task 2 Step 4; (6) delete dead code → Task 3; tests → Task 1 + Task 4. Covered.
- **Placeholder scan:** none — every code step has full code; deletion steps identify methods by exact signature.
- **Type consistency:** `DexCandleBuilder.Bucketize(IReadOnlyList<DexPriceSample>, DateTime, DateTime, TimeSpan, int)` used identically in the test, the impl, and the `BuildLocalCandles` call site; `DexPriceSample` is the single public record in `Services` after Task 1 removes the private one. `NormalizeDexCandlesForDisplay` keeps its `(candles, token, range)` signature so call sites are unchanged.
