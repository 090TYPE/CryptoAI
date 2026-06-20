# Trading Spot-Exchange Selector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Trading desk load the order book / price and place spot orders on a chosen CEX (Binance/Bybit/OKX/KuCoin) instead of always Binance, mirroring the existing multi-exchange futures pattern.

**Architecture:** Add a spot gateway map + `SelectedSpotExchange` + `ActiveSpotGateway` to `MainWindowViewModel` (parallel to `_futuresGatewaysMap`/`ActiveFuturesGateway`). Route the order-book refresh, the `ActiveCexGateway`, and spot order placement through `ActiveSpotGateway`. Gate spot Buy/Sell on the selected exchange's API credentials via the single existing chokepoint `GetCexExecutionGuardReason`. Add a dropdown + key-status label to `TradingDeskView`. A tiny pure resolver is unit-tested.

**Tech Stack:** Avalonia 12 (.NET 8), ReactiveUI, xUnit, existing `IExchangeGateway` gateways.

> **Branch:** work happens on `feature/spot-exchange-selector` (already checked out).
> **Naming note:** mirror the futures members exactly — field `_selectedSpotExchange`, property `SelectedSpotExchange`, getter `ActiveSpotGateway`, options `SpotExchangeOptions`, readiness `IsSpotPrivateApiReady`, status `SpotPrivateApiStatusLabel`/`SpotPrivateApiStatusBrush`, visibility `IsManualSpotMode`.

---

## File Structure

- `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` — spot map + selection + active getter + resolver + routing + gating + status props (the whole feature except UI + test).
- `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml` — spot exchange ComboBox + status label.
- `CryptoAITerminal.Core.Tests/SpotExchangeResolveTests.cs` — unit tests for the pure resolver.

---

### Task 1: Pure gateway resolver (TDD)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`
- Test: `CryptoAITerminal.Core.Tests/SpotExchangeResolveTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class SpotExchangeResolveTests
{
    private sealed class StubGateway : IExchangeGateway
    {
        public string Name { get; init; } = "";
        public System.IObservable<MarketData> MarketDataStream => System.Reactive.Linq.Observable.Empty<MarketData>();
        public System.Threading.Tasks.Task ConnectAsync() => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task DisconnectAsync() => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task<Order> PlaceOrderAsync(Order order) => System.Threading.Tasks.Task.FromResult(order);
        public System.Threading.Tasks.Task CancelOrderAsync(string orderId) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task<decimal> GetBalanceAsync(string asset) => System.Threading.Tasks.Task.FromResult(0m);
        public System.Threading.Tasks.Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => System.Threading.Tasks.Task.FromResult(new OrderBook());
    }

    [Fact]
    public void ResolveGateway_returns_mapped_gateway_case_insensitively()
    {
        var binance = new StubGateway { Name = "Binance" };
        var bybit   = new StubGateway { Name = "Bybit" };
        var map = new Dictionary<string, IExchangeGateway>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Binance"] = binance,
            ["Bybit"]   = bybit,
        };

        Assert.Same(bybit, MainWindowViewModel.ResolveGateway(map, "bybit", binance));
    }

    [Fact]
    public void ResolveGateway_falls_back_when_key_missing_or_empty()
    {
        var binance = new StubGateway { Name = "Binance" };
        var map = new Dictionary<string, IExchangeGateway>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Binance"] = binance,
        };

        Assert.Same(binance, MainWindowViewModel.ResolveGateway(map, "Kraken", binance));
        Assert.Same(binance, MainWindowViewModel.ResolveGateway(map, "", binance));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter SpotExchangeResolveTests`
Expected: FAIL — `MainWindowViewModel.ResolveGateway` not defined (compile error).

- [ ] **Step 3: Add the resolver to MainWindowViewModel**

Add this static method anywhere among the private helpers (e.g. just above `ActiveFuturesGateway` near line 1502). It must be `internal static` so the test can call it (the test project already sees TerminalUI internals/publics — other VM tests reference it):

