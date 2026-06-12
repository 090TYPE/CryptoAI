# DEX AI Token Verdict — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a token is selected on the DEX trading page, auto-show an AI risk verdict card (AVOID/RISKY/NEUTRAL/FAVORABLE), with a "Deep scan" button that enriches it via on-chain security data.

**Architecture:** Reuse the existing `TokenSecurityAiService` (Claude/ChatGPT + offline heuristic, cached) for the verdict, and `Gateway.DEX/TokenSecurityService` (GoPlus/Honeypot.is/RugCheck) for the optional deep scan. A small `DexTokenAiVerdictViewModel` holds card state; `DexTradingViewModel` owns its own service instances and orchestrates refresh-on-select plus the deep-scan command. A pure `DexSecuritySummary.Build` maps the scan result into a keyword-rich string the verdict service consumes.

**Tech Stack:** C# / .NET 8 / Avalonia / ReactiveUI / xUnit.

**Deviation from spec (intentional, lower risk):** `DexTradingViewModel` owns its *own* `TokenSecurityAiService` instance (`= new()`, mirroring `SniperViewModel`) rather than sharing the sniper's private instance. The API key/model flow globally through `AiRuntime`, so behaviour is identical; this avoids touching `SniperViewModel`/`MainWindowViewModel`. Net result: **no `MainWindowViewModel` changes needed.**

---

## File Structure

- **Create** `CryptoAITerminal.TerminalUI/Services/DexSecuritySummary.cs` — pure `TokenSecurityResult → string`.
- **Create** `CryptoAITerminal.TerminalUI/ViewModels/DexTokenAiVerdictViewModel.cs` — card-state wrapper around `TokenAiVerdict`.
- **Modify** `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs` — own services, `TokenVerdict` property, `DeepScanTokenCommand`, refresh-on-select, deep-scan method, dispose.
- **Modify** `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml` — verdict card in the DEX Market Snapshot panel (after the stat tiles, ~line 2319).
- **Create** `CryptoAITerminal.Core.Tests/DexSecuritySummaryTests.cs`
- **Create** `CryptoAITerminal.Core.Tests/DexTokenAiVerdictViewModelTests.cs`

Reference facts (verified against current code):
- `TokenAiVerdict` (Core.Models): `int RiskScore`, `string Verdict`, `string[] RedFlags`, `string Reason`, `string Source`, `bool IsFallback`, computed `RedFlagsText` (joins with `" · "`), computed `AccentHex` (AVOID `#FF5D73`, RISKY `#FF8A4C`, FAVORABLE `#3DDC84`, else `#8FA3B8`), `static Pending()` (Verdict="PENDING").
- `TokenSecurityResult` (Core.Models): `bool IsHoneypot/HasMintFunction/HasBlacklist/HasSelfDestruct/HiddenOwner/IsOwnershipRenounced`, `decimal BuyTaxPercent/SellTaxPercent/TopHolderConcentrationPercent`, `bool TopHolderConcentrated`, `int SecurityScore/DeployerRugpullCount`, `string[] Flags`, `string Source`, `bool ScanFailed`.
- `TokenSecurityAiService` (TerminalUI.Services): `Task<TokenAiVerdict> AssessAsync(DexTokenInfo, string? securitySummary = null, CancellationToken = default)`, `void Invalidate(DexTokenInfo)`, cached per `chainId:tokenAddress`, key/model from `AiRuntime`.
- `TokenSecurityService` (Gateway.DEX, `IDisposable`, parameterless ctor): `Task<TokenSecurityResult> ScanAsync(string tokenAddress, string chainId, CancellationToken = default)`.
- `DexTradingViewModel : ReactiveObject, IDisposable`; `SelectedToken` is `DexTokenItemViewModel?` with `.TokenInfo` (`DexTokenInfo` → `.TokenAddress`, `.ChainId`); `HasSelectedToken` exists; `Dispose()` at ~line 653; commands created with `ReactiveCommand.CreateFromTask(..., outputScheduler: App.UiScheduler)`. Usings already include `CryptoAITerminal.Gateway.DEX`, `ReactiveUI`, `System.Reactive`.

---

