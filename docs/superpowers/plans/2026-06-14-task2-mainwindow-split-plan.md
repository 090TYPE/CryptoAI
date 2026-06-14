# Task 2 — Split MainWindow.axaml into UserControls — progress + procedure

Branch: `task2-mainwindow-split` (stacked on `task1-ui-language-unification`).
Pattern model: `Views/TradingDeskView.axaml` (already extracted). 1 page = 1 commit.
Decision (user): use **`x:CompileBindings="False"`** on each extracted page — the
`{Binding $parent[Window].DataContext.X}` bindings inside DataTemplates can't be
statically typed inside a UserControl. Runtime behavior identical (reflection bindings).

## Per-page procedure (validated on Sniper)
1. Find the page's outer element + line range:
   - TabItem pages: inner `<StackPanel ...>` body (keep the `<TabItem>/<ScrollViewer>` wrapper in MainWindow).
   - Placeholder sections: the whole `<StackPanel ... IsVisible="{Binding IsXxxSectionVisible}">…</StackPanel>`
     (or `<Border IsVisible="…">` for Router/Scanner). Move as the UserControl root (IsVisible stays on it).
2. Script-extract the line range into `Views/<Page>View.axaml`, wrapped in:
   ```
   <UserControl xmlns=… xmlns:x=… xmlns:vm=… xmlns:ctrl=… [xmlns:wt=… if used]
                x:Class="CryptoAITerminal.TerminalUI.Views.<Page>View"
                x:DataType="vm:MainWindowViewModel" x:CompileBindings="False">
   … body …
   </UserControl>
   ```
   Preserve UTF-8 BOM + LF. Add empty `<Page>View.axaml.cs` (InitializeComponent only).
3. Replace the body in MainWindow with `<views:<Page>View />` (TabItem) or
   `<views:<Page>View />` in place of the section StackPanel (placeholder).
4. Build `CryptoAITerminal.TerminalUI` → 0 errors. Launch smoke (no crash). Commit.

## Namespaces used inside sections (from x:DataType grep)
Most need `vm:` + `ctrl:`. Whale section also uses `wt:` (`wt:LabeledWallet`). No section uses `views:`.

## Pages — ALL DONE
- [x] Sniper → `SniperView` (c06134c)
- [x] Bots → `BotsView` (8e1fdce) — bot-log auto-scroll moved into BotsView code-behind
- [x] Dashboard, Markets, Portfolio, AI Signals → `*View` (9362b9c)
- [x] All 21 placeholder sections → `*View` (86e57a6): Risk, Backtest, Whale, FundingRate,
      FundingArb, Arb, Copy, StatArb, Router, Scanner, Liquidation, Rules, Journal, Gas, Positions,
      News, OnChain, Analytics, Settings, Help, Logout

## Result
`MainWindow.axaml`: **11523 → 1198 lines**. It now holds only the shell: sidebar nav, the
`TabControl` (each TabItem = `<views:*View/>`), the placeholder `Border` (list of `<views:*View/>`),
splash overlay, command palette. 27 page UserControls in `Views/`. Build 0 errors, 294 tests green,
launch smoke OK on every commit. Code-behind coupling: only `BotLogScrollViewer` (moved to BotsView).

### Note for whoever merges
Branch `task2-mainwindow-split` is stacked on `task1-ui-language-unification`. Merge Task 1 first,
then Task 2. Visual smoke per page (click each nav item in RU/EN) recommended before merge —
automated checks only cover build + Dashboard-on-startup + unit tests.

## Notes / gotchas
- Line numbers SHIFT after each extraction — re-grep boundaries before each page.
- There are TWO `IsFundingRateSectionVisible` StackPanels (funding + funding-arb area) — check both.
- Verify no `x:Name` inside a page is referenced by `MainWindow.axaml.cs` before moving (Sniper had none).
- Charts (`ctrl:CexPriceChart`/`DexPriceChart`) need `xmlns:ctrl`.