```csharp
    /// <summary>Resolve an exchange-name key to a gateway, case-insensitive, falling back when absent.</summary>
    internal static IExchangeGateway ResolveGateway(
        IReadOnlyDictionary<string, IExchangeGateway> map, string key, IExchangeGateway fallback)
        => !string.IsNullOrWhiteSpace(key) && map.TryGetValue(key, out var gw) ? gw : fallback;
```

If `IReadOnlyDictionary` needs a using, `System.Collections.Generic` is already imported in this file.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter SpotExchangeResolveTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs CryptoAITerminal.Core.Tests/SpotExchangeResolveTests.cs
git commit -m "feat(trading): pure gateway resolver for spot exchange selection" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 2: Spot map, selection, active getter, status props

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add the backing field** next to the other spot-gateway fields (around line 64–69 where `_bybitSpotGateway` etc. are declared). Add:

```csharp
    private IReadOnlyDictionary<string, Core.Interfaces.IExchangeGateway> _spotGatewaysMap = null!;
    private string _selectedSpotExchange = "Binance";
```

- [ ] **Step 2: Build the map in the constructor.** Find where `_futuresGatewaysMap` is created (around line 250, right after the spot gateways `_bybitSpotGateway = bybitSpot;` etc. are assigned ~line 242–247). Immediately after the `_kucoinFuturesGateway = kucoinFutures;` assignment, add:

```csharp
        // Spot order book / price / placement can target any of these (mirrors _futuresGatewaysMap).
        _spotGatewaysMap = new Dictionary<string, Core.Interfaces.IExchangeGateway>(StringComparer.OrdinalIgnoreCase)
        {
            ["Binance"] = _gateway,
            ["Bybit"]   = _bybitSpotGateway,
            ["OKX"]     = _okxSpotGateway,
            ["KuCoin"]  = _kucoinSpotGateway,
        };
```

(If the dashboard `custom-markets` block was inserted in this area in a prior feature, just place this map creation anywhere after all four spot gateway fields are assigned and before the map is first used.)

- [ ] **Step 3: Add the public selection + options + active getter + readiness + status props.** Place near `ActiveFuturesGateway` (line ~1502) and the futures status props (`IsFuturesPrivateApiReady` ~1961, `FuturesPrivateApiStatusLabel` ~1962). Add:

```csharp
    public IReadOnlyList<string> SpotExchangeOptions { get; } = ["Binance", "Bybit", "OKX", "KuCoin"];

    public string SelectedSpotExchange
    {
        get => _selectedSpotExchange;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSpotExchange, value);
            this.RaisePropertyChanged(nameof(IsSpotPrivateApiReady));
            this.RaisePropertyChanged(nameof(SpotPrivateApiStatusLabel));
            this.RaisePropertyChanged(nameof(SpotPrivateApiStatusBrush));
            this.RaisePropertyChanged(nameof(CexMarketModeSummary));
            this.RaisePropertyChanged(nameof(TradingTerminalSummary));
            RaiseCexActionStateChanged();
            _ = RefreshSelectedOrderBookAsync();
        }
    }

    private Core.Interfaces.IExchangeGateway ActiveSpotGateway =>
        ResolveGateway(_spotGatewaysMap, _selectedSpotExchange, _gateway);

    /// <summary>True only in spot CEX trading mode — controls the spot exchange picker's visibility.</summary>
    public bool IsManualSpotMode => IsCexTradingMode && !IsManualFuturesMode;

    public bool IsSpotPrivateApiReady => ActiveSpotGateway.HasPrivateApiCredentials;
    public string SpotPrivateApiStatusLabel => IsSpotPrivateApiReady
        ? $"{SelectedSpotExchange}: Private API Ready"
        : $"{SelectedSpotExchange}: API keys missing";
    public string SpotPrivateApiStatusBrush => IsSpotPrivateApiReady ? "#3DDC84" : "#F4B860";
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors. (`RaiseCexActionStateChanged` and `RefreshSelectedOrderBookAsync` already exist; `CexMarketModeSummary`, `TradingTerminalSummary`, `IsCexTradingMode`, `IsManualFuturesMode` already exist.)

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat(trading): spot gateway map, selection, status props" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 3: Route order book, ActiveCexGateway, and spot placement through ActiveSpotGateway

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Route `ActiveCexGateway`.** Find (line ~3544):

```csharp
    private IExchangeGateway ActiveCexGateway => IsManualFuturesMode ? ActiveFuturesGateway : _gateway;
