# Velopack Auto-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace manual zip-download updates with a user-initiated Velopack auto-updater: the user clicks "Обновить сейчас", the app downloads only the delta from GitHub Releases and restarts on the new version.

**Architecture:** Add the `Velopack` NuGet package to `CryptoAITerminal.TerminalUI`. Initialize Velopack's lifecycle hook at the very top of `Main`. Wrap `Velopack.UpdateManager` in a thin, fully-faked-testable `AppUpdateService` (interface + GitHub-backed implementation). Rewire the existing update banner in `MainWindowViewModel` from "open browser" to "download + apply + restart", with a progress state. Replace the zip step in `build-release.ps1` with `vpk pack`. Delete the old `UpdateCheckService`.

**Tech Stack:** .NET 8, Avalonia 12, ReactiveUI, Velopack (`Velopack` NuGet), xUnit, PowerShell build script.

---

## File Structure

- **Create** `CryptoAITerminal.TerminalUI/Services/IAppUpdateService.cs` — update abstraction (interface + result/status types). Testable seam; no Velopack types leak through it except as primitives.
- **Create** `CryptoAITerminal.TerminalUI/Services/VelopackUpdateService.cs` — real implementation over `Velopack.UpdateManager` + `Velopack.Sources.GithubSource`.
- **Create** `CryptoAITerminal.Core.Tests/AppUpdateServiceTests.cs` — tests for the VM-facing behavior using a fake `IAppUpdateService`.
- **Modify** `CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj` — add `Velopack` package reference.
- **Modify** `CryptoAITerminal.TerminalUI/Program.cs` — add `VelopackApp.Build().Run()` as the first line of `Main`.
- **Modify** `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` — replace `StartUpdateCheck`/`OpenUpdateUrl` browser flow with the Velopack download+apply flow; add progress + "supported" state; inject `IAppUpdateService`.
- **Modify** `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml` — relabel the banner button to "Обновить сейчас", bind to the new command, add a progress line.
- **Delete** `CryptoAITerminal.TerminalUI/Services/UpdateCheckService.cs` — superseded by Velopack (keep `AppInfo` by moving it; see Task 7).
- **Modify** `build-release.ps1` — replace `Compress-Archive` tail with `vpk pack`, add optional `-PublishToGithub`.

**Note on `AppInfo`:** `UpdateCheckService.cs` currently also defines the `public static class AppInfo` (Version + RepoSlug). Task 1 moves `AppInfo` into its own file before anything else, so deleting `UpdateCheckService.cs` later does not remove it.

---

## Task 1: Extract `AppInfo` into its own file

Moving `AppInfo` out first means later tasks can delete `UpdateCheckService.cs` cleanly. `AppInfo.Version` is still used for display.

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/AppInfo.cs`
- Modify: `CryptoAITerminal.TerminalUI/Services/UpdateCheckService.cs:9-15` (remove the moved class)

- [ ] **Step 1: Create `AppInfo.cs` with the existing content**

```csharp
namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Current app identity — single source of truth for version + repo.</summary>
public static class AppInfo
{
    public const string Version  = "1.6.0";
    public const string RepoSlug = "090TYPE/CryptoAI";
    public static string ReleasesUrl => $"https://github.com/{RepoSlug}/releases";
    public static string RepoUrl     => $"https://github.com/{RepoSlug}";
}
```

- [ ] **Step 2: Remove the `AppInfo` class from `UpdateCheckService.cs`**

Delete lines 9-15 (the `/// <summary>Current app identity...` block through the closing `}` of `AppInfo`). Leave the `using` directives and the rest of the file untouched.

- [ ] **Step 3: Build to verify no duplicate / missing symbol**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded (no CS0101 duplicate-definition, no CS0103 missing `AppInfo`).

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/AppInfo.cs CryptoAITerminal.TerminalUI/Services/UpdateCheckService.cs
git commit -m "refactor: move AppInfo into its own file"
```

---

## Task 2: Add the `IAppUpdateService` abstraction

The seam the ViewModel talks to. No Velopack types in the signature → fully fakeable in tests with no network and no real restart.

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/IAppUpdateService.cs`

