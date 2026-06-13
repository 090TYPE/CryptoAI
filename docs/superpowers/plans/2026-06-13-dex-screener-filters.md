# DEX Screener Filters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add chain / min-liquidity / min-volume filters and sort to the DEX Market token list, applied client-side over the loaded set and surviving refresh/search.

**Architecture:** A pure `DexTokenFilter.Apply(...)` does the filtering + sorting. `DexTradingViewModel` keeps the raw loaded tokens in `_loadedTokens`, exposes four dropdown-bound filter properties whose setters call `ApplyTokenFilter()`, which rebuilds the visible `Tokens` collection via `DexTokenFilter`. `LoadTokensAsync` is refactored to store raw tokens and call `ApplyTokenFilter()` so filters persist across refresh/search. The UI adds four preset `ComboBox`es to the DEX Market panel.

**Tech Stack:** C# / .NET 8 / Avalonia / ReactiveUI / xUnit.

---

## File Structure

- **Create** `CryptoAITerminal.TerminalUI/Services/DexTokenFilter.cs` — pure filter+sort.
- **Modify** `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs` — `_loadedTokens`, filter options + properties, `ApplyTokenFilter()` + mappers, `LoadTokensAsync` refactor.
- **Modify** `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml` — four filter `ComboBox`es in the DEX Market panel (after SEARCH/REFRESH, ~line 1419).
- **Create** `CryptoAITerminal.Core.Tests/DexTokenFilterTests.cs`

Reference facts (verified against current code):
- `DexTokenInfo` (Core.Models): `string ChainId` (`"bsc"/"ethereum"/"base"/"solana"/"tron"`), `decimal LiquidityUsd`, `decimal Volume24h`, `decimal PriceChange24h`, `string TokenAddress`, plus others.
- `DexTradingViewModel.Tokens` = `ObservableCollection<DexTokenItemViewModel>`; items built via `new DexTokenItemViewModel(); item.Update(tokenInfo);`.
- `LoadTokensAsync(Func<Task<IReadOnlyList<DexTokenInfo>>> loader, string successMessage)` currently fills `Tokens` directly (lines ~703-743).
- Test project `CryptoAITerminal.Core.Tests` references `TerminalUI` (`TokenSecurityAiServiceTests` already uses `CryptoAITerminal.TerminalUI.Services`).
- DEX Market panel: `TradingDeskView.axaml` — SEARCH/REFRESH grid at lines 1416-1419, then `<!-- Selected token quick info -->` at 1421.

---

## Task 1: `DexTokenFilter` (pure filter + sort)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/DexTokenFilter.cs`
- Test: `CryptoAITerminal.Core.Tests/DexTokenFilterTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// CryptoAITerminal.Core.Tests/DexTokenFilterTests.cs
using System.Linq;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DexTokenFilterTests
{
    private static DexTokenInfo Tok(string chain, decimal liq, decimal vol, decimal chg, string addr) => new()
    {
        ChainId = chain,
        LiquidityUsd = liq,
        Volume24h = vol,
        PriceChange24h = chg,
        TokenAddress = addr
    };

    private static IReadOnlyList<DexTokenInfo> Sample() => new[]
    {
        Tok("bsc",      80_000m, 500_000m,  10m, "a"),
        Tok("ethereum", 20_000m, 900_000m, -5m,  "b"),
        Tok("solana",  150_000m, 100_000m,  40m, "c"),
        Tok("base",     40_000m, 250_000m,   2m, "d"),
    };

    [Fact]
    public void NoFilters_SortsByVolumeDescending()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 0m, sortMode: "Volume");
        Assert.Equal(new[] { "b", "a", "d", "c" }, r.Select(t => t.TokenAddress).ToArray());
    }

    [Fact]
    public void ChainFilter_KeepsOnlyThatChain()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: "solana", minLiquidity: 0m, minVolume: 0m, sortMode: "Volume");
        Assert.Single(r);
        Assert.Equal("c", r[0].TokenAddress);
    }

    [Fact]
    public void MinLiquidity_DropsBelowThreshold()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 50_000m, minVolume: 0m, sortMode: "Volume");
        Assert.Equal(new[] { "a", "c" }, r.Select(t => t.TokenAddress).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void MinVolume_DropsBelowThreshold()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 300_000m, sortMode: "Volume");
        Assert.Equal(new[] { "a", "b" }, r.Select(t => t.TokenAddress).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void SortByLiquidity_Descending()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 0m, sortMode: "Liquidity");
        Assert.Equal(new[] { "c", "a", "d", "b" }, r.Select(t => t.TokenAddress).ToArray());
    }

    [Fact]
    public void SortByChange_Descending()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 0m, sortMode: "Change");
        Assert.Equal(new[] { "c", "a", "d", "b" }, r.Select(t => t.TokenAddress).ToArray());
    }

    [Fact]
    public void UnknownSort_FallsBackToVolume()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 0m, sortMode: "Nonsense");
        Assert.Equal(new[] { "b", "a", "d", "c" }, r.Select(t => t.TokenAddress).ToArray());
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var r = DexTokenFilter.Apply(System.Array.Empty<DexTokenInfo>(), null, 0m, 0m, "Volume");
        Assert.Empty(r);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexTokenFilterTests`
