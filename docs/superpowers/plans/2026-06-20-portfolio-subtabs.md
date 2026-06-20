# Portfolio Sub-Tabs Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the crowded single-screen Portfolio layout with a four-tab `TabControl` (Обзор / Ребаланс / Кошелёк / Активность·Диагностика), moving existing content blocks verbatim into the tabs.

**Architecture:** Pure XAML reorganization of `Views/PortfolioView.axaml`. A `TabControl` with four `TabItem`s; each tab is a `ScrollViewer` + `StackPanel` holding the moved blocks. No view-model, binding, or code-behind changes — every `{Binding …}` is carried over exactly.

**Tech Stack:** Avalonia 12 (.NET 8), XAML.

> **Branch:** `feature/portfolio-subtabs` (already checked out).
> **Critical rule:** do NOT alter any binding path, converter, `x:DataType`, or `$parent[Window].DataContext.…` reference when moving a block. Cut-and-paste only. The page root keeps `x:DataType="vm:MainWindowViewModel"` and `x:CompileBindings="False"`.

---

## File Structure

- `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml` — the only file changed. The current structure (line references into the pre-change file):
  - Left column "Wallet Hub" (lines ~10–328): license activation (~18–33), Connection Studio expander (~35–214), Security Notes expander (~216–229), Saved Wallets expander (~231–272), Session Status (~274–281), Solana Diagnostic Trace (~283–303), Tron Diagnostic Trace (~305–326).
  - Right column grid (lines ~330–645): Active Session card (~331–343), Live Route card (~345–357), Portfolio Lens (~359–387), Portfolio Rebalancer (~389–609), Wallet Capability + Recent Activity (~611–644).

The four tabs draw from these blocks:
- **Обзор:** Active Session + Live Route + Portfolio Lens + Wallet Capability.
- **Ребаланс:** Portfolio Rebalancer.
- **Кошелёк:** license activation + Connection Studio + Saved Wallets + Security Notes + Session Status.
- **Активность · Диагностика:** Recent Activity + Solana Diagnostic Trace + Tron Diagnostic Trace (and the Tron payments/self-test blocks currently inside Connection Studio move here too — see Task 2 note).

---

### Task 1: Scaffold the TabControl shell (tabs empty, build green)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml`

- [ ] **Step 1: Read the current file** end-to-end so you have every block and its exact bindings in hand: `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml`.

- [ ] **Step 2: Replace the root content** with a `TabControl` skeleton. Keep the `UserControl` opening tag (with its `xmlns`, `x:Class`, `x:DataType`, `x:CompileBindings`) and the closing `</UserControl>` exactly as they are. Between them, replace the single outer `<Border Classes="Panel"> … </Border>` with:

```xml
  <TabControl>
    <TabItem Header="Обзор">
      <ScrollViewer Classes="PageScroll">
        <StackPanel Spacing="16" Margin="4">
          <!-- OVERVIEW BLOCKS -->
        </StackPanel>
      </ScrollViewer>
    </TabItem>
    <TabItem Header="Ребаланс">
      <ScrollViewer Classes="PageScroll">
        <StackPanel Spacing="16" Margin="4">
          <!-- REBALANCE BLOCKS -->
        </StackPanel>
      </ScrollViewer>
    </TabItem>
    <TabItem Header="Кошелёк">
      <ScrollViewer Classes="PageScroll">
        <StackPanel Spacing="16" Margin="4">
          <!-- WALLET BLOCKS -->
        </StackPanel>
      </ScrollViewer>
    </TabItem>
    <TabItem Header="Активность · Диагностика">
      <ScrollViewer Classes="PageScroll">
        <StackPanel Spacing="16" Margin="4">
          <!-- ACTIVITY BLOCKS -->
        </StackPanel>
      </ScrollViewer>
    </TabItem>
  </TabControl>
```

> You will paste real content into the four comment slots in Tasks 2–5. To keep the build green at the end of THIS task, temporarily put a single `<TextBlock Text="…" />` placeholder in each StackPanel; they get replaced in the next tasks. (Avalonia allows an empty `StackPanel`, so placeholders are optional — but a one-line `<TextBlock/>` per tab confirms each tab renders.)

