# Order-Book Volume Walls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Highlight large resting limit orders ("walls") in the CEX order book and as horizontal lines on the candlestick chart, with a per-symbol, USD-or-quantity threshold the user sets above the order book.

**Architecture:** A pure detector + value types in `Core` decide which levels are "large" and how intense they are. A per-symbol settings store persists thresholds. `CexMarketItemViewModel` runs the detector on each order-book update, flags level view-models, and rebuilds a `LargeWalls` collection. The order-book rows bind to the new flags; the `CexCandlestickChart` control gains a `Walls` property that draws the lines. `MainWindowViewModel` wires the store to the selected market.

**Tech Stack:** C# / .NET 8, Avalonia 11, ReactiveUI, xUnit.

**Reference spec:** `docs/superpowers/specs/2026-06-18-order-book-volume-walls-design.md`

**Build/test commands (run from repo root `C:\Users\090\Documents\GitHub\CryptoAI`):**
- Tests: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo`
- Single test class: append `--filter "FullyQualifiedName~OrderBookWallDetectorTests"`
- Build UI: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo`
- Before any build, kill a stale running app: `try { Get-Process CryptoAITerminal.TerminalUI -ErrorAction Stop | Stop-Process -Force } catch {}`

---

## Task 1: Core value types + wall detector (pure logic)