Expected: FAIL — `DexTokenFilter` does not exist (compile error).

- [ ] **Step 3: Implement `DexTokenFilter`**

```csharp
// CryptoAITerminal.TerminalUI/Services/DexTokenFilter.cs
using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Pure client-side filter + sort for the DEX Market token list.
/// <paramref name="chainId"/> null/empty = no chain filter (compared case-insensitively
/// against <see cref="DexTokenInfo.ChainId"/>). <paramref name="sortMode"/> "Liquidity"
/// or "Change" sort by those fields, anything else by 24h volume. All sorts descending.
/// </summary>
public static class DexTokenFilter
{
    public static IReadOnlyList<DexTokenInfo> Apply(
        IReadOnlyList<DexTokenInfo> tokens,
        string? chainId,
        decimal minLiquidity,
        decimal minVolume,
        string sortMode)
    {
        if (tokens is null || tokens.Count == 0)
            return Array.Empty<DexTokenInfo>();

        IEnumerable<DexTokenInfo> query = tokens;

        if (!string.IsNullOrWhiteSpace(chainId))
            query = query.Where(t => string.Equals(t.ChainId, chainId, StringComparison.OrdinalIgnoreCase));
        if (minLiquidity > 0m)
            query = query.Where(t => t.LiquidityUsd >= minLiquidity);
        if (minVolume > 0m)
            query = query.Where(t => t.Volume24h >= minVolume);

        query = sortMode switch
        {
            "Liquidity" => query.OrderByDescending(t => t.LiquidityUsd),
            "Change"    => query.OrderByDescending(t => t.PriceChange24h),
            _           => query.OrderByDescending(t => t.Volume24h),
        };

        return query.ToList();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexTokenFilterTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/DexTokenFilter.cs CryptoAITerminal.Core.Tests/DexTokenFilterTests.cs
git commit -m "feat: DexTokenFilter pure filter + sort for DEX token list"
```

---

## Task 2: Wire filters into `DexTradingViewModel`

No unit test (constructing `DexTradingViewModel` needs the full timer/client stack); verified by build + Task 4 smoke run.

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs`

- [ ] **Step 1: Add the Services using**

After `using CryptoAITerminal.Gateway.DEX;` (line 11), add:

```csharp
using CryptoAITerminal.TerminalUI.Services;
```

- [ ] **Step 2: Add backing fields**

After `private string _onChainScanStatus = string.Empty;` (~line 27), add:

```csharp
    private IReadOnlyList<DexTokenInfo> _loadedTokens = System.Array.Empty<DexTokenInfo>();
    private string _lastLoadSuccessMessage = "Latest DEX tokens refreshed.";
    private string _selectedChainFilter = "All";
    private string _selectedMinLiquidity = "Any";
    private string _selectedMinVolume = "Any";
    private string _selectedSortMode = "Volume";
```

- [ ] **Step 3: Add filter option collections + properties**

After `public ObservableCollection<string> QuoteAssetOptions { get; } = new();` (~line 66), add:

```csharp
    public ObservableCollection<string> ChainFilterOptions { get; } = new() { "All", "BSC", "Ethereum", "Base", "Solana", "Tron" };
    public ObservableCollection<string> MinLiquidityOptions { get; } = new() { "Any", "$10k", "$50k", "$100k", "$500k" };
    public ObservableCollection<string> MinVolumeOptions { get; } = new() { "Any", "$10k", "$50k", "$250k", "$1M" };
    public ObservableCollection<string> SortModeOptions { get; } = new() { "Volume", "Liquidity", "24h Change" };

    public string SelectedChainFilter
    {
        get => _selectedChainFilter;
        set { this.RaiseAndSetIfChanged(ref _selectedChainFilter, value); ApplyTokenFilter(); }
    }

    public string SelectedMinLiquidity
    {
        get => _selectedMinLiquidity;
        set { this.RaiseAndSetIfChanged(ref _selectedMinLiquidity, value); ApplyTokenFilter(); }
    }

    public string SelectedMinVolume
    {
        get => _selectedMinVolume;
        set { this.RaiseAndSetIfChanged(ref _selectedMinVolume, value); ApplyTokenFilter(); }
    }

    public string SelectedSortMode
    {
        get => _selectedSortMode;
        set { this.RaiseAndSetIfChanged(ref _selectedSortMode, value); ApplyTokenFilter(); }
    }
