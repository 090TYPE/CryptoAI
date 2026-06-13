# DEX Auto-Refresh Throttle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the 3s auto-refresh timer from re-running the expensive 8-request per-chain scout, so the DEX list no longer risks DexScreener rate limits.

**Architecture:** A pure `DexRefreshPolicy.NextAutoRefresh(chainId, searchText)` returns Skip / ReloadLatest / Search. `AutoRefreshAsync` follows it: search and the cheap "All" feed keep auto-refreshing; a specific chain (multi-request scout) is skipped on the timer and only loads on chain-select + REFRESH.

**Tech Stack:** C# / .NET 8 / xUnit.

---

## File Structure

- **Create** `CryptoAITerminal.TerminalUI/Services/DexRefreshPolicy.cs` — pure auto-refresh decision.
- **Modify** `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs` — `AutoRefreshAsync` uses the policy.
- **Create** `CryptoAITerminal.Core.Tests/DexRefreshPolicyTests.cs`

Verified facts:
- Current `AutoRefreshAsync` (≈795-810):
  ```csharp
  private async Task AutoRefreshAsync()
  {
      if (IsLoading)
      {
          return;
      }

      if (string.IsNullOrWhiteSpace(SearchText))
      {
          await RefreshAsync();
      }
      else
      {
          await SearchAsync();
      }
  }
  ```
- `ChainIdForFilter(string)` is a private static in the VM: returns `"bsc"/"ethereum"/"base"/"solana"/"tron"` or `null` for "All".
- `DexTradingViewModel` already has `using CryptoAITerminal.TerminalUI.Services;`.
- Test project `CryptoAITerminal.Core.Tests` references TerminalUI.

---

## Task 1: `DexRefreshPolicy` (pure) + tests

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/DexRefreshPolicy.cs`
- Test: `CryptoAITerminal.Core.Tests/DexRefreshPolicyTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// CryptoAITerminal.Core.Tests/DexRefreshPolicyTests.cs
using CryptoAITerminal.TerminalUI.Services;
using static CryptoAITerminal.TerminalUI.Services.DexRefreshPolicy;

namespace CryptoAITerminal.Core.Tests;

public class DexRefreshPolicyTests
{
    [Fact]
    public void Search_TakesPriority_EvenWithChain()
    {
        Assert.Equal(AutoRefreshAction.Search, NextAutoRefresh("bsc", "doge"));
        Assert.Equal(AutoRefreshAction.Search, NextAutoRefresh(null, "doge"));
    }

    [Fact]
    public void NoSearch_NoChain_ReloadsLatest()
    {
        Assert.Equal(AutoRefreshAction.ReloadLatest, NextAutoRefresh(null, null));
        Assert.Equal(AutoRefreshAction.ReloadLatest, NextAutoRefresh("", "   "));
    }

    [Fact]
    public void NoSearch_SpecificChain_Skips()
    {
        Assert.Equal(AutoRefreshAction.Skip, NextAutoRefresh("bsc", null));
        Assert.Equal(AutoRefreshAction.Skip, NextAutoRefresh("tron", ""));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Make sure the app is not running: `Get-Process | Where-Object { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -ErrorAction SilentlyContinue`

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexRefreshPolicyTests`
Expected: FAIL — `DexRefreshPolicy` does not exist (compile error).

- [ ] **Step 3: Implement `DexRefreshPolicy`**

```csharp
// CryptoAITerminal.TerminalUI/Services/DexRefreshPolicy.cs
namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Decides what the DEX list auto-refresh tick should do. A specific chain uses
/// the multi-request scout, so it is never auto-rescanned on the fast timer —
/// only the cheap "All" latest feed and active search keep refreshing.
/// </summary>
public static class DexRefreshPolicy
{
    public enum AutoRefreshAction
    {
        Skip,
        ReloadLatest,
        Search
    }

    /// <param name="chainId">Resolved chain id; null/empty means the "All" latest feed.</param>
    /// <param name="searchText">Current search box text.</param>
    public static AutoRefreshAction NextAutoRefresh(string? chainId, string? searchText)
    {
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            return AutoRefreshAction.Search;
        }

        return string.IsNullOrWhiteSpace(chainId)
            ? AutoRefreshAction.ReloadLatest
            : AutoRefreshAction.Skip;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexRefreshPolicyTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/DexRefreshPolicy.cs CryptoAITerminal.Core.Tests/DexRefreshPolicyTests.cs
git commit -m "feat: DexRefreshPolicy decides DEX auto-refresh action"
```

---

## Task 2: Apply the policy in `AutoRefreshAsync`

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs`

- [ ] **Step 1: Replace the `AutoRefreshAsync` body**

Replace this method:

```csharp
    private async Task AutoRefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await RefreshAsync();
        }
        else
        {
            await SearchAsync();
        }
    }
```

with:

```csharp
    private async Task AutoRefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        switch (DexRefreshPolicy.NextAutoRefresh(ChainIdForFilter(_selectedChainFilter), SearchText))
        {
            case DexRefreshPolicy.AutoRefreshAction.Search:
                await SearchAsync();
                break;
            case DexRefreshPolicy.AutoRefreshAction.ReloadLatest:
                await RefreshAsync();
                break;
            // Skip: a specific chain uses the multi-request scout — it loads on
            // chain-select and the REFRESH button, never on the fast timer.
        }
    }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug -v minimal`
Expected: `Ошибок: 0`.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs
git commit -m "fix: DEX auto-refresh no longer rescans per-chain scout on timer"
```

---

## Task 3: Full verification

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests`
Expected: PASS — all prior tests plus the 3 new `DexRefreshPolicyTests`.

- [ ] **Step 2: Smoke-run the app**

Launch `CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe`.
Manual check (demo, no keys):
1. Trading page → Venue = DEX. With Chain = **All**, the list keeps auto-updating (cheap latest feed).
2. Set Chain = **BSC** → list loads once; it does **not** keep re-scanning/reordering on its own.
3. Click **REFRESH** → the BSC list reloads on demand.
4. (Optional) Watch for absence of flicker/reordering while a specific chain is selected.

- [ ] **Step 3: Final commit (if smoke fixes were needed)**

```bash
git add -A
git commit -m "chore: finalize DEX auto-refresh throttle"
```

---

## Self-Review (completed during authoring)

- **Spec coverage:** pure decision (Search/ReloadLatest/Skip) → Task 1 (`DexRefreshPolicy` + tests); `AutoRefreshAsync` uses it → Task 2; specific chain not auto-rescanned, All/search still refresh → encoded in the policy + switch; tests → Task 1; smoke → Task 3. Covered.
- **Placeholder scan:** none — full code in every step.
- **Type consistency:** `DexRefreshPolicy.NextAutoRefresh(string?, string?)` and `AutoRefreshAction { Skip, ReloadLatest, Search }` used identically in tests and the VM switch; `ChainIdForFilter` returns the `string?` the policy expects.