- [ ] **Step 1: Create the interface and supporting types**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Outcome of a check for a newer release.</summary>
/// <param name="IsUpdateAvailable">True when a newer version is ready to download.</param>
/// <param name="CurrentVersion">The version currently running.</param>
/// <param name="LatestVersion">The newer version, or the current one when none.</param>
public sealed record AppUpdateInfo(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion);

/// <summary>
/// Self-update abstraction over the packaging tool. All methods are failure-tolerant:
/// network/IO errors surface as a non-available result or a false return, never an
/// exception into the UI. Implementations that are not installed via the updater
/// (debug runs, unpacked folders) report <see cref="IsSupported"/> = false.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>False in debug / unpacked runs where self-update is impossible.</summary>
    bool IsSupported { get; }

    /// <summary>Checks the release feed. Never throws; returns not-available on error.</summary>
    Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the pending update (delta when possible), reporting 0-100 progress.
    /// Returns true when a downloaded update is staged and ready to apply.
    /// </summary>
    Task<bool> DownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Applies the staged update and restarts the app. Only call after
    /// <see cref="DownloadAsync"/> returned true. Does not return on success.
    /// </summary>
    void ApplyAndRestart();
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/IAppUpdateService.cs
git commit -m "feat: add IAppUpdateService abstraction"
```

---

## Task 3: Add the Velopack package reference

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj:31` (inside the first `<ItemGroup>` of `PackageReference`s)

- [ ] **Step 1: Add the package reference**

Add this line in the `<ItemGroup>` that contains the other `PackageReference` entries (right after the `WTelegramClient` line at `:31`):

```xml
    <PackageReference Include="Velopack" Version="0.0.1053" />
```

- [ ] **Step 2: Restore and build**

Run: `dotnet restore CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj`
Then: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Restore + Build succeeded (Velopack resolves; if `0.0.1053` is unavailable in the feed, run `dotnet add CryptoAITerminal.TerminalUI package Velopack` to pin the latest stable and use that version number instead).

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj
git commit -m "build: add Velopack package reference"
```

---

## Task 4: Initialize Velopack in `Main`

`VelopackApp.Build().Run()` MUST run before Avalonia starts — Velopack relaunches the exe with hook arguments during install/update, and this call services and exits those launches.

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Program.cs:8-12`

- [ ] **Step 1: Add the Velopack hook as the first statement in `Main`**

Replace the body of `Main`:

```csharp
    public static void Main(string[] args)
    {
        // Must be the very first thing: services Velopack install/update hook launches.
        Velopack.VelopackApp.Build().Run();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Run the app once to confirm normal startup is unaffected**

Run: `dotnet run --project CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: The app window opens as before (Velopack no-ops when not launched with hook args). Close it.

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Program.cs
git commit -m "feat: initialize Velopack lifecycle in Main"
```

---

## Task 5: Implement `VelopackUpdateService`

The real implementation. Keeps the failure-tolerant contract: every async path catches and degrades to "no update" / `false`.

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/VelopackUpdateService.cs`