```

- [ ] **Step 4: Refactor `LoadTokensAsync` to store raw + apply filter**

Replace the `previousTokenAddress` capture line (~line 708):

```csharp
            var previousTokenAddress = await RunOnUiAsync(() => SelectedToken?.TokenAddress);
```

with nothing (delete that line — `ApplyTokenFilter` captures the selection itself).

Then replace the UI-thread block (~lines 717-732):

```csharp
            await RunOnUiAsync(() =>
            {
                Tokens.Clear();
                foreach (var token in tokens)
                {
                    var item = new DexTokenItemViewModel();
                    item.Update(token);
                    Tokens.Add(item);
                }

                SelectedToken = Tokens.FirstOrDefault(token => string.Equals(token.TokenAddress, previousTokenAddress, StringComparison.OrdinalIgnoreCase))
                    ?? Tokens.FirstOrDefault();

                LastUpdatedLocal = DateTime.Now;
                StatusMessage = Tokens.Count == 0 ? "No tokens found for the current filter." : successMessage;
            });
```

with:

```csharp
            await RunOnUiAsync(() =>
            {
                _loadedTokens = tokens;
                _lastLoadSuccessMessage = successMessage;
                ApplyTokenFilter();
                LastUpdatedLocal = DateTime.Now;
            });
```

- [ ] **Step 5: Add `ApplyTokenFilter` + mappers**

Add these methods to the class (e.g. just after `LoadTokensAsync`):

```csharp
    private void ApplyTokenFilter()
    {
        var previousTokenAddress = SelectedToken?.TokenAddress;

        var filtered = DexTokenFilter.Apply(
            _loadedTokens,
            ChainIdForFilter(_selectedChainFilter),
            ThresholdValue(_selectedMinLiquidity),
            ThresholdValue(_selectedMinVolume),
            SortModeKey(_selectedSortMode));

        Tokens.Clear();
        foreach (var token in filtered)
        {
            var item = new DexTokenItemViewModel();
            item.Update(token);
            Tokens.Add(item);
        }

        SelectedToken = Tokens.FirstOrDefault(t => string.Equals(t.TokenAddress, previousTokenAddress, StringComparison.OrdinalIgnoreCase))
            ?? Tokens.FirstOrDefault();

        StatusMessage = Tokens.Count == 0
            ? (_loadedTokens.Count == 0 ? "No tokens found." : "No tokens match filters.")
            : _lastLoadSuccessMessage;
    }

    private static string? ChainIdForFilter(string display) => display switch
    {
        "BSC"      => "bsc",
        "Ethereum" => "ethereum",
        "Base"     => "base",
        "Solana"   => "solana",
        "Tron"     => "tron",
        _          => null,
    };

    private static decimal ThresholdValue(string preset) => preset switch
    {
        "$10k"  => 10_000m,
        "$50k"  => 50_000m,
        "$100k" => 100_000m,
        "$250k" => 250_000m,
        "$500k" => 500_000m,
        "$1M"   => 1_000_000m,
        _       => 0m,
    };

    private static string SortModeKey(string display) => display == "24h Change" ? "Change" : display;