- [ ] **Step 3: Build to verify the shell compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: `Сборка успешно завершена`, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml
git commit -m "refactor(portfolio): scaffold four-tab TabControl shell" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 2: Fill the "Обзор" (Overview) tab

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml`

- [ ] **Step 1: Move the overview blocks** into the `<!-- OVERVIEW BLOCKS -->` StackPanel, in this order. Cut them from their old location and paste verbatim (do not edit any binding):
  1. The "Active Session" card and the "Live Route" card — wrap the two side by side:
     ```xml
     <Grid ColumnDefinitions="*,*" ColumnSpacing="16">
       <!-- paste the Active Session <Border Classes="Panel">…</Border> here, set Grid.Column="0" (remove any old Grid.Row) -->
       <!-- paste the Live Route <Border Classes="Panel">…</Border> here, set Grid.Column="1" (remove any old Grid.Row) -->
     </Grid>
     ```
     (These were the right-column `Grid.Row="0" Grid.Column="0"` and `Grid.Row="0" Grid.Column="1"` borders. Strip their `Grid.Row`/`Grid.Column` attributes and use the new wrapper Grid's columns.)
  2. The "Portfolio Lens" `<Border Classes="Panel">` block (the `ItemsControl` over `WalletVM.Assets`). Strip its `Grid.Row`/`Grid.ColumnSpan`.
  3. The "Wallet Capability" `StackPanel` (currently the left half of the bottom row): paste it inside a `<Border Classes="Panel">` so it reads as its own card:
     ```xml
     <Border Classes="Panel">
       <!-- paste the Wallet Capability StackPanel (H2 "Wallet Capability" + WalletVM.WalletCapabilityText) -->
     </Border>
     ```

- [ ] **Step 2: Build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml
git commit -m "refactor(portfolio): Overview tab (session, route, lens, capability)" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 3: Fill the "Ребаланс" (Rebalance) tab

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml`

- [ ] **Step 1: Move the rebalancer** — cut the entire `<!-- ═══ PORTFOLIO REBALANCER ═══ -->` … `<!-- ═══ END PORTFOLIO REBALANCER ═══ -->` `<Border Classes="Panel">` block (the one bound to `PortfolioRebalanceVM.*`) and paste it into the `<!-- REBALANCE BLOCKS -->` StackPanel. Strip its `Grid.Row`/`Grid.ColumnSpan` attributes. Keep the inner `x:DataType="vm:MainWindowViewModel"` and every binding exactly.

- [ ] **Step 2: Build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml
git commit -m "refactor(portfolio): Rebalance tab" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 4: Fill the "Кошелёк" (Wallet) tab

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml`

- [ ] **Step 1: Move the wallet-setup blocks** into the `<!-- WALLET BLOCKS -->` StackPanel, in order:
  1. The "License" activation `<Border Background="#0A1622" …>` block.
  2. The "Connection Studio" `<Expander>` — BUT it currently also contains the Solana self-test, Tron self-test, and Tron TRC20 Payments sub-blocks. Move the Connection Studio expander here with ONLY: the Wallet Provider buttons, Network combo, Wallet Address, Private Key, and the CONNECT/IMPORT/REFRESH/OPEN/DISCONNECT `WrapPanel`. CUT the three sub-blocks below it inside the expander — the "Solana Self-Test" `<Border Classes="SoftPanel">`, the "Tron Self-Test" `<Border Classes="SoftPanel" IsVisible=…IsTronNetworkSelected>`, and the "Tron TRC20 Payments" `<Border Classes="SoftPanel" IsVisible=…IsTronNetworkSelected>` — and set them aside for Task 5 (paste them into the Activity tab).
  3. The "Security Notes" `<Expander>`.
  4. The "Saved Wallets" `<Expander>` (includes the Execution Matrix SoftPanel + the `SavedWallets` ItemsControl).
  5. The "Session Status" `<Border Classes="SoftPanel">` block.

- [ ] **Step 2: Build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml
git commit -m "refactor(portfolio): Wallet tab (license, connection studio, saved wallets)" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 5: Fill the "Активность · Диагностика" tab + delete leftovers

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml`