## Task 1: `DexSecuritySummary.Build` (pure function)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/DexSecuritySummary.cs`
- Test: `CryptoAITerminal.Core.Tests/DexSecuritySummaryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// CryptoAITerminal.Core.Tests/DexSecuritySummaryTests.cs
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DexSecuritySummaryTests
{
    [Fact]
    public void Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DexSecuritySummary.Build(null));
    }

    [Fact]
    public void Honeypot_EmitsHoneypotKeyword_AndSource()
    {
        var r = new TokenSecurityResult
        {
            IsHoneypot = true,
            IsOwnershipRenounced = true,
            Source = "GoPlus + Honeypot.is"
        };

        var s = DexSecuritySummary.Build(r);

        Assert.Contains("honeypot", s);
        Assert.Contains("GoPlus + Honeypot.is", s);
    }

    [Fact]
    public void Taxes_AreFormattedInvariant()
    {
        var r = new TokenSecurityResult
        {
            IsOwnershipRenounced = true,
            BuyTaxPercent = 5m,
            SellTaxPercent = 12.5m,
            Source = "GoPlus"
        };

        var s = DexSecuritySummary.Build(r);

        Assert.Contains("buy tax 5%", s);
        Assert.Contains("sell tax 12.5%", s);
    }

    [Fact]
    public void CleanResult_ReportsCleanWithScore()
    {
        var r = new TokenSecurityResult
        {
            IsOwnershipRenounced = true,
            SecurityScore = 88,
            Source = "GoPlus"
        };

        var s = DexSecuritySummary.Build(r);

        Assert.Contains("clean", s);
        Assert.Contains("88/100", s);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexSecuritySummaryTests`
Expected: FAIL — `DexSecuritySummary` does not exist (compile error).

- [ ] **Step 3: Implement `DexSecuritySummary`**

```csharp
// CryptoAITerminal.TerminalUI/Services/DexSecuritySummary.cs
using System.Collections.Generic;
using System.Globalization;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Flattens an on-chain <see cref="TokenSecurityResult"/> into a keyword-rich
/// one-line summary fed back into <see cref="TokenSecurityAiService"/>. Its
/// offline heuristic looks for "honeypot"/"mintable"/"blacklist"; the live
/// model reads the whole sentence.
/// </summary>
public static class DexSecuritySummary
{
    public static string Build(TokenSecurityResult? result)
    {
        if (result is null)
            return string.Empty;

        var parts = new List<string>();
        if (result.IsHoneypot)            parts.Add("honeypot");
        if (result.HasMintFunction)       parts.Add("mintable");
        if (result.HasBlacklist)          parts.Add("blacklist");
        if (result.HasSelfDestruct)       parts.Add("self-destruct");
        if (result.HiddenOwner)           parts.Add("hidden owner");
        if (!result.IsOwnershipRenounced) parts.Add("ownership not renounced");
        if (result.BuyTaxPercent > 0m)
            parts.Add($"buy tax {result.BuyTaxPercent.ToString("0.#", CultureInfo.InvariantCulture)}%");
        if (result.SellTaxPercent > 0m)
            parts.Add($"sell tax {result.SellTaxPercent.ToString("0.#", CultureInfo.InvariantCulture)}%");
        if (result.TopHolderConcentrated)
            parts.Add($"top holder {result.TopHolderConcentrationPercent.ToString("0.#", CultureInfo.InvariantCulture)}%");
        if (result.DeployerRugpullCount > 0)
            parts.Add($"deployer {result.DeployerRugpullCount} prior rugpull(s)");

        foreach (var flag in result.Flags)
            if (!string.IsNullOrWhiteSpace(flag))
                parts.Add(flag);

        var source = string.IsNullOrWhiteSpace(result.Source) ? "on-chain scan" : result.Source;
        return parts.Count == 0
            ? $"On-chain scan clean (score {result.SecurityScore}/100, {source})."
            : $"On-chain scan ({source}): {string.Join(", ", parts)}.";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexSecuritySummaryTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/DexSecuritySummary.cs CryptoAITerminal.Core.Tests/DexSecuritySummaryTests.cs
git commit -m "feat: DexSecuritySummary maps on-chain scan to verdict input"
```

---