```

- [ ] **Step 6: Build to verify it compiles**

Make sure the app is not running first:
`Get-Process | Where-Object { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -ErrorAction SilentlyContinue`

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug -v minimal`
Expected: `Ошибок: 0`.

- [ ] **Step 7: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs
git commit -m "feat: DEX token list filter/sort state wired into DexTradingViewModel"
```

---

## Task 3: Filter controls in the DEX Market panel

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml`

- [ ] **Step 1: Insert the filter dropdowns after SEARCH/REFRESH**

Find this block (lines 1416-1420):

```xml
          <Grid ColumnDefinitions="*,*" ColumnSpacing="8">
            <Button Grid.Column="0" Content="SEARCH"  Classes="GhostButton" Command="{Binding DexTradingVM.SearchCommand}" />
            <Button Grid.Column="1" Content="REFRESH" Classes="GhostButton" Command="{Binding DexTradingVM.RefreshCommand}" />
          </Grid>
```

Insert immediately after it (before the `<!-- Selected token quick info -->` comment):

```xml

          <!-- Screener filters -->
          <Grid ColumnDefinitions="*,*" ColumnSpacing="8" RowDefinitions="Auto,Auto" RowSpacing="8">
            <StackPanel Grid.Row="0" Grid.Column="0" Spacing="2">
              <TextBlock Classes="Overline" Text="Chain" />
              <ComboBox ItemsSource="{Binding DexTradingVM.ChainFilterOptions}"
                        SelectedItem="{Binding DexTradingVM.SelectedChainFilter}"
                        HorizontalAlignment="Stretch" />
            </StackPanel>
            <StackPanel Grid.Row="0" Grid.Column="1" Spacing="2">
              <TextBlock Classes="Overline" Text="Sort" />
              <ComboBox ItemsSource="{Binding DexTradingVM.SortModeOptions}"
                        SelectedItem="{Binding DexTradingVM.SelectedSortMode}"
                        HorizontalAlignment="Stretch" />
            </StackPanel>
            <StackPanel Grid.Row="1" Grid.Column="0" Spacing="2">
              <TextBlock Classes="Overline" Text="Min Liq" />
              <ComboBox ItemsSource="{Binding DexTradingVM.MinLiquidityOptions}"
                        SelectedItem="{Binding DexTradingVM.SelectedMinLiquidity}"
                        HorizontalAlignment="Stretch" />
            </StackPanel>
            <StackPanel Grid.Row="1" Grid.Column="1" Spacing="2">
              <TextBlock Classes="Overline" Text="Min Vol" />
              <ComboBox ItemsSource="{Binding DexTradingVM.MinVolumeOptions}"
                        SelectedItem="{Binding DexTradingVM.SelectedMinVolume}"
                        HorizontalAlignment="Stretch" />
            </StackPanel>
          </Grid>
```

- [ ] **Step 2: Build to verify XAML compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug -v minimal`
Expected: `Ошибок: 0` (Avalonia compiles XAML at build; bad bindings fail here).

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml
git commit -m "feat: chain/liquidity/volume/sort filters on DEX Market panel"
```

---

## Task 4: Full verification

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests`
Expected: PASS — all prior tests plus the 8 new `DexTokenFilterTests`.

- [ ] **Step 2: Smoke-run the app**

Launch `CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe`.
Manual check (demo, no keys):
1. Trading page → Venue = **DEX**.
2. The DEX Market panel now shows four dropdowns: **Chain · Sort · Min Liq · Min Vol** (defaults All / Volume / Any / Any) above the token list.
3. Pick **Chain = Solana** → list narrows to Solana pairs.
4. Pick **Min Liq = $100k** → low-liquidity pairs drop out.
5. Pick **Sort = 24h Change** → list reorders by 24h change descending.
6. Reset to **All / Any / Any / Volume** → full list returns, volume-sorted.

- [ ] **Step 3: Final commit (if smoke fixes were needed)**

```bash
git add -A
git commit -m "chore: finalize DEX screener filters"
```

---

## Self-Review (completed during authoring)

- **Spec coverage:** pure filter/sort → Task 1 (`DexTokenFilter`); `_loadedTokens` + filter properties + `ApplyTokenFilter` + `LoadTokensAsync` refactor → Task 2; preset/chain/sort mappings → Task 2 Step 5 (`ChainIdForFilter`/`ThresholdValue`/`SortModeKey`, exactly the spec's mappings); four dropdowns in DEX Market → Task 3; edge cases (empty → status, selection preserved, filters survive refresh) → Task 2 (`ApplyTokenFilter` called from both setters and `LoadTokensAsync`); tests → Task 1. Covered.
- **Placeholder scan:** none — every step has concrete code/commands.
- **Type consistency:** `DexTokenFilter.Apply(IReadOnlyList<DexTokenInfo>, string?, decimal, decimal, string)` used identically in tests, VM, and impl; property names `SelectedChainFilter`/`SelectedMinLiquidity`/`SelectedMinVolume`/`SelectedSortMode` + `*Options` match across VM and XAML; mapper outputs ("bsc"/.../"Change") match `DexTokenFilter` expectations. Consistent.
