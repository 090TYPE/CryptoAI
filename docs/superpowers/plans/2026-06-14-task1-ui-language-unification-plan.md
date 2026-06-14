# Task 1 — UI language unification (RU/EN) — Implementation plan

Branch: `task1-ui-language-unification` (off `main`).
Model (confirmed with user): **English = source of truth, RU rendered at runtime via the
`UiLocalizationService` EN→RU dictionary + the visual-tree scanner in `MainWindow.axaml.cs`.**

## How localization works (verified)
- `Services/UiLocalizationService.cs`: `_englishToRussian` dict (~569 lines, exact per-line match),
  `_prefixTranslations` (prefix match), `Translate()` splits by newline → `TranslateSingleLine()`.
- `Views/MainWindow.axaml.cs`: 2s timer scans visual tree, registers source (English) text and
  applies `Translate()` when RU. **Currently scans only:** `TextBlock.Text`, `Button.Content`,
  `TabItem.Header`, `Expander.Header`, `TextBox.PlaceholderText`.
- **Gaps found:** `ToolTip.Tip` not scanned; `<Run Text="…">` inline fragments not scanned
  (a `TextBlock` with inline children has empty `.Text`). API-key help blocks use Runs → never translated.

## "Mixed language" root causes
1. Russian literal authored in XAML/VM (always Russian; no dict key).
2. English literal not present in dict (always English under RU).
3. Text-bearing control/property not covered by scanner (Runs, ToolTip.Tip).

## Inventory (verified counts)
- `Views/MainWindow.axaml`: 113 translatable Russian attribute/Run occurrences. Clusters:
  - L441 liquidation-heatmap `ToolTip.Tip`
  - L5642 DCA spot-only note
  - L7217–7434 Smart Order Router / Best Execution page
  - L7494–7782 Market Scanner page (+ Alerts / Price Levels)
  - L10211–10678 API-key help panels ×4 exchanges (Binance/Bybit/OKX/Bitget) — `<Run>` fragments,
    `PlaceholderText`, `ToolTip.Tip`, `Content="💾 Сохранить"`
- ViewModels:
  - `MainWindowViewModel.cs`: mostly **already-bilingual** `_localization.IsRussian ? ru : en` —
    LEAVE THESE (they render correctly). Only fix any Russian-only literals.
  - `BestExecutionViewModel.cs` (14), `BacktestViewModel.cs` (44), others: Russian-only status
    strings → English source + dict entries (prefix entries for interpolated `$"Error: {…}"`).

## Phases (build + commit per phase)
- [ ] **A. Scanner infra** — add `ToolTip.Tip` (Controls) + `Run.Text` (walk `TextBlock.Inlines`)
      to `AttachLocalizationObservers` / `ApplyLocalizationToObservedControls`. Build.
- [ ] **B. XAML literals → English + dict**, page by page, build after each:
  - [ ] B1 Smart Order Router (7217–7434)
  - [ ] B2 Market Scanner + Alerts/Price Levels (7494–7782)
  - [ ] B3 API-key panels ×4 (10211–10678) — convert bare-text TextBlocks to explicit `<Run>`s
  - [ ] B4 Misc: L441 tooltip, L5642 DCA note
- [ ] **C. VM Russian-only → English + dict** (BestExecution, Backtest, scan others). Leave bilingual ternaries.
- [ ] **D. Dictionary coverage test** in `CryptoAITerminal.Core.Tests` — no Russian chars leak
      for known keys; round-trip sanity.
- [ ] **E. Build + run + smoke** RU/EN on each touched page; commit.

## Verification
- `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug` → 0 errors.
- `dotnet test CryptoAITerminal.Core.Tests` → green.
- Close app before every build: `Get-Process | ? { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -EA SilentlyContinue`
- Smoke: launch, toggle EN/RU on Trading, Smart Router, Market Scanner, API-keys pages — no foreign language remains.

## Gotchas
- Author ONLY English in code; never translate Russian literals in place (no dict key).
- Run-fragment strings normalize whitespace → keep each Run a clean single-line string for deterministic dict match.
- Don't touch bindings/commands — text only.
