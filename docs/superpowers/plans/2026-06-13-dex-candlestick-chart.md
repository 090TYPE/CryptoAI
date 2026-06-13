# DEX Candlestick Chart Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the line-based DEX price chart on the Trading page with the existing candlestick chart control, giving candles + MA/Bollinger + VWAP + volume profile + pan/zoom for free.

**Architecture:** Single XAML control swap in `TradingDeskView.axaml`: `DexPriceChart` → `CexCandlestickChart`, bound to the same `DexTradingVM.ChartCandles` (`ObservableCollection<DexOhlcvPoint>`). The candlestick control already accepts that type and is already used for CEX on the same page; its defaults (`ShowVwap=true`, `ShowVolumeProfile=true`, `ToolMode="Cursor"`) provide indicators and pan/zoom with no extra wiring.

**Tech Stack:** Avalonia XAML (compiled at build time) / .NET 8.

---

## File Structure

- **Modify** `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml` — swap the DEX chart control (~lines 1283–1288).

No new files. No view-model changes (timeframe buttons already drive `DexTradingVM.SelectChartRangeCommand` which rebuilds `ChartCandles`). No unit tests — this is a presentation-only control swap; Avalonia compiles XAML at build time so binding/markup errors surface in the build, and behaviour is confirmed by a smoke run.

Reference facts (verified against current code):
- Current usage `TradingDeskView.axaml:1285-1287`:
  ```xml
  <ctrl:DexPriceChart Height="320"
                      Candles="{Binding DexTradingVM.ChartCandles}"
                      ShowVwap="True" />
  ```
- `CexCandlestickChart` (`Controls/CexCandlestickChart.cs`) properties: `Candles` (`IReadOnlyList<DexOhlcvPoint>`), `ShowVwap` (default `true`), `ShowVolumeProfile` (default `true`), `ToolMode` (default `"Cursor"`).
- `DexTradingVM.ChartCandles` is `ObservableCollection<DexOhlcvPoint>`.
- `CexCandlestickChart` is already referenced in this file (line 589) under the same `ctrl:` namespace prefix, so no new xmlns is needed.

---

## Task 1: Swap DEX chart control to candlesticks

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml:1283-1288`

- [ ] **Step 1: Replace the control**

Find this block (the comment `<!-- Candlestick chart (same host styling as CEX) -->` precedes it):

```xml
            <!-- Candlestick chart (same host styling as CEX) -->
            <Border Classes="SoftPanel,ChartCanvasHost" Padding="8">
              <ctrl:DexPriceChart Height="320"
                                  Candles="{Binding DexTradingVM.ChartCandles}"
                                  ShowVwap="True" />
            </Border>
```

Replace it with:

```xml
            <!-- Candlestick chart (same control as CEX) -->
            <Border Classes="SoftPanel,ChartCanvasHost" Padding="8">
              <ctrl:CexCandlestickChart Height="320"
                                        Candles="{Binding DexTradingVM.ChartCandles}" />
            </Border>
```

- [ ] **Step 2: Build to verify XAML compiles**

Make sure the app is not running first (it locks the win-x64 DLLs):

```
Get-Process | Where-Object { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -ErrorAction SilentlyContinue
```

Then:

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug -v minimal`
Expected: `Ошибок: 0` (Build succeeded, 0 errors). Avalonia compiles XAML at build time, so a bad binding or unknown control would fail here.

- [ ] **Step 3: Smoke-run the app**

Launch the built exe:
`CryptoAITerminal.TerminalUI/bin/Debug/net8.0-windows/win-x64/CryptoAITerminal.TerminalUI.exe`

Manual check (demo mode, no keys):
1. Trading page → Venue = **DEX**.
2. Select a token in the DEX Market list.
3. The center DEX chart now renders **candlesticks** (green/red bars) instead of a single line, with VWAP/MA overlay and a volume profile on the right edge.
4. Click timeframe buttons **15M / 1H / 4H / 1D / 1W** → the candles rebuild for the selected range.
5. Mouse-wheel over the chart zooms; drag pans the history.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml
git commit -m "feat: candlestick DEX chart (reuse CexCandlestickChart)"
```

---

## Self-Review (completed during authoring)

- **Spec coverage:** "replace line chart with candlestick control" → Task 1 Step 1; "candles + MA + VWAP + volume profile + pan/zoom via defaults" → relies on `CexCandlestickChart` defaults (verified); "timeframe buttons unchanged" → no VM edits; "no drawing tools / no PersistenceKey" → omitted from the new markup; "don't delete DexPriceChart / don't touch MainWindow dead copies" → not in scope, untouched. Covered.
- **Placeholder scan:** none — the single edit shows exact before/after markup and exact build/run commands.
- **Type consistency:** `CexCandlestickChart.Candles` (`IReadOnlyList<DexOhlcvPoint>`) ← `DexTradingVM.ChartCandles` (`ObservableCollection<DexOhlcvPoint>`), assignable. `ctrl:` namespace already in use in this file. Consistent.