- [ ] **Step 1: Create the implementation**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// <see cref="IAppUpdateService"/> backed by Velopack reading GitHub Releases.
/// Construction is cheap and never touches the network. <see cref="IsSupported"/>
/// is false unless the app was installed via the Velopack Setup.exe.
/// </summary>
public sealed class VelopackUpdateService : IAppUpdateService
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateService()
    {
        var source = new GithubSource(AppInfo.RepoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(source);
    }

    public bool IsSupported => _manager.IsInstalled;

    public async Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var current = AppInfo.Version;
        if (!IsSupported)
            return new AppUpdateInfo(false, current, current);

        try
        {
            _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null)
                return new AppUpdateInfo(false, current, current);

            var latest = _pending.TargetFullRelease.Version.ToString();
            return new AppUpdateInfo(true, current, latest);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Non-fatal: stay on the current version, surface nothing to the UI.
            return new AppUpdateInfo(false, current, current);
        }
    }

    public async Task<bool> DownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!IsSupported || _pending is null) return false;
        try
        {
            await _manager.DownloadUpdatesAsync(
                _pending, p => progress?.Report(p)).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return false;
        }
    }

    public void ApplyAndRestart()
    {
        if (!IsSupported || _pending is null) return;
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
```

- [ ] **Step 2: Build to verify the Velopack API names resolve**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded. If any member name differs in the pinned Velopack version (e.g. `TargetFullRelease.Version`), the compiler error names the type — adjust to the equivalent member and rebuild.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/VelopackUpdateService.cs
git commit -m "feat: add VelopackUpdateService"
```

---

## Task 6: Rewire the update banner in `MainWindowViewModel`

Swap the browser-open flow for download + apply, behind the existing banner. Add an injected service (defaulting to the real one), a progress string, and an "is supported" gate. Keep the existing `IsUpdateAvailable` / `UpdateBannerText` / `DismissUpdateCommand` wiring.

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` — constructor (`:1129`, `:1248-1249`), command props (`:1692-1693`), banner region (`:3396-3438`)

- [ ] **Step 1: Add an injected field for the service**

Near the other private fields used by the update banner (just above `private bool _isUpdateAvailable;` at `:3397`), add:

```csharp
    private readonly IAppUpdateService _updateService;
```

In the constructor, accept it with a default so existing call sites need no change. Find the constructor signature (the one that calls `StartUpdateCheck();` at `:1129`) and add this optional parameter to its parameter list:

```csharp
        IAppUpdateService? updateService = null,
```

Then, before the `StartUpdateCheck();` call at `:1129`, assign it:

```csharp
        _updateService = updateService ?? new VelopackUpdateService();
```

- [ ] **Step 2: Add progress + supported state properties**

Next to `UpdateBannerText` (`:3407-3411`), add:

```csharp
    private string _updateProgressText = "";
    public string UpdateProgressText
    {
        get => _updateProgressText;
        private set => this.RaiseAndSetIfChanged(ref _updateProgressText, value);
    }

    private bool _isUpdateDownloading;
    public bool IsUpdateDownloading
    {
        get => _isUpdateDownloading;
        private set => this.RaiseAndSetIfChanged(ref _isUpdateDownloading, value);
    }
```

- [ ] **Step 3: Replace `StartUpdateCheck` body to use the service and gate on support**

Replace the `StartUpdateCheck` method (`:3413-3424`) with:

```csharp
    private void StartUpdateCheck()
    {
        if (!_updateService.IsSupported) return; // debug / unpacked: no self-update UI
        RunLoggedAsync(async () =>
        {
            var result = await _updateService.CheckAsync().ConfigureAwait(true);
            if (!result.IsUpdateAvailable) return;

            UpdateBannerText  = $"Доступна версия v{result.LatestVersion} (у вас v{result.CurrentVersion})";
            IsUpdateAvailable = true;
        }, "Update check");
    }
```

- [ ] **Step 4: Replace `OpenUpdateUrl` with the download+apply flow**

Replace the `OpenUpdateUrl` method (`:3426-3436`) and remove the now-unused `_updateUrl` field (`:3399`). New method:

```csharp
    private void StartUpdateDownload()
    {
        if (IsUpdateDownloading) return;
        IsUpdateDownloading = true;
        UpdateProgressText  = "Загрузка… 0%";
        var progress = new Progress<int>(p => UpdateProgressText = $"Загрузка… {p}%");
        RunLoggedAsync(async () =>
        {
            var ok = await _updateService.DownloadAsync(progress).ConfigureAwait(true);
            if (!ok)
            {
                IsUpdateDownloading = false;
                UpdateProgressText  = "Не удалось загрузить обновление. Попробуйте позже.";
                return;
            }
            UpdateProgressText = "Перезапуск…";
            _updateService.ApplyAndRestart(); // does not return on success
        }, "Update download");
    }
```

Delete the field declaration `private string _updateUrl = "";` at `:3399`.

- [ ] **Step 5: Rename the command and point it at the new handler**

At `:1248`, change the command creation from `OpenUpdateUrl` to the new handler and rename the command to `InstallUpdateCommand`:

```csharp
        InstallUpdateCommand = ReactiveCommand.Create(StartUpdateDownload, outputScheduler: App.UiScheduler);
        DismissUpdateCommand = ReactiveCommand.Create(DismissUpdateBanner, outputScheduler: App.UiScheduler);
```

At `:1692`, change the property declaration:

```csharp
    public ReactiveCommand<Unit, Unit> InstallUpdateCommand { get; }
```

(Leave `DismissUpdateCommand` at `:1693` and `DismissUpdateBanner` at `:3438` unchanged.)

- [ ] **Step 6: Build to verify the VM compiles**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded. (Expect a binding error from XAML only at runtime until Task 8 updates the view — the C# build itself should pass; the old `OpenUpdateCommand` binding is fixed in Task 8.)

- [ ] **Step 7: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: wire update banner to Velopack download+apply"
```

---

## Task 7: Delete the obsolete `UpdateCheckService`

`AppInfo` already lives in its own file (Task 1), so the type is fully unused now.

**Files:**
- Delete: `CryptoAITerminal.TerminalUI/Services/UpdateCheckService.cs`

- [ ] **Step 1: Confirm no remaining references**

Run: `git grep -n "UpdateCheckService\|UpdateCheckResult"`
Expected: no matches (Task 6 removed the only usage in `MainWindowViewModel`).

- [ ] **Step 2: Delete the file**

```bash
git rm CryptoAITerminal.TerminalUI/Services/UpdateCheckService.cs
```

- [ ] **Step 3: Build to verify nothing broke**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git commit -m "refactor: remove obsolete UpdateCheckService (superseded by Velopack)"
```

---

## Task 8: Update the banner UI in `MainWindow.axaml`

Relabel to "Обновить сейчас", bind to `InstallUpdateCommand`, show progress, and disable the button mid-download.

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml:1041-1050`

- [ ] **Step 1: Replace the banner's inner `StackPanel` content**

Replace lines `1041-1050` (the `<StackPanel Spacing="8"> … </StackPanel>` block inside the update `Border`) with:

```xml
      <StackPanel Spacing="8">
        <TextBlock Text="⬆ Доступно обновление" Foreground="#1FE6C2" FontSize="11" FontWeight="SemiBold" />
        <TextBlock Text="{Binding UpdateBannerText}" Foreground="#E0E8F0" TextWrapping="Wrap" FontSize="13" />
        <TextBlock Text="{Binding UpdateProgressText}" Foreground="#9FB2C4" FontSize="12"
                   IsVisible="{Binding IsUpdateDownloading}" />
        <StackPanel Orientation="Horizontal" Spacing="10" HorizontalAlignment="Right">
          <Button Content="Позже" Command="{Binding DismissUpdateCommand}"
                  IsEnabled="{Binding !IsUpdateDownloading}"
                  Padding="12,5" Background="Transparent" BorderBrush="#2C4257" BorderThickness="1" Foreground="#C5D2DE" />
          <Button Content="Обновить сейчас" Command="{Binding InstallUpdateCommand}"
                  IsEnabled="{Binding !IsUpdateDownloading}"
                  Padding="14,5" Background="#1FE6C2" Foreground="#06121C" FontWeight="Bold" />
        </StackPanel>
      </StackPanel>
```

- [ ] **Step 2: Build to verify XAML compiles and bindings resolve**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded (compiled bindings are on by default — a typo'd binding name fails the build here).

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/MainWindow.axaml
git commit -m "feat: update banner UI for in-app install"
```

---

## Task 9: Tests for the update flow

Drive the VM-facing contract through a fake `IAppUpdateService` — no network, no real restart. Tests live in the existing test project, which already references `TerminalUI` and uses xUnit.

**Files:**
- Create: `CryptoAITerminal.Core.Tests/AppUpdateServiceTests.cs`

- [ ] **Step 1: Write the failing tests with an in-memory fake**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class AppUpdateServiceTests
{
    /// <summary>Configurable fake — records calls, returns scripted results.</summary>
    private sealed class FakeUpdateService : IAppUpdateService
    {
        public bool IsSupported { get; set; } = true;
        public AppUpdateInfo NextCheck { get; set; } = new(false, "1.0.0", "1.0.0");
        public bool DownloadResult { get; set; } = true;
        public bool ThrowOnCheck { get; set; }
        public int ApplyCalls { get; private set; }
        public int[] ReportedProgress = Array.Empty<int>();

        public Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            if (ThrowOnCheck) throw new InvalidOperationException("network down");
            return Task.FromResult(NextCheck);
        }

        public Task<bool> DownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default)
        {
            progress?.Report(50);
            progress?.Report(100);
            ReportedProgress = new[] { 50, 100 };
            return Task.FromResult(DownloadResult);
        }

        public void ApplyAndRestart() => ApplyCalls++;
    }

    [Fact]
    public async Task Check_returns_available_when_newer_version_exists()
    {
        var svc = new FakeUpdateService { NextCheck = new(true, "1.6.0", "1.7.0") };
        var result = await svc.CheckAsync();
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.7.0", result.LatestVersion);
    }

    [Fact]
    public async Task Download_reports_progress_and_succeeds()
    {
        var svc = new FakeUpdateService { DownloadResult = true };
        var seen = new System.Collections.Generic.List<int>();
        var ok = await svc.DownloadAsync(new Progress<int>(p => seen.Add(p)));
        Assert.True(ok);
        Assert.Equal(new[] { 50, 100 }, svc.ReportedProgress);
    }

    [Fact]
    public async Task Download_failure_does_not_apply()
    {
        var svc = new FakeUpdateService { DownloadResult = false };
        var ok = await svc.DownloadAsync();
        Assert.False(ok);
        Assert.Equal(0, svc.ApplyCalls);
    }

    [Fact]
    public void Unsupported_service_reports_not_supported()
    {
        var svc = new FakeUpdateService { IsSupported = false };
        Assert.False(svc.IsSupported);
    }

    [Fact]
    public void Real_service_is_not_supported_in_test_host()
    {
        // Not installed via Velopack Setup.exe → must report unsupported, never throw.
        var svc = new VelopackUpdateService();
        Assert.False(svc.IsSupported);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail (before building deps) / pass once compiled**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter "FullyQualifiedName~AppUpdateServiceTests" -v minimal`
Expected: If the production types from Tasks 2 & 5 are in place, all 5 tests PASS. If a type is missing, the test build fails naming it — implement that type, then re-run.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.Core.Tests/AppUpdateServiceTests.cs
git commit -m "test: cover update service contract"
```

---

## Task 10: Switch the release build to `vpk pack`

Replace the zip step with Velopack packaging so releases produce `Setup.exe` + full + delta packages.

**Files:**
- Modify: `build-release.ps1:109-127` (the "Package" section) and the summary section `:129-145`

- [ ] **Step 1: Ensure the `vpk` tool is available (one-time, documented in the script header)**

Add near the top of `build-release.ps1` (after the `param(...)` block), a preflight that installs the tool if missing:

```powershell
# Velopack CLI — installs once if absent.
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing Velopack CLI (vpk)...' -ForegroundColor Yellow
    & dotnet tool install -g vpk
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}
```

- [ ] **Step 2: Add a `-PublishToGithub` switch to the `param` block**

Change the `param(...)` block (`:12-16`) to include the new switch:

```powershell
param(
    [switch] $SkipClean,
    [switch] $SkipZip,
    [string] $Version = (Get-Date -Format 'yyyy-MM-dd'),
    [switch] $PublishToGithub
)
```

- [ ] **Step 3: Replace the "Package" section with `vpk pack`**

Replace the entire `# ── 4. Package ──` block (`:109-127`) with:

```powershell
# ── 4. Pack (Velopack) ─────────────────────────────────────────────────────
if (-not $SkipZip) {
    Write-Step 'Packing release with Velopack (vpk)'

    if (-not (Test-Path $ReleaseDir)) { New-Item -ItemType Directory -Path $ReleaseDir | Out-Null }

    & vpk pack `
        --packId        CryptoAITerminal `
        --packVersion   $Version `
        --packDir       $PublishDir `
        --mainExe       CryptoAITerminal.TerminalUI.exe `
        --packTitle     'CryptoAI Terminal' `
        --outputDir     $ReleaseDir
    Assert-Success 'vpk pack'

    Write-Host "  Velopack output: $ReleaseDir" -ForegroundColor Green

    if ($PublishToGithub) {
        Write-Step 'Uploading release to GitHub (vpk upload github)'
        if (-not $env:GITHUB_TOKEN) {
            Write-Host '[FAIL] GITHUB_TOKEN env var required for -PublishToGithub' -ForegroundColor Red
            exit 1
        }
        & vpk upload github `
            --repoUrl     "https://github.com/090TYPE/CryptoAI" `
            --token       $env:GITHUB_TOKEN `
            --outputDir   $ReleaseDir `
            --tag         "v$Version" `
            --releaseName "v$Version"
        Assert-Success 'vpk upload github'
    }
}
else {
    Write-Step 'Skipping pack (-SkipZip)'
}
```

- [ ] **Step 4: Update the summary section's customer instructions**

Replace the `Customer instructions:` block (`:140-143`) with:

```powershell
    Write-Host '  Customer instructions:' -ForegroundColor Yellow
    Write-Host '    1. Run Setup.exe from the release folder (installs to %LocalAppData%).'
    Write-Host '    2. The app updates itself from then on — no manual downloads.'
    Write-Host '    3. Configure API keys via Settings tab or env vars (see README.md).'