## Task 2: `DexTokenAiVerdictViewModel` (card state)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/ViewModels/DexTokenAiVerdictViewModel.cs`
- Test: `CryptoAITerminal.Core.Tests/DexTokenAiVerdictViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// CryptoAITerminal.Core.Tests/DexTokenAiVerdictViewModelTests.cs
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.Core.Tests;

public class DexTokenAiVerdictViewModelTests
{
    [Fact]
    public void Initial_HasNoVerdict_AndPendingBadge()
    {
        var vm = new DexTokenAiVerdictViewModel();
        Assert.False(vm.HasVerdict);
        Assert.Equal("PENDING", vm.Badge);
        Assert.False(vm.HasDeepScanNote);
    }

    [Fact]
    public void ApplyVerdict_MapsBadgeScoreAccentAndFlags()
    {
        var vm = new DexTokenAiVerdictViewModel();
        vm.ApplyVerdict(new TokenAiVerdict
        {
            Verdict = "AVOID",
            RiskScore = 82,
            RedFlags = new[] { "honeypot signal", "very thin liquidity" },
            Reason = "High risk profile.",
            Source = "Heuristic (offline)"
        });

        Assert.True(vm.HasVerdict);
        Assert.Equal("AVOID", vm.Badge);
        Assert.Equal("#FF5D73", vm.AccentHex);
        Assert.Equal("82/100", vm.ScoreLabel);
        Assert.Equal("honeypot signal · very thin liquidity", vm.RedFlagsText);
        Assert.Equal("High risk profile.", vm.Reason);
        Assert.Equal("Heuristic (offline)", vm.SourceLabel);
    }

    [Fact]
    public void DeepScanNote_TogglesHasDeepScanNote()
    {
        var vm = new DexTokenAiVerdictViewModel();
        Assert.False(vm.HasDeepScanNote);
        vm.DeepScanNote = "Deep scan unavailable: RugCheck.xyz";
        Assert.True(vm.HasDeepScanNote);
    }

    [Fact]
    public void Reset_ClearsVerdictAndNote()
    {
        var vm = new DexTokenAiVerdictViewModel();
        vm.ApplyVerdict(new TokenAiVerdict { Verdict = "FAVORABLE", RiskScore = 10 });
        vm.DeepScanNote = "x";

        vm.Reset();

        Assert.False(vm.HasVerdict);
        Assert.Equal("PENDING", vm.Badge);
        Assert.Null(vm.DeepScanNote);
        Assert.False(vm.HasDeepScanNote);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexTokenAiVerdictViewModelTests`
Expected: FAIL — `DexTokenAiVerdictViewModel` does not exist (compile error).

- [ ] **Step 3: Implement `DexTokenAiVerdictViewModel`**

```csharp
// CryptoAITerminal.TerminalUI/ViewModels/DexTokenAiVerdictViewModel.cs
using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Card-state wrapper around a <see cref="TokenAiVerdict"/> for the DEX trading
/// page. Holds busy / deep-scan flags and exposes binding-friendly pass-throughs.
/// </summary>
public sealed class DexTokenAiVerdictViewModel : ReactiveObject
{
    private TokenAiVerdict _verdict = TokenAiVerdict.Pending();
    private bool _hasVerdict;
    private bool _isBusy;
    private bool _deepScanBusy;
    private string? _deepScanNote;

    public bool HasVerdict
    {
        get => _hasVerdict;
        private set => this.RaiseAndSetIfChanged(ref _hasVerdict, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public bool DeepScanBusy
    {
        get => _deepScanBusy;
        set => this.RaiseAndSetIfChanged(ref _deepScanBusy, value);
    }

    public string? DeepScanNote
    {
        get => _deepScanNote;
        set
        {
            this.RaiseAndSetIfChanged(ref _deepScanNote, value);
            this.RaisePropertyChanged(nameof(HasDeepScanNote));
        }
    }

    public bool HasDeepScanNote => !string.IsNullOrWhiteSpace(_deepScanNote);

    public string Badge => _verdict.Verdict;
    public string AccentHex => _verdict.AccentHex;
    public string ScoreLabel => $"{_verdict.RiskScore}/100";
    public string Reason => _verdict.Reason;
    public string RedFlagsText => _verdict.RedFlagsText;
    public string SourceLabel => _verdict.Source;

    public void ApplyVerdict(TokenAiVerdict verdict)
    {
        _verdict = verdict ?? TokenAiVerdict.Pending();
        HasVerdict = true;
        RaisePassThroughs();
    }

    public void Reset()
    {
        _verdict = TokenAiVerdict.Pending();
        HasVerdict = false;
        DeepScanNote = null;
        RaisePassThroughs();
    }

    private void RaisePassThroughs()
    {
        this.RaisePropertyChanged(nameof(Badge));
        this.RaisePropertyChanged(nameof(AccentHex));
        this.RaisePropertyChanged(nameof(ScoreLabel));
        this.RaisePropertyChanged(nameof(Reason));
        this.RaisePropertyChanged(nameof(RedFlagsText));
        this.RaisePropertyChanged(nameof(SourceLabel));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CryptoAITerminal.Core.Tests --filter DexTokenAiVerdictViewModelTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/DexTokenAiVerdictViewModel.cs CryptoAITerminal.Core.Tests/DexTokenAiVerdictViewModelTests.cs
git commit -m "feat: DexTokenAiVerdictViewModel card state"
```

---