```
Replace `_gateway` with `ActiveSpotGateway`:
```csharp
    private IExchangeGateway ActiveCexGateway => IsManualFuturesMode ? ActiveFuturesGateway : ActiveSpotGateway;
```

- [ ] **Step 2: Route the order-book refresh.** In `RefreshOrderBookAsync` (line ~5406), change the spot gateway selection and the two spot fallbacks. Current:

```csharp
        var useFutures = IsManualFuturesMode;
        var gateway = useFutures ? (IExchangeGateway)_futuresGateway : _gateway;
```
becomes:
```csharp
        var useFutures = IsManualFuturesMode;
        var spotGateway = ActiveSpotGateway;
        var gateway = useFutures ? ActiveFuturesGateway : spotGateway;
```
Then the two futures-fallback lines that read `await _gateway.GetOrderBookAsync(market.Symbol, depth: 50);` (lines ~5421 and ~5427) become:
```csharp
                    orderBook = await spotGateway.GetOrderBookAsync(market.Symbol, depth: 50);
```
(apply to both occurrences inside this method).

> Note: the original `gateway` used `ActiveFuturesGateway` only implicitly via `_futuresGateway`; using `ActiveFuturesGateway` here also fixes futures to honor the selected futures exchange in the book — that is consistent and desired. If a reviewer objects to changing futures behavior, keep `(IExchangeGateway)_futuresGateway` for the futures branch and only change the spot side; the spot change is what this feature requires.

- [ ] **Step 3: Route spot order placement.** Find (line ~4364):

```csharp
        var router = new MarketOrderRouter(_gateway);
        return side == CryptoAITerminal.Core.Enums.OrderSide.Buy
            ? await router.BuyMarketAsync(SelectedTradingSymbol, quantity)
            : await router.SellMarketAsync(SelectedTradingSymbol, quantity);
```
Replace `_gateway` with `ActiveSpotGateway`:
```csharp
        var router = new MarketOrderRouter(ActiveSpotGateway);
```

- [ ] **Step 4: Check for other spot-limit placement paths.** Open `PlaceBuyLimit` (line ~4606) and `PlaceSellLimit` (line ~4634). If either constructs a router/gateway directly with `_gateway` for the spot path, change that to `ActiveSpotGateway` too (same pattern). If they already delegate to the method changed in Step 3, no change is needed. Show the exact edit you make (or state "no spot-limit gateway literal found").

- [ ] **Step 5: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat(trading): route order book + spot orders through ActiveSpotGateway" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 4: Gate spot orders on the selected exchange's API keys

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add the spot-keys guard** in the single chokepoint `GetCexExecutionGuardReason` (line ~5941). After the existing futures block:

```csharp
        if (IsManualFuturesMode && !IsFuturesPrivateApiReady)
        {
            return "Binance futures private API credentials are required for this live action.";
        }
```
add:
```csharp
        if (!IsManualFuturesMode && !IsSpotPrivateApiReady)
        {
            return $"Add API keys for {SelectedSpotExchange} in Settings to place spot orders.";
        }
```

- [ ] **Step 2: Refresh spot status when credentials reload.** Find the method that raises the futures status props after credentials load (it contains `this.RaisePropertyChanged(nameof(FuturesPrivateApiStatusLabel));` around line 5920). Right after those two futures lines, add:

```csharp
        this.RaisePropertyChanged(nameof(IsSpotPrivateApiReady));
        this.RaisePropertyChanged(nameof(SpotPrivateApiStatusLabel));
        this.RaisePropertyChanged(nameof(SpotPrivateApiStatusBrush));