**Files:**
- Create: `CryptoAITerminal.Core/Models/BookWall.cs`
- Create: `CryptoAITerminal.Core/OrderBookWallDetector.cs`
- Test: `CryptoAITerminal.Core.Tests/OrderBookWallDetectorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `CryptoAITerminal.Core.Tests/OrderBookWallDetectorTests.cs`:

```csharp
using CryptoAITerminal.Core;
using CryptoAITerminal.Core.Models;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class OrderBookWallDetectorTests
{
    [Theory]
    [InlineData(100, 9, 1000, false)]   // 900  < 1000
    [InlineData(100, 10, 1000, true)]   // 1000 == 1000
    [InlineData(100, 11, 1000, true)]   // 1100 > 1000
    public void Usd_compares_notional_to_threshold(double price, double qty, double thr, bool expected)
    {
        var large = OrderBookWallDetector.IsLarge((decimal)price, (decimal)qty, WallHighlightMode.Usd, (decimal)thr, 0m);
        Assert.Equal(expected, large);
    }

    [Theory]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    public void Qty_compares_quantity_to_threshold(double qty, double thr, bool expected)
    {
        var large = OrderBookWallDetector.IsLarge(100m, (decimal)qty, WallHighlightMode.Qty, 0m, (decimal)thr);
        Assert.Equal(expected, large);
    }

    [Fact]
    public void Zero_threshold_never_highlights()
    {
        Assert.False(OrderBookWallDetector.IsLarge(100m, 9999m, WallHighlightMode.Usd, 0m, 0m));
        Assert.False(OrderBookWallDetector.IsLarge(100m, 9999m, WallHighlightMode.Qty, 0m, 0m));
    }

    [Fact]
    public void Intensity_scales_and_guards_zero_max()
    {
        Assert.Equal(0d, OrderBookWallDetector.Intensity(500m, 0m));
        Assert.Equal(0.5d, OrderBookWallDetector.Intensity(500m, 1000m), 3);
        Assert.Equal(1d, OrderBookWallDetector.Intensity(2000m, 1000m), 3); // clamped
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo --filter "FullyQualifiedName~OrderBookWallDetectorTests"`
Expected: FAIL — `OrderBookWallDetector` / `WallHighlightMode` do not exist (compile error).

- [ ] **Step 3: Create the value types**

Create `CryptoAITerminal.Core/Models/BookWall.cs`:

```csharp
namespace CryptoAITerminal.Core.Models;

/// <summary>How the "large order" threshold is interpreted.</summary>
public enum WallHighlightMode
{
    Usd,
    Qty
}

/// <summary>Per-symbol highlight thresholds. A threshold of 0 means "off" for that mode.</summary>
public sealed class BookWallSettings
{
    public decimal UsdThreshold { get; set; } = 250_000m;
    public decimal QtyThreshold { get; set; } = 0m;
}

/// <summary>One large resting order projected onto the price chart.</summary>
public readonly record struct BookWall(decimal Price, bool IsBid, double Intensity, decimal Notional, decimal Quantity);
```

- [ ] **Step 4: Create the detector**

Create `CryptoAITerminal.Core/OrderBookWallDetector.cs`:

```csharp
using System;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Core;

/// <summary>Pure rules deciding which order-book levels are "walls".</summary>
public static class OrderBookWallDetector
{
    public static bool IsLarge(decimal price, decimal qty, WallHighlightMode mode, decimal usdThreshold, decimal qtyThreshold) =>
        mode switch
        {
            WallHighlightMode.Usd => usdThreshold > 0m && price * qty >= usdThreshold,
            WallHighlightMode.Qty => qtyThreshold > 0m && qty >= qtyThreshold,
            _ => false
        };

    /// <summary>Relative size 0..1 used for line thickness/opacity.</summary>
    public static double Intensity(decimal notional, decimal maxNotional)
    {
        if (maxNotional <= 0m) return 0d;
        return Math.Clamp((double)(notional / maxNotional), 0d, 1d);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo --filter "FullyQualifiedName~OrderBookWallDetectorTests"`
Expected: PASS (8 tests).

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.Core/Models/BookWall.cs CryptoAITerminal.Core/OrderBookWallDetector.cs CryptoAITerminal.Core.Tests/OrderBookWallDetectorTests.cs
git commit -m "feat: order-book wall detector + value types"
```

---

## Task 2: Per-symbol settings store (persistence)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/BookWallSettingsStore.cs`
- Test: `CryptoAITerminal.Core.Tests/BookWallSettingsStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Create `CryptoAITerminal.Core.Tests/BookWallSettingsStoreTests.cs`:

```csharp
using System;
using System.IO;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class BookWallSettingsStoreTests
{
    [Fact]
    public void Persists_mode_and_per_symbol_thresholds_across_instances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"book-walls-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BookWallSettingsStore(path);
            store.Mode = WallHighlightMode.Qty;
            store.Set("BTCUSDT", new BookWallSettings { UsdThreshold = 1_000_000m, QtyThreshold = 5m });

            var reloaded = new BookWallSettingsStore(path);
            Assert.Equal(WallHighlightMode.Qty, reloaded.Mode);
            var s = reloaded.Get("BTCUSDT");
            Assert.Equal(1_000_000m, s.UsdThreshold);
            Assert.Equal(5m, s.QtyThreshold);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Unknown_symbol_returns_default_threshold()
    {
        var path = Path.Combine(Path.GetTempPath(), $"book-walls-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BookWallSettingsStore(path);
            var s = store.Get("NEWPAIR");
            Assert.Equal(250_000m, s.UsdThreshold);
            Assert.Equal(0m, s.QtyThreshold);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo --filter "FullyQualifiedName~BookWallSettingsStoreTests"`
Expected: FAIL — `BookWallSettingsStore` does not exist.

- [ ] **Step 3: Create the store**

Create `CryptoAITerminal.TerminalUI/Services/BookWallSettingsStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Persists the global wall-highlight mode and per-symbol thresholds to
/// %LOCALAPPDATA%\CryptoAITerminal\book-walls.json. Best-effort: any IO error
/// falls back to in-memory defaults.
/// </summary>
public sealed class BookWallSettingsStore
{
    public sealed class PersistedState
    {
        public WallHighlightMode Mode { get; set; } = WallHighlightMode.Usd;
        public Dictionary<string, BookWallSettings> Symbols { get; set; } = new();
    }

    private readonly string _path;
    private readonly PersistedState _state;

    public BookWallSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal", "book-walls.json");
        _state = Load(_path);
    }

    public WallHighlightMode Mode
    {
        get => _state.Mode;
        set { _state.Mode = value; Save(); }
    }

    public BookWallSettings Get(string symbol)
    {
        if (_state.Symbols.TryGetValue(symbol, out var existing))
        {
            return existing;
        }

        var created = new BookWallSettings();
        _state.Symbols[symbol] = created;
        return created;
    }

    public void Set(string symbol, BookWallSettings settings)
    {
        _state.Symbols[symbol] = settings;
        Save();
    }

    private void Save()
    {
        try { Services.AtomicJsonFile.Write(_path, _state); }
        catch { /* best-effort persistence */ }
    }

    private static PersistedState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return Services.AtomicJsonFile.Read<PersistedState>(path) ?? new PersistedState();
            }
        }
        catch { /* fall through to defaults */ }
        return new PersistedState();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo --filter "FullyQualifiedName~BookWallSettingsStoreTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/BookWallSettingsStore.cs CryptoAITerminal.Core.Tests/BookWallSettingsStoreTests.cs
git commit -m "feat: per-symbol book-wall settings store"
```

---

## Task 3: Order-book level view-model highlight flags

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/CexMarketItemViewModel.cs:344-369` (the `OrderBookLevelViewModel` class)

No new unit test here — the behavior is exercised by Task 4's view-model test. This task is a pure additive change.

- [ ] **Step 1: Replace the `OrderBookLevelViewModel` class body**

Find the existing class (currently lines 344-369):

```csharp
public class OrderBookLevelViewModel : ReactiveObject
{
    private bool _isSelected;

    public OrderBookLevelViewModel(decimal price, decimal quantity)
    {
        Price = price;
        Quantity = quantity;
    }

    public decimal Price { get; }
    public decimal Quantity { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(PriceBrush));
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    public string PriceBrush => IsSelected ? "#F4B860" : "#F4F7FB";
    public string RowBackground => IsSelected ? "#243241" : "Transparent";
}
```

Replace it entirely with:

```csharp
public class OrderBookLevelViewModel : ReactiveObject
{
    private bool _isSelected;
    private bool _isLarge;

    public OrderBookLevelViewModel(decimal price, decimal quantity)
    {
        Price = price;
        Quantity = quantity;
    }

    public decimal Price { get; }
    public decimal Quantity { get; }
    public decimal Notional => Price * Quantity;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(PriceBrush));
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    /// <summary>True when this level is at/above the user's wall threshold.</summary>
    public bool IsLarge
    {
        get => _isLarge;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLarge, value);
            this.RaisePropertyChanged(nameof(RowBackground));
            this.RaisePropertyChanged(nameof(SizeFontWeight));
            this.RaisePropertyChanged(nameof(WallIconVisible));
        }
    }

    public string PriceBrush => IsSelected ? "#F4B860" : "#F4F7FB";

    // Selected tint wins; otherwise a warm wall tint when large.
    public string RowBackground => IsSelected ? "#243241" : IsLarge ? "#3A2E12" : "Transparent";
    public string SizeFontWeight => IsLarge ? "Bold" : "Normal";
    public bool WallIconVisible => IsLarge;
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo`
Expected: `Сборка успешно завершена` / Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/CexMarketItemViewModel.cs
git commit -m "feat: wall highlight flags on order-book level VM"
```

---

## Task 4: Detect walls on order-book updates (market view-model)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/CexMarketItemViewModel.cs` (fields ~18-24, `UpdateOrderBook` 210-227, add members + `ApplyWallHighlighting`)
- Test: `CryptoAITerminal.Core.Tests/CexMarketWallTests.cs`

- [ ] **Step 1: Write the failing test**

Create `CryptoAITerminal.Core.Tests/CexMarketWallTests.cs`:

```csharp
using System.Linq;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class CexMarketWallTests
{
    private static OrderBook Book() => new()
    {
        Symbol = "BTCUSDT",
        Bids =
        [
            new OrderBookLevel { Price = 100m, Quantity = 20m }, // 2000 notional
            new OrderBookLevel { Price = 99m,  Quantity = 1m },  // 99 notional
        ],
        Asks =
        [
            new OrderBookLevel { Price = 101m, Quantity = 15m }, // 1515 notional
        ]
    };

    [Fact]
    public void Usd_mode_flags_levels_and_builds_walls()
    {
        var vm = new CexMarketItemViewModel("BTCUSDT")
        {
            WallMode = WallHighlightMode.Usd,
            WallUsdThreshold = 1000m
        };

        vm.UpdateOrderBook(Book());

        Assert.True(vm.BidLevels[0].IsLarge);
        Assert.False(vm.BidLevels[1].IsLarge);
        Assert.True(vm.AskLevels[0].IsLarge);

        Assert.Equal(2, vm.LargeWalls.Count);
        Assert.Contains(vm.LargeWalls, w => w.IsBid && w.Price == 100m);
        Assert.Contains(vm.LargeWalls, w => !w.IsBid && w.Price == 101m);
        Assert.Equal(1, vm.BidWallCount);
        Assert.Equal(1, vm.AskWallCount);
    }

    [Fact]
    public void Zero_threshold_flags_nothing()
    {
        var vm = new CexMarketItemViewModel("BTCUSDT")
        {
            WallMode = WallHighlightMode.Usd,
            WallUsdThreshold = 0m
        };

        vm.UpdateOrderBook(Book());

        Assert.All(vm.BidLevels, l => Assert.False(l.IsLarge));
        Assert.Empty(vm.LargeWalls);
    }

    [Fact]
    public void Changing_threshold_recomputes_current_book()
    {
        var vm = new CexMarketItemViewModel("BTCUSDT") { WallMode = WallHighlightMode.Usd, WallUsdThreshold = 1000m };
        vm.UpdateOrderBook(Book());
        Assert.Equal(2, vm.LargeWalls.Count);

        vm.WallUsdThreshold = 1600m; // now only the 2000-notional bid qualifies
        Assert.Single(vm.LargeWalls);
        Assert.True(vm.BidLevels[0].IsLarge);
        Assert.False(vm.AskLevels[0].IsLarge);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo --filter "FullyQualifiedName~CexMarketWallTests"`
Expected: FAIL — `WallMode`, `WallUsdThreshold`, `LargeWalls`, `BidWallCount`, `AskWallCount` do not exist.

- [ ] **Step 3: Add the imports, backing fields, members and detection**

In `CexMarketItemViewModel.cs`, the file already has `using CryptoAITerminal.Core.Models;`. Add this using under the existing usings (after line 7):

```csharp
using CryptoAITerminal.Core;
```

Add backing fields to the existing field block (after line 24, `private bool _isFavorite;`):

```csharp
    private WallHighlightMode _wallMode = WallHighlightMode.Usd;
    private decimal _wallUsdThreshold = 250_000m;
    private decimal _wallQtyThreshold;
    private int _bidWallCount;
    private int _askWallCount;
```

Add these public members immediately after the constructor (after line 29, the closing `}` of the ctor). `BidLevels`/`AskLevels` already exist further down — do not redeclare them:

```csharp
    /// <summary>Large resting orders for the chart, rebuilt on every book update.</summary>
    public ObservableCollection<BookWall> LargeWalls { get; } = [];

    /// <summary>Invoked after the user changes mode/thresholds, so callers can persist.</summary>
    public Action? WallSettingsChanged { get; set; }

    public WallHighlightMode WallMode
    {
        get => _wallMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _wallMode, value);
            this.RaisePropertyChanged(nameof(IsWallModeUsd));
            this.RaisePropertyChanged(nameof(IsWallModeQty));
            ApplyWallHighlighting();
            WallSettingsChanged?.Invoke();
        }
    }

    public bool IsWallModeUsd => WallMode == WallHighlightMode.Usd;
    public bool IsWallModeQty => WallMode == WallHighlightMode.Qty;

    public decimal WallUsdThreshold
    {
        get => _wallUsdThreshold;
        set
        {
            this.RaiseAndSetIfChanged(ref _wallUsdThreshold, value);
            ApplyWallHighlighting();
            WallSettingsChanged?.Invoke();
        }
    }

    public decimal WallQtyThreshold
    {
        get => _wallQtyThreshold;
        set
        {
            this.RaiseAndSetIfChanged(ref _wallQtyThreshold, value);
            ApplyWallHighlighting();
            WallSettingsChanged?.Invoke();
        }
    }

    public int BidWallCount
    {
        get => _bidWallCount;
        private set => this.RaiseAndSetIfChanged(ref _bidWallCount, value);
    }

    public int AskWallCount
    {
        get => _askWallCount;
        private set => this.RaiseAndSetIfChanged(ref _askWallCount, value);
    }

    public int TotalWallCount => BidWallCount + AskWallCount;
```

- [ ] **Step 4: Call detection from `UpdateOrderBook`**

Replace the existing `UpdateOrderBook` (lines 210-227):

```csharp
    public void UpdateOrderBook(OrderBook orderBook)
    {
        ReplaceLevels(
            BidLevels,
            orderBook.Bids
                .OrderByDescending(level => level.Price)
                .Take(8)
                .Select(level => new OrderBookLevelViewModel(level.Price, level.Quantity)));

        ReplaceLevels(
            AskLevels,
            orderBook.Asks
                .OrderBy(level => level.Price)
                .Take(8)
                .Select(level => new OrderBookLevelViewModel(level.Price, level.Quantity)));

        RaiseDerivedState();
    }
```

with (only the `ApplyWallHighlighting()` call is added before `RaiseDerivedState`):

```csharp
    public void UpdateOrderBook(OrderBook orderBook)
    {
        ReplaceLevels(
            BidLevels,
            orderBook.Bids
                .OrderByDescending(level => level.Price)
                .Take(8)
                .Select(level => new OrderBookLevelViewModel(level.Price, level.Quantity)));

        ReplaceLevels(
            AskLevels,
            orderBook.Asks
                .OrderBy(level => level.Price)
                .Take(8)
                .Select(level => new OrderBookLevelViewModel(level.Price, level.Quantity)));

        ApplyWallHighlighting();
        RaiseDerivedState();
    }

    private void ApplyWallHighlighting()
    {
        decimal maxNotional = 0m;

        void Flag(ObservableCollection<OrderBookLevelViewModel> levels)
        {
            foreach (var level in levels)
            {
                level.IsLarge = OrderBookWallDetector.IsLarge(
                    level.Price, level.Quantity, WallMode, WallUsdThreshold, WallQtyThreshold);
                if (level.IsLarge && level.Notional > maxNotional)
                {
                    maxNotional = level.Notional;
                }
            }
        }

        Flag(BidLevels);
        Flag(AskLevels);

        LargeWalls.Clear();
        var bidWalls = 0;
        var askWalls = 0;

        void Collect(ObservableCollection<OrderBookLevelViewModel> levels, bool isBid)
        {
            foreach (var level in levels)
            {
                if (!level.IsLarge) continue;
                LargeWalls.Add(new BookWall(
                    level.Price, isBid,
                    OrderBookWallDetector.Intensity(level.Notional, maxNotional),
                    level.Notional, level.Quantity));
                if (isBid) bidWalls++; else askWalls++;
            }
        }

        Collect(BidLevels, true);
        Collect(AskLevels, false);

        BidWallCount = bidWalls;
        AskWallCount = askWalls;
        this.RaisePropertyChanged(nameof(TotalWallCount));
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo --filter "FullyQualifiedName~CexMarketWallTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Run the full Core test suite (no regressions)**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo`
Expected: PASS, all tests green.

- [ ] **Step 7: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/CexMarketItemViewModel.cs CryptoAITerminal.Core.Tests/CexMarketWallTests.cs
git commit -m "feat: detect order-book walls on each update"
```

---

## Task 5: Draw wall lines on the candlestick chart

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Controls/CexCandlestickChart.cs`

Rendering — verified by build + manual smoke, no unit test.

- [ ] **Step 1: Add the `Walls` styled property + collection subscription field**

In `CexCandlestickChart.cs`, add `using CryptoAITerminal.Core.Models;` to the usings (after `using CryptoAITerminal.Core.Models;` is NOT yet present — it imports `DexOhlcvPoint` from there already via line 10 `using CryptoAITerminal.Core.Models;`). It is already imported. No new using needed.

After the `ShowVolumeProfileProperty` registration (line 34-35), add:

```csharp
    public static readonly StyledProperty<IReadOnlyList<BookWall>?> WallsProperty =
        AvaloniaProperty.Register<CexCandlestickChart, IReadOnlyList<BookWall>?>(nameof(Walls));
```

Add a subscription field next to `_subscribedCollection` (after line 42):

```csharp
    private INotifyCollectionChanged? _subscribedWalls;
```

Add the CLR property next to the other properties (after the `ShowVolumeProfile` property, line 107-111):

```csharp
    public IReadOnlyList<BookWall>? Walls
    {
        get => GetValue(WallsProperty);
        set => SetValue(WallsProperty, value);
    }
```

- [ ] **Step 2: Register `Walls` for render + handle its changes**

In the static constructor (line 66-68), add `WallsProperty` to the `AffectsRender` list:

```csharp
        AffectsRender<CexCandlestickChart>(CandlesProperty, ToolModeProperty, ClearDrawingsVersionProperty, ResetViewVersionProperty, PersistenceKeyProperty, ShowVwapProperty, ShowVolumeProfileProperty, WallsProperty);
```

In `OnPropertyChanged` (after the `CandlesProperty` block, before the `ClearDrawingsVersionProperty` block, around line 124), add:

```csharp
        if (change.Property == WallsProperty)
        {
            SubscribeToWalls();
            InvalidateVisual();
            return;
        }
```

- [ ] **Step 3: Add the subscription helper (mirrors `SubscribeToCandles`)**

Immediately after the existing `SubscribeToCandles` method (ends line 436), add:

```csharp
    private void SubscribeToWalls()
    {
        if (_subscribedWalls is not null)
        {
            _subscribedWalls.CollectionChanged -= OnWallsCollectionChanged;
            _subscribedWalls = null;
        }

        if (Walls is INotifyCollectionChanged notify)
        {
            _subscribedWalls = notify;
            _subscribedWalls.CollectionChanged += OnWallsCollectionChanged;
        }
    }

    private void OnWallsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }
```

- [ ] **Step 4: Draw the walls in `Render`**

In `Render`, after the `DrawDrawings(context);` line (line 207), add:

```csharp
        DrawWalls(context);
```

Add the `DrawWalls` method after `DrawDrawings` (after line 756):

```csharp
    private void DrawWalls(DrawingContext context)
    {
        var walls = Walls;
        if (walls is null || walls.Count == 0)
        {
            return;
        }

        foreach (var wall in walls)
        {
            if (wall.Price < _minVisiblePrice || wall.Price > _maxVisiblePrice)
            {
                continue;
            }

            var y = MapY(wall.Price);
            var rgb = wall.IsBid ? Color.Parse("#42F5B1") : Color.Parse("#FF6B6B");
            var alpha = (byte)Math.Clamp(90d + (wall.Intensity * 130d), 0d, 255d);
            var lineColor = new Color(alpha, rgb.R, rgb.G, rgb.B);
            var pen = new Pen(new SolidColorBrush(lineColor), 1.5d + (wall.Intensity * 2.5d));
            context.DrawLine(pen, new Point(_chartBounds.Left, y), new Point(_chartBounds.Right, y));

            var label = new FormattedText(
                FormatWallSize(wall.Quantity),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
                10,
                new SolidColorBrush(rgb));
            context.DrawText(label, new Point(_chartBounds.Left + 6, y - 12));
        }
    }

    private static string FormatWallSize(decimal qty) =>
        qty >= 1000m
            ? (qty / 1000m).ToString("N1", CultureInfo.InvariantCulture) + "K"
            : qty.ToString("N2", CultureInfo.InvariantCulture);
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Controls/CexCandlestickChart.cs
git commit -m "feat: draw order-book wall lines on candlestick chart"
```

---

## Task 6: Threshold panel + order-book row highlight + chart binding (XAML)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml` (Bid/Ask grid ~851, both row templates ~865-919, the `CexCandlestickChart` element)

UI — verified by build + manual smoke.

- [ ] **Step 1: Add the threshold panel above the Bid/Ask grid**

Find the order-book grid opener (line 851):

```xml
            <Grid ColumnDefinitions="*,*" ColumnSpacing="10">
              <Border Grid.Column="0" Classes="TicketMetricCard,OrderBookSideCard">
```

Insert this panel immediately BEFORE that `<Grid ColumnDefinitions="*,*" ColumnSpacing="10">` line:

```xml
            <Border Classes="TicketMetricCard" Margin="0,0,0,8">
              <Grid ColumnDefinitions="Auto,Auto,Auto,*,Auto" ColumnSpacing="10">
                <TextBlock Grid.Column="0" Text="Крупные заявки" VerticalAlignment="Center"
                           Foreground="#8FA3B8" FontWeight="SemiBold" />
                <Button Grid.Column="1" Content="USD"
                        Command="{Binding SetWallModeCommand}" CommandParameter="Usd"
                        Classes="GhostButton" Padding="10,4"
                        IsEnabled="{Binding !SelectedMarket.IsWallModeUsd}" />
                <Button Grid.Column="2" Content="Объём"
                        Command="{Binding SetWallModeCommand}" CommandParameter="Qty"
                        Classes="GhostButton" Padding="10,4"
                        IsEnabled="{Binding !SelectedMarket.IsWallModeQty}" />
                <NumericUpDown Grid.Column="3"
                               Minimum="0" Increment="1000" FormatString="N0"
                               Value="{Binding SelectedMarket.WallUsdThreshold}"
                               IsVisible="{Binding SelectedMarket.IsWallModeUsd}"
                               ToolTip.Tip="Порог в USD (цена × объём)" />
                <NumericUpDown Grid.Column="3"
                               Minimum="0" Increment="1" FormatString="N4"
                               Value="{Binding SelectedMarket.WallQtyThreshold}"
                               IsVisible="{Binding SelectedMarket.IsWallModeQty}"
                               ToolTip.Tip="Порог в объёме (базовый актив)" />
                <TextBlock Grid.Column="4" VerticalAlignment="Center" Foreground="#8FA3B8"
                           Text="{Binding SelectedMarket.TotalWallCount, StringFormat=Стен: {0}}" />
              </Grid>
            </Border>
```

- [ ] **Step 2: Highlight the Bid rows**

Find the Bid row button (lines 868-880) and replace it:

```xml
                          <Button Classes="OrderBookRowButton,OrderBookBidRow"
                                  Padding="0"
                                  Command="{Binding $parent[UserControl].DataContext.SelectBidPriceCommand}"
                                  CommandParameter="{Binding Price}">
                            <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8">
                              <TextBlock Text="{Binding Price, StringFormat={}{0:N2}}"
                                         Foreground="#42F5B1"
                                         FontWeight="SemiBold" />
                              <TextBlock Grid.Column="1"
                                         Text="{Binding Quantity, StringFormat={}{0:N4}}"
                                         HorizontalAlignment="Right" />
                            </Grid>
                          </Button>
```

with (adds `Background`, the wall icon, and bold size):

```xml
                          <Button Classes="OrderBookRowButton,OrderBookBidRow"
                                  Padding="0"
                                  Background="{Binding RowBackground}"
                                  Command="{Binding $parent[UserControl].DataContext.SelectBidPriceCommand}"
                                  CommandParameter="{Binding Price}">
                            <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="6">
                              <TextBlock Grid.Column="0" Text="🟩" FontSize="10"
                                         VerticalAlignment="Center"
                                         IsVisible="{Binding WallIconVisible}" />
                              <TextBlock Grid.Column="1" Text="{Binding Price, StringFormat={}{0:N2}}"
                                         Foreground="#42F5B1"
                                         FontWeight="SemiBold" />
                              <TextBlock Grid.Column="2"
                                         Text="{Binding Quantity, StringFormat={}{0:N4}}"
                                         FontWeight="{Binding SizeFontWeight}"
                                         HorizontalAlignment="Right" />
                            </Grid>
                          </Button>
```

- [ ] **Step 3: Highlight the Ask rows**

Find the Ask row button (lines 904-916) and replace it:

```xml
                          <Button Classes="OrderBookRowButton,OrderBookAskRow"
                                  Padding="0"
                                  Command="{Binding $parent[UserControl].DataContext.SelectAskPriceCommand}"
                                  CommandParameter="{Binding Price}">
                            <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8">
                              <TextBlock Text="{Binding Price, StringFormat={}{0:N2}}"
                                         Foreground="#FF857B"
                                         FontWeight="SemiBold" />
                              <TextBlock Grid.Column="1"
                                         Text="{Binding Quantity, StringFormat={}{0:N4}}"
                                         HorizontalAlignment="Right" />
                            </Grid>
                          </Button>
```

with:

```xml
                          <Button Classes="OrderBookRowButton,OrderBookAskRow"
                                  Padding="0"
                                  Background="{Binding RowBackground}"
                                  Command="{Binding $parent[UserControl].DataContext.SelectAskPriceCommand}"
                                  CommandParameter="{Binding Price}">
                            <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="6">
                              <TextBlock Grid.Column="0" Text="🟥" FontSize="10"
                                         VerticalAlignment="Center"
                                         IsVisible="{Binding WallIconVisible}" />
                              <TextBlock Grid.Column="1" Text="{Binding Price, StringFormat={}{0:N2}}"
                                         Foreground="#FF857B"
                                         FontWeight="SemiBold" />
                              <TextBlock Grid.Column="2"
                                         Text="{Binding Quantity, StringFormat={}{0:N4}}"
                                         FontWeight="{Binding SizeFontWeight}"
                                         HorizontalAlignment="Right" />
                            </Grid>
                          </Button>
```

- [ ] **Step 4: Bind the chart's `Walls`**

Find the candlestick chart element in this file:

Run: `Grep` for `CexCandlestickChart` in `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml`.

On that `<ctrl:CexCandlestickChart ... />` element, add the attribute (keep all existing attributes):

```xml
                Walls="{Binding SelectedMarket.LargeWalls}"
```

If the control is referenced with a different xmlns prefix than `ctrl`, use whatever prefix maps to `clr-namespace:CryptoAITerminal.TerminalUI.Controls` in this file's root element.

- [ ] **Step 5: Build to verify the XAML compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo`
Expected: Build succeeded, 0 errors. (Avalonia XAML errors surface here as `AVLN####`.)

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml
git commit -m "feat: wall threshold panel, order-book row highlight, chart binding"
```

---

## Task 7: Wire the settings store into the selected market

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`

Integration — verified by build + manual smoke.

- [ ] **Step 1: Add the store field**

In `MainWindowViewModel.cs`, find the field declarations near the top of the class (search for `private readonly` fields). Add:

```csharp
    private readonly Services.BookWallSettingsStore _wallSettingsStore = new();
```

- [ ] **Step 2: Add a helper that binds a market to the store**

Add this method inside the `MainWindowViewModel` class (anywhere among the private methods):

```csharp
    private void ConfigureWallSettings(ViewModels.CexMarketItemViewModel market)
    {
        var settings = _wallSettingsStore.Get(market.Symbol);
        // Apply persisted values BEFORE wiring the callback so loading doesn't re-save.
        market.WallUsdThreshold = settings.UsdThreshold;
        market.WallQtyThreshold = settings.QtyThreshold;
        market.WallMode = _wallSettingsStore.Mode;
        market.WallSettingsChanged = () =>
        {
            _wallSettingsStore.Mode = market.WallMode;
            _wallSettingsStore.Set(market.Symbol, new Core.Models.BookWallSettings
            {
                UsdThreshold = market.WallUsdThreshold,
                QtyThreshold = market.WallQtyThreshold
            });
        };
    }
```

- [ ] **Step 3: Call the helper wherever markets are created**

Run: `Grep` for `new CexMarketItemViewModel(` in `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`.

For each construction site, capture the instance in a local and call `ConfigureWallSettings` on it before it is added to the markets collection. Example transformation:

```csharp
// before
var market = new CexMarketItemViewModel(symbol);
// ...existing setup...

// after — add this line after construction/setup, before it is used:
ConfigureWallSettings(market);
```

If a site constructs inline inside a `.Select(...)` or collection initializer, refactor it to a local variable first so `ConfigureWallSettings(market)` can be called, then add the local to the collection.

- [ ] **Step 4: Add the mode-toggle command**

Add a public command property and its backing implementation. Find where other `ReactiveCommand` properties are declared and initialized (search for `ReactiveCommand.Create`). Add the property:

```csharp
    public ReactiveCommand<string, System.Reactive.Unit> SetWallModeCommand { get; }
```

In the constructor, where other commands are initialized, add:

```csharp
        SetWallModeCommand = ReactiveCommand.Create<string>(mode =>
        {
            if (SelectedMarket is null) return;
            SelectedMarket.WallMode = string.Equals(mode, "Qty", System.StringComparison.OrdinalIgnoreCase)
                ? Core.Models.WallHighlightMode.Qty
                : Core.Models.WallHighlightMode.Usd;
        });
```

(Setting `SelectedMarket.WallMode` triggers the VM's `WallSettingsChanged`, which persists the new mode via the callback from Step 2.)

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Run the full Core test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --nologo`
Expected: PASS, all green.

- [ ] **Step 7: Manual smoke test**

```powershell
try { Get-Process CryptoAITerminal.TerminalUI -ErrorAction Stop | Stop-Process -Force } catch {}
Start-Process "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.TerminalUI\bin\Debug\net8.0-windows\win-x64\CryptoAITerminal.TerminalUI.exe"
```

Manually verify on the Trading page:
1. The "Крупные заявки" panel appears above the order book with `USD | Объём` buttons and a numeric input.
2. Setting a low USD threshold highlights large Bid/Ask rows (warm background, bold size, colored icon) and draws matching horizontal lines on the chart (green for bids below price, red for asks above).
3. Switching to `Объём` swaps the input units and re-highlights.
4. Restarting the app keeps the threshold for that symbol.

Then close the app.

- [ ] **Step 8: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: persist + apply per-symbol wall settings on selected market"
```

---

## Self-Review Notes

- **Spec coverage:** USD/Qty toggle (Tasks 1,4,6,7) · per-symbol threshold + persistence (Tasks 2,7) · order-book row highlight bg+bold+icon (Tasks 3,6) · chart wall lines green/red by side, thickness by size (Tasks 1,5) · panel above the book (Task 6) · detection on each book update (Task 4). Non-goals (alerts, deeper book, DEX) intentionally absent.
- **Type consistency:** `WallHighlightMode`, `BookWallSettings`, `BookWall(Price,IsBid,Intensity,Notional,Quantity)`, `OrderBookWallDetector.IsLarge/Intensity`, VM members `WallMode/WallUsdThreshold/WallQtyThreshold/LargeWalls/BidWallCount/AskWallCount/TotalWallCount/IsWallModeUsd/IsWallModeQty/WallSettingsChanged`, chart `WallsProperty/Walls`, command `SetWallModeCommand` — names identical across all tasks.
- **Order-book depth:** detection runs over the 8 displayed levels per side (existing `Take(8)`); deeper walls are out of scope per the spec.
