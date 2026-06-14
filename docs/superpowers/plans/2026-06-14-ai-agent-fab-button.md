# Always-visible AI button (FAB) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a floating action button (FAB) that opens the existing global AI omnibox (`Ctrl+K`) from any screen with the mouse.

**Architecture:** Pure additive XAML. A circular `Button` overlay in the root `<Grid>` of `MainWindow.axaml`, bottom-right, bound to the existing `OpenCommandPaletteCommand`; hidden while the omnibox is open. The bottom-right alert toast's margin is raised so it never overlaps the FAB. One EN→RU dictionary entry for the tooltip. No view-model changes, no unit tests (UI-only; the command is already covered).

**Tech Stack:** Avalonia 11 XAML, ReactiveUI (existing command), `UiLocalizationService` dictionary.

---

## File structure
- `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml` — add the FAB overlay; raise the alert-toast bottom margin.
- `CryptoAITerminal.TerminalUI/Services/UiLocalizationService.cs` — add the tooltip EN→RU entry.

No new files. No code-behind. No test files (pure XAML; `OpenCommandPaletteCommand` is covered by existing tests).

---

## Pre-flight
Close any running instance before building:
`Get-Process | Where-Object { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -ErrorAction SilentlyContinue`

---

### Task 1: Floating AI omnibox button

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Services/UiLocalizationService.cs` (dictionary block)
- Modify: `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml` (root `<Grid>` overlays; alert-toast margin)

- [ ] **Step 1: Add the tooltip translation to the dictionary**

In `UiLocalizationService.cs`, add this entry to the `_englishToRussian` initializer (place it next to the other recently-added UI entries, before the closing `};` of the dictionary):

```csharp
        ["Ask AI · Ctrl+K"] = "Спросить AI · Ctrl+K",
```

(Ensure the line above it ends with a comma and you don't introduce a duplicate key — this key is new.)

- [ ] **Step 2: Raise the alert-toast bottom margin so it clears the FAB**

In `MainWindow.axaml`, find the bottom-right **alert toast** `Border` (it contains `<TextBlock Text="Alert Fired" .../>` and `<TextBlock Text="{Binding ToastMessage}" .../>`). Change its margin:

Old:
```xml
            Margin="0,0,24,32"
```
New:
```xml
            Margin="0,0,24,96"
```

If `Margin="0,0,24,32"` is not unique in the file, scope the edit by including the next line `Padding="16,12"` so it matches only the toast Border.

- [ ] **Step 3: Add the FAB overlay**

In `MainWindow.axaml`, insert the following block as a sibling overlay in the root `<Grid>`, immediately **before** the omnibox overlay border (the line `<Border IsVisible="{Binding IsCommandPaletteOpen}"` … `ZIndex="100">`):

```xml
    <!-- Floating "Ask AI" button — opens the global omnibox (same as Ctrl+K) -->
    <Border HorizontalAlignment="Right" VerticalAlignment="Bottom"
            Margin="0,0,24,28" ZIndex="95"
            CornerRadius="26"
            BoxShadow="0 6 20 0 #80000000"
            IsVisible="{Binding !IsCommandPaletteOpen}">
      <Button Command="{Binding OpenCommandPaletteCommand}"
              Width="52" Height="52" CornerRadius="26" Padding="0"
              Background="#21E6C1" Foreground="#06121C"
              FontSize="22" Content="🧠"
              HorizontalContentAlignment="Center" VerticalContentAlignment="Center"
              ToolTip.Tip="Ask AI · Ctrl+K">
        <Button.Styles>
          <Style Selector="Button:pointerover /template/ ContentPresenter">
            <Setter Property="Background" Value="#3FF0D4" />
          </Style>
          <Style Selector="Button:pressed /template/ ContentPresenter">
            <Setter Property="Background" Value="#15C7A8" />
          </Style>
        </Button.Styles>
      </Button>
    </Border>
```

- [ ] **Step 4: Build and verify 0 errors**

Run:
```
dotnet build "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.TerminalUI\CryptoAITerminal.TerminalUI.csproj" -c Debug --nologo -v q
```
Expected: `Сборка успешно завершена.` / `Ошибок: 0`. (Avalonia compiles the XAML; binding/namespace errors would surface here.)

- [ ] **Step 5: Launch smoke**

Run the app:
```
& "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.TerminalUI\bin\Debug\net8.0-windows\win-x64\CryptoAITerminal.TerminalUI.exe"
```
Verify:
- A round 🧠 button is visible bottom-right after the splash, on Dashboard and after switching sections.
- Click it → the omnibox opens with the input focused; the FAB disappears while it's open.
- `Esc` closes the omnibox → the FAB reappears.
- `Ctrl+K` still opens the omnibox.
- Toggle EN/RU (top-right) → hover the FAB → tooltip switches between "Ask AI · Ctrl+K" and "Спросить AI · Ctrl+K".
- If an alert toast fires (or by inspection), it sits above the FAB, not overlapping.
Close the app afterward.

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/MainWindow.axaml CryptoAITerminal.TerminalUI/Services/UiLocalizationService.cs
git commit -m "feat: always-visible floating AI button opens the Ctrl+K omnibox"
```
(Author is the repo user only — do NOT add a Co-Authored-By trailer.)

---

## Self-review notes
- **Spec coverage:** FAB placement/ZIndex (Steps 2–3), opens omnibox via existing command (Step 3), hidden while omnibox open (`IsVisible="{Binding !IsCommandPaletteOpen}"`), splash occlusion (automatic, ZIndex 95 < splash 300), localized tooltip (Steps 1 & 5), toast clearance (Step 2) — all covered.
- **No new VM properties:** reuses `OpenCommandPaletteCommand` and `IsCommandPaletteOpen` (both already exist on `MainWindowViewModel`).
- **No duplicate dict key:** "Ask AI · Ctrl+K" is new.