```

- [ ] **Step 3: Raise spot picker visibility + status when market mode toggles.** Find the `IsManualFuturesMode` / `SelectedCexMarketMode` setter block that raises `FuturesPrivateApiStatusLabel` (around line 1495). Right after the existing `this.RaisePropertyChanged(...)` calls there, add:

```csharp
            this.RaisePropertyChanged(nameof(IsManualSpotMode));
            this.RaisePropertyChanged(nameof(IsSpotPrivateApiReady));
            this.RaisePropertyChanged(nameof(SpotPrivateApiStatusLabel));
            this.RaisePropertyChanged(nameof(SpotPrivateApiStatusBrush));
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat(trading): block spot orders when selected exchange has no API keys" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 5: UI — spot exchange dropdown + status label

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml`

- [ ] **Step 1: Add the spot exchange picker.** Find the spot market-mode block — the `ComboBox` bound to `AvailableCexMarketModes` (around line 240) and the futures `Exchange` block right after it (lines 244–249, `IsVisible="{Binding IsManualFuturesMode}"`). Immediately AFTER the futures exchange `StackPanel` (after line 249) add a spot equivalent:

```xml
            <StackPanel Spacing="6" IsVisible="{Binding IsManualSpotMode}">
              <TextBlock Classes="Overline" Text="Exchange (spot)" />
              <ComboBox ItemsSource="{Binding SpotExchangeOptions}"
                        SelectedItem="{Binding SelectedSpotExchange}"
                        HorizontalAlignment="Stretch" />
              <TextBlock Text="{Binding SpotPrivateApiStatusLabel}"
                         Foreground="{Binding SpotPrivateApiStatusBrush}"
                         TextWrapping="Wrap" FontSize="11" />
            </StackPanel>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Manual smoke test**

Launch:
```bash
cd /c/Users/090/Documents/GitHub/CryptoAI
nohup "./CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe" >/tmp/spot.log 2>&1 &
PID=$!; sleep 7; ps -p $PID >/dev/null && echo ALIVE || echo DEAD; head -30 /tmp/spot.log
```
On the Trading desk in spot CEX mode (not futures, not DEX):
1. The "Exchange (spot)" dropdown shows Binance/Bybit/OKX/KuCoin.
2. Switching to Bybit/OKX/KuCoin reloads the order book from that exchange (bids/asks change).
3. With Binance selected (keys present) Buy/Sell are enabled; selecting an exchange with no keys disables the primary order button and the status label turns amber with the "API keys missing" hint.
4. Switching the market mode to Futures hides the spot picker (futures picker shows instead); DEX mode unaffected.
Kill the process when done: `kill $PID`.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml
git commit -m "feat(trading): spot exchange dropdown + key-status label" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 6: Full verification

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj`
Expected: all pass (prior count + 2 new SpotExchangeResolveTests).

- [ ] **Step 2: Confirm clean build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: 0 errors.

- [ ] **Step 3: Commit any fixups**

```bash
git add -A
git commit -m "test(trading): spot exchange selector green" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

## Self-Review notes (already applied)

- **Spec coverage:** spot map + selection + ActiveSpotGateway (Task 2); routing order book + ActiveCexGateway + placement (Task 3); keys gate + status (Task 4); UI dropdown + status label (Task 5); resolver unit test (Task 1); manual smoke (Task 5 Step 3).
- **No-keys behavior:** order book always loads (public, Task 3); Buy/Sell blocked via guard reason (Task 4); dropdown lists all four regardless of keys (Task 5).
- **Type/name consistency:** `ResolveGateway` (Task 1) used by `ActiveSpotGateway` (Task 2). `_spotGatewaysMap`, `_selectedSpotExchange`, `SelectedSpotExchange`, `SpotExchangeOptions`, `IsManualSpotMode`, `IsSpotPrivateApiReady`, `SpotPrivateApiStatusLabel`/`Brush` consistent across Tasks 2/3/4/5. Reuses existing `RaiseCexActionStateChanged`, `RefreshSelectedOrderBookAsync`, `ActiveFuturesGateway`, `GetCexExecutionGuardReason`.
- **Scope:** single VM + one view + one test — one cohesive plan, no decomposition needed.
