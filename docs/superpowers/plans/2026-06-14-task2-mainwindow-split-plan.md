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

## Pages (handoff order)
- [x] Sniper → `SniperView` (commit c06134c)
- [ ] Bots (placeholder `IsBotsSectionVisible`)
- [ ] AI Signals (TabItem)
- [ ] Portfolio (TabItem)
- [ ] Backtest (placeholder `IsBacktestSectionVisible`)
- [ ] Markets (TabItem)
- [ ] Dashboard (TabItem)
- [ ] Smaller placeholder sections: Risk, Funding, Arb, Copy, StatArb, Router, Scanner,
      Liquidation, Rules, Journal, Gas, Positions, News, OnChain, Analytics, Settings, Help, Logout, Whale

## Notes / gotchas
- Line numbers SHIFT after each extraction — re-grep boundaries before each page.
- There are TWO `IsFundingRateSectionVisible` StackPanels (funding + funding-arb area) — check both.
- Verify no `x:Name` inside a page is referenced by `MainWindow.axaml.cs` before moving (Sniper had none).
- Charts (`ctrl:CexPriceChart`/`DexPriceChart`) need `xmlns:ctrl`.
