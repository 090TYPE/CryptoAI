# Auto-Update via Velopack — Design

**Date:** 2026-06-30
**Status:** Approved (design), pending implementation plan
**App:** CryptoAI Terminal (`CryptoAITerminal.TerminalUI`, .NET 8 / Avalonia, self-contained win-x64)

## Problem

Today every release is shipped as a self-contained `dotnet publish` folder zipped via
`build-release.ps1`. To update, the end user must manually: go to GitHub Releases,
download the whole zip (hundreds of MB, includes the .NET runtime), close the app,
and replace the folder by hand. The existing `UpdateCheckService` only *detects* a
newer GitHub release and points the user at the releases page — it does not install
anything.

**Goal:** a user downloads the app once; from then on it updates itself
automatically. No manual re-download, no folder swapping.

## Decisions

- **Tool:** Velopack (actively maintained successor to Squirrel, by the same author).
  Chosen over a custom updater (fragile: full re-download every time, hand-rolled file-lock
  / partial-download / rollback handling) and over Clowd.Squirrel (superseded by Velopack).
- **UX:** user-initiated apply. The app checks silently; when an update exists it shows an
  unobtrusive prompt with an **"Обновить сейчас"** button. Nothing downloads until the user
  clicks. After download it applies and restarts automatically. (Not silent auto-apply —
  this is a trading app, we must not restart mid-trade.)
- **Install model:** users move from "unzip a portable folder" to running `Setup.exe`
  (installs to `%LocalAppData%\CryptoAITerminal`). One-time migration for existing users;
  after that all updates are automatic.
- **Source:** existing GitHub Releases on `090TYPE/CryptoAI` (already referenced by
  `AppInfo.RepoSlug`).
- **Upload:** default flow is local build + manual asset upload. `build-release.ps1` also
  gains an optional `vpk upload github` step behind a flag/token for one-command publishing.

## Components

### 1. Packaging — `build-release.ps1`
- Keep the existing `dotnet publish` step (self-contained, win-x64, `PublishSingleFile=false`
  — Velopack requires a non-single-file publish dir).
- Replace the `Compress-Archive` tail with a `vpk pack` invocation:
  - `--packId CryptoAITerminal`, `--packVersion <from csproj <Version>>`,
    `--packDir <publish dir>`, `--mainExe CryptoAITerminal.TerminalUI.exe`.
  - Produces: `Setup.exe`, full `.nupkg`, **delta `.nupkg`** (diff vs previous release),
    and the `RELEASES` manifest, into a `release/` output dir.
- Version remains single-sourced from `<Version>` in
  `CryptoAITerminal.TerminalUI.csproj` (currently `1.6.0`).
- Optional `-PublishToGithub` flag → `vpk upload github` using a GitHub token from the
  environment.

### 2. Distribution — GitHub Releases
- Velopack artifacts uploaded as release assets (manually by default, or via the optional
  upload flag).
- App reads them through `new GithubSource("https://github.com/090TYPE/CryptoAI", null, false)`.

### 3. App code
- **`Program.cs` `Main`:** first line, before any Avalonia startup —
  `VelopackApp.Build().Run();`. Required hook so Velopack can handle the short-lived
  install/update helper invocations.
- **New `AppUpdateService`** wrapping `UpdateManager(new GithubSource(...))`:
  - `IsSupported` → `UpdateManager.IsInstalled` (false in debug / unpacked runs).
  - `CheckAsync()` → `CheckForUpdatesAsync()` returns update info or null.
  - `DownloadAsync(IProgress<int>)` → `DownloadUpdatesAsync(...)`.
  - `ApplyAndRestart()` → `ApplyUpdatesAndRestart(...)`.
  - All network/IO failures are non-fatal (mirrors current `UpdateCheckService` contract:
    log, never throw into the UI).
- **Remove** `UpdateCheckService` (manual GitHub API parsing) — Velopack supersedes it.
  Keep `AppInfo.Version` for display of the running version.

### 4. UI flow
- On startup, and every N hours, call `CheckAsync()` silently. No download.
- If an update is available and `IsSupported`, show an unobtrusive banner/tray indicator:
  "Доступна версия X.Y. [Обновить сейчас]".
- On click → `DownloadAsync` with a progress bar → on completion `ApplyAndRestart()`.
- If `IsSupported == false` (debug build / unpacked folder), hide the update UI entirely.

### 5. Migration
- Existing portable-folder users download the new `Setup.exe` once → installs to
  `%LocalAppData%\CryptoAITerminal`. Last manual download; everything after is automatic.
- Add a one-line migration note to README / the release notes.

## Out of scope (YAGNI)
- Silent background auto-restart (user chose the button flow).
- Code signing / certificates (Velopack supports it; can be added later).
- CI / GitHub Actions changes — build stays local via `build-release.ps1`.

## Error handling
- Update checks and downloads never throw into the UI; on failure the app simply continues
  on the current version and logs the error.
- `ApplyUpdatesAndRestart` is only reached after a successful, verified download (Velopack
  verifies package integrity); a failed download leaves the installed version untouched.

## Testing
- Unit-test `AppUpdateService` against a faked update source: check / no-update / download
  progress / error-is-swallowed paths (no real network, no real restart).
- Manual end-to-end: build v1.6.0 → install via `Setup.exe` → build v1.6.1 → upload →
  confirm the running app detects it, the button downloads the delta, and it restarts on
  v1.6.1.
