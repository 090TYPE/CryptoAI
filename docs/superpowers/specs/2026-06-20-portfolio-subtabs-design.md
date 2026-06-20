# Portfolio Tab Sub-Tabs Redesign — Design

**Date:** 2026-06-20
**Project:** CryptoAI Terminal (Avalonia .NET 8 desktop)
**Goal:** Declutter the Portfolio page by splitting its one long, crowded two-column layout into four sub-tabs inside the page. Pure layout reorganization — no view-model or binding changes.

## Problem

`Views/PortfolioView.axaml` is a 2-column grid where the left "Wallet Hub" column stacks license activation, the Connection Studio (provider buttons, network, address, private key, connect buttons), Solana self-test, Tron self-test, Tron TRC20 payments form, security notes, saved wallets, session status, and two diagnostic traces — all in one tall column. The right column mixes session KPIs, the Portfolio Lens asset cards, the large Portfolio Rebalancer table, and the activity feed. Everything is visible at once → cluttered.

## Decision (locked)

A `TabControl` inside `PortfolioView` with four `TabItem`s. Existing content blocks are MOVED verbatim into the tabs (same XAML, same bindings). No `MainWindowViewModel` / sub-VM changes.

## Tab breakdown

1. **Обзор (Overview)** — default tab, the at-a-glance portfolio:
   - KPI row: "Active Session" card + "Live Route" card (current right-column Row 0 blocks).
   - "Portfolio Lens" asset cards (current right-column Row 1 block).
   - "Wallet Capability" (from the current right-column Row 3, left half).

2. **Ребаланс (Rebalance)** — the entire Portfolio Rebalancer block verbatim (current right-column Row 2: total value badge, drift threshold, AI target weights, column headers, allocation rows, add-asset row, status bar).

3. **Кошелёк (Wallet)** — wallet setup:
   - License activation block.
   - Connection Studio expander (provider / network / address / private key / connect buttons).
   - Saved Wallets expander, Security Notes expander, Session Status.

4. **Активность / Диагностика (Activity / Diagnostics)** — advanced/diagnostic:
   - Recent Activity (Session Feed) (current right-column Row 3, right half).
   - Solana self-test + Solana Diagnostic Trace.
   - Tron self-test + Tron Diagnostic Trace + Tron TRC20 Payments (these already carry `IsVisible="{Binding WalletVM.IsTronNetworkSelected}"`; keep that).

## Architecture

- Root of `PortfolioView` becomes `<TabControl>` (inside the existing outer `Border Classes="Panel"`, or replacing it) with four `<TabItem Header="…">`.
- Each `TabItem` content is a `ScrollViewer Classes="PageScroll"` wrapping the moved blocks in a `StackPanel Spacing="16"`, so each tab scrolls independently and stays tidy.
- Multi-column sub-layouts within a tab (e.g. Overview's two KPI cards side by side, Portfolio Lens card wrap) are preserved with the same `Grid`/`WrapPanel` they use today.
- The Overview tab is the first `TabItem` (selected by default).
- No `x:Name`/code-behind needed; `TabControl` manages selection internally. `x:DataType="vm:MainWindowViewModel"` and `x:CompileBindings="False"` stay on the root so all existing bindings resolve unchanged.

## What does NOT change

- No `MainWindowViewModel`, `WalletWorkspaceViewModel`, `PortfolioRebalanceViewModel`, or `LicenseViewModel` edits.
- No binding paths change — every `{Binding …}` is carried over exactly as-is, including the `$parent[Window].DataContext.…` command bindings inside item templates.
- No behavior change (self-tests, payments, rebalancer all work as before, just relocated).

## Error handling

- N/A (no logic). The only risk is XAML correctness (a block placed under the wrong tag, a dropped binding). Mitigated by building and a visual smoke test of each tab.

## Testing

- Build must be 0 errors.
- Manual smoke: launch, open Portfolio, click each of the four tabs; confirm Overview shows balances/KPIs, Rebalance shows the table, Wallet shows connect/license, Activity shows feed + diagnostics; confirm Tron-only sections still appear only when a Tron network is selected.
- No unit tests (pure view markup).

## Files

- `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml` — the only file changed.