## Task 3: Wire verdict into `DexTradingViewModel`

No unit test here (constructing `DexTradingViewModel` needs the full Avalonia/timer stack; the orchestration is verified by build + the smoke run in Task 5). Keep each edit minimal and exact.

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs`

- [ ] **Step 1: Add the Services using**

At the top of the file, after `using CryptoAITerminal.Gateway.DEX;` (line 11), add:

```csharp
using CryptoAITerminal.TerminalUI.Services;
```

- [ ] **Step 2: Add private fields**

After the field `private string _onChainScanStatus = string.Empty;` (~line 27), add:

```csharp
    private readonly TokenSecurityAiService _tokenAiService = new();
    private readonly TokenSecurityService _securityScanner = new();
    private int _verdictSeq;
```

- [ ] **Step 3: Add the verdict property + command declaration**

Next to the other command declarations (after `public ReactiveCommand<string, Unit> SelectChartRangeCommand { get; }`, ~line 601), add:

```csharp
    public DexTokenAiVerdictViewModel TokenVerdict { get; } = new();
    public ReactiveCommand<Unit, Unit> DeepScanTokenCommand { get; }
```

- [ ] **Step 4: Create the command in the constructor**

After `SelectChartRangeCommand = ReactiveCommand.CreateFromTask<string>(SelectChartRangeAsync, outputScheduler: App.UiScheduler);` (~line 618), add:

```csharp
        DeepScanTokenCommand = ReactiveCommand.CreateFromTask(DeepScanTokenAsync, outputScheduler: App.UiScheduler);
```

- [ ] **Step 5: Trigger verdict refresh on selection**

In the `SelectedToken` setter, immediately after `_ = RefreshTokenBalanceAsync();` (~line 91), add:

```csharp
            _ = RefreshTokenVerdictAsync(value);
```

- [ ] **Step 6: Add the two orchestration methods**

Add these methods to the class body (e.g. just above `public void Dispose()`):

```csharp
    private async Task RefreshTokenVerdictAsync(DexTokenItemViewModel? token)
    {
        if (token is null)
        {
            TokenVerdict.Reset();
            return;
        }

        var seq = ++_verdictSeq;
        TokenVerdict.IsBusy = true;
        TokenVerdict.DeepScanNote = null;
        try
        {
            var verdict = await _tokenAiService.AssessAsync(token.TokenInfo);
            if (seq != _verdictSeq || !ReferenceEquals(SelectedToken, token))
                return; // a newer selection superseded this one
            TokenVerdict.ApplyVerdict(verdict);
        }
        catch
        {
            // Never let verdict failure break token selection; keep prior state.
        }
        finally
        {
            if (seq == _verdictSeq)
                TokenVerdict.IsBusy = false;
        }
    }

    private async Task DeepScanTokenAsync()
    {
        var token = SelectedToken;
        if (token is null)
            return;

        TokenVerdict.DeepScanBusy = true;
        TokenVerdict.DeepScanNote = null;
        try
        {
            var scan = await _securityScanner.ScanAsync(
                token.TokenInfo.TokenAddress, token.TokenInfo.ChainId);

            if (!ReferenceEquals(SelectedToken, token))
                return; // token changed while scanning

            if (scan.ScanFailed)
            {
                TokenVerdict.DeepScanNote = $"Deep scan unavailable: {scan.Source}";
                return;
            }

            var summary = DexSecuritySummary.Build(scan);
            _tokenAiService.Invalidate(token.TokenInfo);
            var verdict = await _tokenAiService.AssessAsync(token.TokenInfo, summary);

            if (!ReferenceEquals(SelectedToken, token))
                return;

            TokenVerdict.ApplyVerdict(verdict);
        }
        catch (System.Exception ex)
        {
            TokenVerdict.DeepScanNote = $"Deep scan error: {ex.Message}";
        }
        finally
        {
            TokenVerdict.DeepScanBusy = false;
        }
    }
```

- [ ] **Step 7: Dispose the scanner**

Inside the existing `public void Dispose()` body (~line 653), add:

```csharp
        _securityScanner.Dispose();
```

- [ ] **Step 8: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs
git commit -m "feat: DEX trading auto-assesses token risk + deep-scan command"
```

---