```

Also change the "Version tag" default note: a Velopack version must be SemVer. When invoking for a real release, pass `-Version 1.6.0` (matching the csproj `<Version>`), not the date default. Add this one-line guard right after the `param`/preflight, before `# ── 0. Preflight`:

```powershell
if ($Version -notmatch '^\d+\.\d+\.\d+') {
    Write-Host "[warn] -Version '$Version' is not SemVer; vpk needs e.g. 1.6.0. Pass -Version explicitly for releases." -ForegroundColor Yellow
}
```

- [ ] **Step 5: Smoke-test the pack step locally**

Run: `./build-release.ps1 -Version 1.6.0`
Expected: Publish succeeds, then `vpk pack` produces `release/CryptoAITerminal-1.6.0-full.nupkg` and `release/Setup.exe` (no `-PublishToGithub`, so nothing is uploaded). On a second release with a higher `-Version`, a `-delta.nupkg` also appears.

- [ ] **Step 6: Commit**

```bash
git add build-release.ps1
git commit -m "build: package releases with Velopack instead of zip"
```

---

## Task 11: Document the one-time migration

**Files:**
- Modify: `README.md` (the install/update section — search for the existing extract-and-run instructions)

- [ ] **Step 1: Add a migration note**

Add a short subsection to `README.md` near the existing install instructions:

```markdown
## Updating

Starting with v1.6.0 the app updates itself. Existing users: download `Setup.exe`
from the latest [release](https://github.com/090TYPE/CryptoAI/releases) and run it
once — it installs to `%LocalAppData%\CryptoAITerminal`. After that, when an update
is available the app shows an "Обновить сейчас" button that downloads only the
changed parts and restarts on the new version. No more manual downloads.
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: document Velopack self-update migration"
```

---

## Task 12: Full verification

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build -c Debug`
Expected: Build succeeded across all projects.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj -v minimal`
Expected: All tests pass (existing suite + the 5 new `AppUpdateServiceTests`).

- [ ] **Step 3: Manual end-to-end (documented, run by maintainer)**

1. `./build-release.ps1 -Version 1.6.0` → install via `release/Setup.exe`.
2. Bump csproj `<Version>` to `1.6.1`, rebuild `./build-release.ps1 -Version 1.6.1 -PublishToGithub` (with `GITHUB_TOKEN` set).
3. Launch the installed v1.6.0 app → confirm the banner appears, "Обновить сейчас" downloads the delta with progress, and the app restarts on v1.6.1.

Expected: update detected, delta downloaded, app restarts on the new version.

---

## Self-Review Notes

- **Spec coverage:** Packaging (Task 10), GitHub distribution + upload (Task 10), `VelopackApp.Build().Run()` (Task 4), `AppUpdateService` check/download/apply (Tasks 2, 5), removal of `UpdateCheckService` keeping `AppInfo` (Tasks 1, 7), button UX + progress + `IsSupported` gate (Tasks 6, 8), migration note (Task 11), tests (Task 9). All spec sections map to tasks.
- **Type consistency:** `IAppUpdateService` (`IsSupported`, `CheckAsync`, `DownloadAsync`, `ApplyAndRestart`, `AppUpdateInfo`) is defined in Task 2 and used identically in Tasks 5, 6, 9. Command renamed consistently to `InstallUpdateCommand` in both the VM (Task 6) and the XAML (Task 8). `AppInfo.RepoUrl` is added in Task 1 and consumed in Task 5.
- **Velopack version/API caveat:** the pinned package version (Task 3) and a couple of member names (`TargetFullRelease.Version`, `DownloadUpdatesAsync` progress callback) may differ slightly across Velopack releases; Tasks 3 and 5 include the fallback instruction to adjust to the compiler-named member.
