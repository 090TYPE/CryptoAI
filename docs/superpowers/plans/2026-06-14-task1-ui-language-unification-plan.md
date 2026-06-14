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
- [x] **A. Scanner infra** — added `ToolTip.Tip` (Controls), `Run.Text` (walk `TextBlock.Inlines`),
      and `ToggleSwitch` OnContent/OffContent. (commit 1de650b + f3ef521)
- [x] **B. XAML literals → English + dict** (all 113 occurrences):
  - [x] B1 Smart Order Router  - [x] B2 Market Scanner + Alerts/Price Levels
  - [x] B3 API-key panels ×4 (bare-text TextBlocks restructured to explicit `<Run>`s)
  - [x] B4 Misc: liquidation tooltip, DCA note, scanner sort headers
- [x] **C. VM Russian-only → English + dict** — BestExecution, Backtest (incl. 2 sync-bug fixes),
      AIBot, FundingRate, MarketScanner, MainWindow section metadata. Bilingual ternaries + Russian
      input-matching keywords left untouched.
- [x] **D. Dictionary coverage test** — `UiLocalizationServiceTests` (4 tests). Full suite 294 green.
- [x] **E. Build (0 errors) + launch smoke** — app starts, survives scan ticks, no crash.

## Done — branch `task1-ui-language-unification`
English is now the single source for all authored UI text; RU renders via the dictionary/scanner.

### Remaining optional polish (not blocking; consistent with existing conventions)
- Long placeholder-section descriptions/roadmaps (funding, liquidation, rules, arb, scanner, router,
  journal, gas, positions, news, onchain, copy, statarb) are English source but have no RU dict
  entries → show English under RU (same convention as existing English-only product names like
  "Whale Tracker"). Add RU dict entries if full RU coverage is wanted.
- A few interpolated VM status strings with mid-string dynamic values and transient toasts show
  English under RU (line-match dictionary can't translate them). Minor.

## Verification
- `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug` → 0 errors.
- `dotnet test CryptoAITerminal.Core.Tests` → green.
- Close app before every build: `Get-Process | ? { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -EA SilentlyContinue`
- Smoke: launch, toggle EN/RU on Trading, Smart Router, Market Scanner, API-keys pages — no foreign language remains.

## Gotchas
- Author ONLY English in code; never translate Russian literals in place (no dict key).
- Run-fragment strings normalize whitespace → keep each Run a clean single-line string for deterministic dict match.
- Don't touch bindings/commands — text only.