## Task 4: Verdict card in the DEX Market Snapshot panel

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml`

- [ ] **Step 1: Insert the card after the selected-token stat tiles**

Find the closing `</Grid>` of the "24h Volume / 24h Change" row in the **DEX Market Snapshot** panel (immediately before the `<!-- Pair + token address -->` comment, ~line 2319–2321). Insert the following block right after that `</Grid>` and before the `<!-- Pair + token address -->` comment:

```xml
                  <!-- AI risk verdict (auto on select; Deep scan enriches) -->
                  <Border Classes="SoftPanel" Padding="12"
                          IsVisible="{Binding DexTradingVM.HasSelectedToken}">
                    <StackPanel Spacing="8">
                      <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="8">
                        <Border Grid.Column="0"
                                Background="{Binding DexTradingVM.TokenVerdict.AccentHex}"
                                CornerRadius="4" Padding="8,3" VerticalAlignment="Center">
                          <TextBlock Text="{Binding DexTradingVM.TokenVerdict.Badge}"
                                     Foreground="White" FontWeight="Bold" FontSize="12" />
                        </Border>
                        <TextBlock Grid.Column="1" VerticalAlignment="Center"
                                   Text="{Binding DexTradingVM.TokenVerdict.ScoreLabel, StringFormat=risk {0}}"
                                   Foreground="#8FA3B8" FontSize="12" />
                        <Button Grid.Column="2" Content="🔍 Deep scan"
                                Classes="GhostButton" FontSize="11" Padding="8,3"
                                Command="{Binding DexTradingVM.DeepScanTokenCommand}"
                                IsEnabled="{Binding !DexTradingVM.TokenVerdict.DeepScanBusy}" />
                      </Grid>
                      <TextBlock Text="{Binding DexTradingVM.TokenVerdict.RedFlagsText}"
                                 Foreground="#C9D6E2" FontSize="11" TextWrapping="Wrap" />
                      <TextBlock Text="{Binding DexTradingVM.TokenVerdict.Reason}"
                                 Foreground="#8FA3B8" FontSize="11" TextWrapping="Wrap" />
                      <TextBlock Text="{Binding DexTradingVM.TokenVerdict.SourceLabel}"
                                 Foreground="#5C6E82" FontSize="10" />
                      <TextBlock Text="{Binding DexTradingVM.TokenVerdict.DeepScanNote}"
                                 Foreground="#F4B860" FontSize="10" TextWrapping="Wrap"
                                 IsVisible="{Binding DexTradingVM.TokenVerdict.HasDeepScanNote}" />
                    </StackPanel>
                  </Border>
```

- [ ] **Step 2: Build to verify XAML compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded, 0 errors (Avalonia compiles XAML at build time, so binding/markup errors surface here).

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/MainWindow.axaml
git commit -m "feat: AI risk verdict card on DEX Market panel"
```

---

## Task 5: Full verification

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests`
Expected: PASS — all prior tests plus the 8 new ones (4 + 4) green.

- [ ] **Step 2: Smoke-run the app**

Run the built exe:
`CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe`
Manual check (demo mode, no keys):
1. Trading page → Venue = **DEX**.
2. Select a token in the **DEX Market** list.
3. A verdict card appears under the stat tiles with a colored badge (e.g. NEUTRAL/FAVORABLE), `risk N/100`, red flags, reason, and source `Heuristic (offline)`.
4. Switch to another token → card updates (no stale value).
5. Click **🔍 Deep scan** on an EVM token (BSC/ETH/Base) → with network, source/flags update; without network or on Tron, a `Deep scan unavailable…` note appears and the light verdict stays.

- [ ] **Step 3: Final commit (if any smoke fixes were needed)**

```bash
git add -A
git commit -m "chore: finalize DEX AI token verdict"
```

---

## Self-Review (completed during authoring)

- **Spec coverage:** auto-verdict-on-select → Task 3 Step 5/6; instant from loaded data → `AssessAsync(TokenInfo)` (no `securitySummary`); Deep scan button → Task 3 Step 6 + Task 4 button; offline heuristic/no-key → reused service (existing tests); stale-result guard → `_verdictSeq` + `ReferenceEquals`; card placement → Task 4; tests → Tasks 1–2. Covered.
- **Spec deviation:** own `TokenSecurityAiService` instance instead of shared (documented above) — no `MainWindowViewModel` change.
- **Placeholder scan:** none — every step has concrete code/commands.
- **Type consistency:** `TokenVerdict` (`DexTokenAiVerdictViewModel`), `DeepScanTokenCommand`, `RefreshTokenVerdictAsync`, `DeepScanTokenAsync`, `DexSecuritySummary.Build`, `ScanAsync(tokenAddress, chainId)`, `AssessAsync(DexTokenInfo, summary)`, `AccentHex`/`Badge`/`ScoreLabel`/`RedFlagsText`/`SourceLabel`/`HasDeepScanNote` — all consistent across tasks and XAML bindings.