- [ ] **Step 1: Move the activity/diagnostic blocks** into the `<!-- ACTIVITY BLOCKS -->` StackPanel, in order:
  1. The "Recent Activity" content — currently the right StackPanel of the bottom row (H2 "Recent Activity" + the "Session Feed" `<Expander>` over `WalletVM.RecentActivity`). Wrap it in a `<Border Classes="Panel">` so it reads as a card.
  2. The three blocks set aside in Task 4: "Solana Self-Test" SoftPanel, "Tron Self-Test" SoftPanel, "Tron TRC20 Payments" SoftPanel (keep their `IsVisible="{Binding WalletVM.IsTronNetworkSelected}"` on the two Tron ones).
  3. The "Solana Diagnostic Trace" `<Border Classes="SoftPanel">` block.
  4. The "Tron Diagnostic Trace" `<Border Classes="SoftPanel" IsVisible=…IsTronNetworkSelected>` block.

- [ ] **Step 2: Remove dead scaffolding** — delete any temporary `<TextBlock/>` placeholders added in Task 1, and confirm no orphaned blocks remain from the old two-column `Grid` (the old left "Wallet Hub" `Border` and the old right `Grid` wrapper should now be fully gone; only the `TabControl` remains as the page root).

- [ ] **Step 3: Build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml
git commit -m "refactor(portfolio): Activity/Diagnostics tab + remove old layout" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

### Task 6: Verify — build, smoke, sanity

**Files:**
- None (verification only)

- [ ] **Step 1: Clean build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug --nologo -v q`
Expected: 0 errors.

- [ ] **Step 2: Confirm no content was lost.** Grep the new file for the key bindings to prove each block survived the move:

Run: `grep -c -E "WalletVM.Assets|PortfolioRebalanceVM.Allocations|LicenseVM.ActivateCommand|WalletVM.SavedWallets|WalletVM.RecentActivity|WalletVM.SolanaDiagnosticSteps|WalletVM.TronDiagnosticSteps|WalletVM.WalletCapabilityText|WalletVM.ActiveWalletBanner|WalletVM.NativeBalanceLabel" CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml`
Expected: 10 (each of the ten anchor bindings present exactly once → total 10 matched lines; if a number is lower, a block was dropped — restore it).

- [ ] **Step 3: Manual smoke**

Launch:
```bash
cd /c/Users/090/Documents/GitHub/CryptoAI
nohup "./CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe" >/tmp/pf.log 2>&1 &
PID=$!; sleep 7; ps -p $PID >/dev/null && echo ALIVE || echo DEAD; head -30 /tmp/pf.log
```
Open the Portfolio page and click each tab:
1. **Обзор** — Active Session + Live Route cards, Portfolio Lens asset cards, Wallet Capability.
2. **Ребаланс** — the rebalancer table with total/threshold/AI/add-asset.
3. **Кошелёк** — license, provider/network/key inputs, connect buttons, saved wallets, security notes, session status.
4. **Активность · Диагностика** — session feed, self-tests, diagnostic traces (Tron-only blocks appear only when a Tron network is selected).
Kill when done: `kill $PID`.

- [ ] **Step 4: Commit (if Step 2 required a fixup)**

```bash
git add CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml
git commit -m "refactor(portfolio): verification fixups" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>" --no-gpg-sign
```

---

## Self-Review notes (already applied)

- **Spec coverage:** TabControl shell (Task 1); Overview (Task 2); Rebalance (Task 3); Wallet (Task 4); Activity/Diagnostics incl. relocating the Tron/Solana sub-blocks out of Connection Studio (Tasks 4 set-aside → 5); old two-column layout removed (Task 5 Step 2); no-loss check + smoke (Task 6).
- **Placeholder scan:** none — every step is a concrete cut/paste with exact block identifiers and bindings.
- **Consistency:** binding paths are never rewritten, only relocated; Tron `IsVisible` guards preserved; the page root attributes are kept. The anchor-binding grep count (10) ties the verification to the ten blocks moved.
- **Scope:** one file, pure markup — single cohesive plan.
