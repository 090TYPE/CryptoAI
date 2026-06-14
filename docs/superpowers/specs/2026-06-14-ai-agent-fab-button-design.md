# Always-visible AI button (FAB) — design

> Small additive UI feature. Gives the global AI omnibox (`Ctrl+K`) a permanent,
> mouse-reachable entry point so it's always at hand without the keyboard shortcut.

## Goal
A floating action button (FAB), visible in every section, that opens the existing
global AI command bar (the `Ctrl+K` omnibox: section navigation + AI copilot questions).

## What already exists (reuse, don't rebuild)
- `MainWindowViewModel.OpenCommandPaletteCommand` — opens the omnibox (resets input,
  sets `IsCommandPaletteOpen = true`). Already bound to the `Ctrl+K` `KeyBinding`
  (`MainWindow.axaml` line 30).
- `IsCommandPaletteOpen` (bool, bindable) — drives the omnibox overlay (`MainWindow.axaml`
  ~1140, ZIndex 100) and the focus of `CommandPaletteBox`.
- Overlay z-order in the root `<Grid>`: splash `SplashOverlay` ZIndex 300; license overlay 220;
  omnibox + alert-toast + update-banner ZIndex 100; license chip 90; placeholder section 5.
- Alert toast: anchored bottom-right, `Margin="0,0,24,32"`, ZIndex 100 (transient).
- Update banner: anchored bottom-left (no conflict with a bottom-right FAB).

## Design

### Placement & layering
- Add the FAB as a **sibling overlay** inside the root `<Grid>` of `MainWindow.axaml`
  (same level as the toasts/overlays), `HorizontalAlignment="Right" VerticalAlignment="Bottom"`,
  `Margin="0,0,24,28"`.
- **ZIndex 95** — above page content, **below** the omnibox overlay (100), license (220) and
  splash (300). Result: while the omnibox / license modal / splash are up, the FAB is covered.
- To guarantee no visual overlap with the bottom-right alert toast, raise the **alert toast's
  bottom margin** so it always sits above the FAB (e.g. `Margin="0,0,24,96"`). Update banner
  (bottom-left) is untouched.

### Appearance
- Circular `Button`, ~52×52, `CornerRadius=26`, accent background `#21E6C1`, dark glyph `#06121C`,
  subtle shadow. Glyph: `🧠` (matches the app's AI iconography — "каждая кнопка помечена 🧠").
- `ToolTip.Tip` = "Ask AI · Ctrl+K" (English source; RU via the localization dictionary →
  "Спросить AI · Ctrl+K"). The button has no text content, so it isn't affected by the
  text-scanner; the tooltip is picked up by the existing `ToolTip.Tip` scan added in Task 1.
- A `Classes="AiFab"` style block in `App.axaml`/`AppStyles.axaml` for hover/pressed states
  (consistent with existing button styles), or inline if simpler.

### Behaviour
- `Command="{Binding OpenCommandPaletteCommand}"` — identical to pressing `Ctrl+K`.
- `IsVisible="{Binding !IsCommandPaletteOpen}"` — hidden the moment the omnibox opens (avoids the
  FAB faintly showing through the semi-transparent omnibox scrim). The splash overlay (opaque,
  ZIndex 300) already occludes the FAB during boot, so no extra splash binding is required.

## Out of scope (YAGNI)
- No drag-to-reposition, no badge/notifications, no second action menu.
- No VM logic changes beyond reusing the existing command/flag (no new properties unless a
  binding turns out to need one).

## Verification
- Build `CryptoAITerminal.TerminalUI` → 0 errors (Avalonia compiles the XAML).
- Launch smoke: FAB visible bottom-right on Dashboard and after switching sections; click opens
  the omnibox and focuses input; `Ctrl+K` still works; alert toast (if fired) sits above the FAB;
  FAB hidden while omnibox open and during splash.
- Toggle RU/EN: tooltip switches language.
- No unit tests (pure XAML; `OpenCommandPaletteCommand` already covered by existing tests).

## Files
- `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml` — add FAB overlay; bump alert-toast bottom margin.
- `CryptoAITerminal.TerminalUI/Styles/AppStyles.axaml` (or `App.axaml`) — optional `AiFab` style.
- `CryptoAITerminal.TerminalUI/Services/UiLocalizationService.cs` — add EN→RU dict entry for the tooltip.
