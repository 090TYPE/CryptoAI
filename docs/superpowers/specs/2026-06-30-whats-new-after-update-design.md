# "What's New" After Update — Design

**Date:** 2026-06-30
**Status:** Approved (design), pending implementation plan
**App:** CryptoAI Terminal (`CryptoAITerminal.TerminalUI`, .NET 8 / Avalonia 12)
**Builds on:** the Velopack auto-update feature (`docs/superpowers/specs/2026-06-30-auto-update-velopack-design.md`) — same branch `feat/auto-update-velopack`.

## Problem

After the app auto-updates and restarts on a new version, the user has no idea what
changed. We want a scrollable, dismissable "What's New" overlay to appear once after an
update, showing the release notes for the now-current version.

**Goal:** on the first launch of a newly-updated version, show the GitHub release notes
for that version in a scrollable overlay with a Close button. Show it once per version,
never on a fresh first-ever install, and never break anything when offline.

## Decisions

- **Content source:** the GitHub Release body (markdown) for tag `v{version}` — the same
  notes shown on github.com (single source of truth). Fetched at runtime via the GitHub
  REST API, reusing `AppInfo.RepoSlug`.
- **Rendering:** `Markdown.Avalonia` (renders headings, lists, bold). One new dependency.
  - **Compatibility risk:** the project targets Avalonia **12.0.0**; `Markdown.Avalonia`
    may be built against 11.x. The implementation plan's FIRST step verifies the package
    restores and renders against Avalonia 12. If it does not, fall back to a dependency-free
    "light parse" (strip `#`, bullet `-`/`*` lines to `•`, otherwise plain text) shown in a
    scrollable `TextBlock`. The rest of the design is unchanged by which renderer is used.
- **Trigger:** auto-show only, after a real update. No manual "What's New" button for now
  (YAGNI — can be added later).
- **Once per version:** tracked by a persisted last-seen version marker.

## When it shows (the gate)

A persisted marker file `%LocalAppData%\CryptoAITerminal\.last-version` stores the version
string the user last ran (mirrors the existing `.welcome-shown` pattern at
`MainWindowViewModel` and `custom-markets.json` paths).

On startup, `WhatsNewGate` decides based on `(lastSeen, current)` where current =
`AppInfo.Version`:

| lastSeen marker | Decision |
|---|---|
| absent (first-ever run) | Do NOT show. Write current. (Welcome overlay greets first run.) |
| present, parses < current | SHOW notes for `current`. Then write current. |
| present, == current | Do nothing. |
| present, > current or unparseable | Do NOT show. Write current. (Fail safe — never nag.) |

The decision is a pure function of two version strings, unit-tested independently of file IO.
The marker is written in all "Do not show / showed" cases so the API is not hit on every
launch. Welcome and What's New never collide: What's New requires a prior version, so it
cannot fire on a first-ever run.

## Components

Each unit is small, single-purpose, and independently testable.

### `Services/WhatsNewGate.cs`
- `static bool ShouldShow(string? lastSeen, string current)` — the pure decision above
  (version comparison reuses the same parse rules as the rest of the app: tolerate leading
  `v`, compare numeric dotted parts; unparseable → false).
- Instance methods to read/write the marker file (`ReadLastSeen()` / `WriteLastSeen(string)`),
  failure-tolerant (IO errors are swallowed; a missing/unreadable marker reads as `null`).

### `Services/IReleaseNotesService.cs` + `ReleaseNotesService.cs`
- `Task<string?> GetNotesAsync(string version, CancellationToken ct = default)` —
  GET `https://api.github.com/repos/{RepoSlug}/releases/tags/v{version}`, parse the JSON
  `body` field, return it (markdown). Returns `null` on any failure (network, non-200,
  missing/empty body, parse error). Never throws into the UI. Mirrors the existing
  `UpdateCheckService`/`VelopackUpdateService` failure-tolerant + `HttpClient` injection
  pattern so it is testable with a fake `HttpMessageHandler`.

### `MainWindowViewModel` additions
- Bindable: `bool IsWhatsNewVisible`, `string WhatsNewMarkdown`, `string WhatsNewVersion`.
- `ReactiveCommand CloseWhatsNewCommand` → sets `IsWhatsNewVisible = false`.
- `StartWhatsNewCheck()` called from the constructor near `StartUpdateCheck()`:
  reads the marker; if `WhatsNewGate.ShouldShow` is true, fetches notes via
  `IReleaseNotesService`; if notes are non-empty, sets `WhatsNewMarkdown` /
  `WhatsNewVersion` and `IsWhatsNewVisible = true`; then writes the marker (always, even
  when notes are null/empty) via `RunLoggedAsync`. Injected `IReleaseNotesService` and
  `WhatsNewGate` default to real implementations (constructor optional params, like
  `IAppUpdateService`).

### `Views/MainWindow.axaml`
- A new overlay `Border` styled like the existing welcome/license overlays: dimmed
  background, centered panel, title "Что нового в v{WhatsNewVersion}", a scrollable
  markdown view (`MarkdownScrollViewer` from Markdown.Avalonia, or a scrollable `TextBlock`
  in the fallback) bound to `WhatsNewMarkdown`, and a "Закрыть" button bound to
  `CloseWhatsNewCommand`. Visibility bound to `IsWhatsNewVisible`. Sensible max size so long
  notes scroll rather than overflow the window.

## Error handling

- No network / API error / no release for the tag / empty body → `GetNotesAsync` returns
  `null` → overlay does not show. The marker is still advanced so the app does not retry
  the API every launch.
- All IO on the marker file is wrapped; failures are non-fatal (worst case: the overlay may
  show again next launch).

## Out of scope (YAGNI)

- Manual "What's New" button / menu entry.
- Caching notes offline for later viewing.
- Showing notes for intermediate skipped versions (only the current version's notes show).

## Testing

- `WhatsNewGate.ShouldShow`: first-run (null) → false; lastSeen `1.6.0` & current `1.6.1`
  → true; equal → false; lastSeen `1.7.0` & current `1.6.1` (downgrade) → false;
  unparseable lastSeen → false.
- `ReleaseNotesService`: with a fake `HttpMessageHandler` — a 200 response whose JSON
  `body` is "## Changes\n- x" returns that string; a 404 returns `null`; a 200 with empty
  `body` returns `null`; a thrown/socket error returns `null`.
- Marker read/write round-trip via a temp directory (or an injected path) — write then read
  returns the same version; reading a missing file returns `null`.
