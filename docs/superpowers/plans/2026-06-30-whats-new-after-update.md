# "What's New" After-Update Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On the first launch of a newly-updated version, show the GitHub release notes for that version in a scrollable, dismissable overlay — once per version, never on a fresh first install, never breaking when offline.

**Architecture:** A pure decision unit (`WhatsNewGate.ShouldShow`) gated on a persisted last-seen version marker decides whether to show. A failure-tolerant `ReleaseNotesService` fetches the GitHub release body (markdown) for the current version. `MainWindowViewModel` wires these into a new overlay rendered by `Markdown.Avalonia` (with a dependency-free plain-text fallback). All pieces mirror the existing `IAppUpdateService` / welcome-overlay patterns.

**Tech Stack:** .NET 8, Avalonia 12, ReactiveUI, Markdown.Avalonia (new), GitHub REST API, xUnit.

---

## File Structure

- **Create** `CryptoAITerminal.TerminalUI/Services/WhatsNewGate.cs` — pure `ShouldShow` decision + marker file read/write (injectable path).
- **Create** `CryptoAITerminal.TerminalUI/Services/IReleaseNotesService.cs` — fetch abstraction (testable seam).
- **Create** `CryptoAITerminal.TerminalUI/Services/ReleaseNotesService.cs` — GitHub-backed implementation (HttpClient-injectable, failure-tolerant).
- **Create** `CryptoAITerminal.Core.Tests/WhatsNewGateTests.cs` — decision + marker round-trip tests.
- **Create** `CryptoAITerminal.Core.Tests/ReleaseNotesServiceTests.cs` — fake-HttpMessageHandler tests.
- **Modify** `CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj` — add `Markdown.Avalonia` package.
- **Modify** `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` — new properties, command, `StartWhatsNewCheck()`, constructor params.
- **Modify** `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml` — the What's New overlay + markdown namespace.

**Context the implementer needs:**
- The app persists small state files in `%LocalAppData%\CryptoAITerminal\` (e.g. `.welcome-shown`). The welcome marker code at `MainWindowViewModel.cs:3365-3388` is the pattern to mirror.
- `AppInfo` (`Services/AppInfo.cs`) exposes `Version` (e.g. "1.6.0") and `RepoSlug` ("090TYPE/CryptoAI").
- `IAppUpdateService` (`Services/IAppUpdateService.cs`) and the old `UpdateCheckService` git history show the established failure-tolerant + HttpClient-injection + `[Theory]` test conventions.
- The VM constructor is currently `public MainWindowViewModel(IAppUpdateService? updateService = null)`. It calls `StartUpdateCheck();`. Commands are created with `ReactiveCommand.Create(..., outputScheduler: App.UiScheduler)`. `RunLoggedAsync(Func<Task>, string)` runs a logged fire-and-forget async op.
- The test project `CryptoAITerminal.Core.Tests` uses xUnit (`Xunit` globally imported) and references `CryptoAITerminal.TerminalUI`.

---

## Task 1: Add the Markdown.Avalonia package (with Avalonia-12 compatibility gate)

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj`

- [ ] **Step 1: Add the package via CLI (picks latest stable, resolves against Avalonia 12)**

Run: `dotnet add CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj package Markdown.Avalonia`

- [ ] **Step 2: Build and decide**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`

- If build SUCCEEDS → Markdown.Avalonia is compatible. Record the pinned version. Proceed; later tasks use `MarkdownScrollViewer`.
- If restore/build FAILS due to an Avalonia version conflict (e.g. the package demands Avalonia 11.x and won't resolve against 12.0.0) → **remove the package** (`dotnet remove CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj package Markdown.Avalonia`), confirm the build is green again, and report **DONE_WITH_CONCERNS: Markdown.Avalonia incompatible with Avalonia 12 — use the FALLBACK render path in Task 6**. Do NOT force a downgrade of Avalonia.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj
git commit -m "build: add Markdown.Avalonia for release-notes rendering"
```

(If the package was removed as incompatible, commit nothing and skip to Task 2; record the fallback decision in your report.)

---

## Task 2: `WhatsNewGate.ShouldShow` decision logic (TDD)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/WhatsNewGate.cs`
- Test: `CryptoAITerminal.Core.Tests/WhatsNewGateTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `CryptoAITerminal.Core.Tests/WhatsNewGateTests.cs`:
```csharp
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class WhatsNewGateTests
{
    [Theory]
    [InlineData(null, "1.6.1", false)]      // first-ever run: don't show
    [InlineData("", "1.6.1", false)]        // empty marker: don't show
    [InlineData("1.6.0", "1.6.1", true)]    // updated: show
    [InlineData("1.6.0", "1.7.0", true)]    // updated (minor): show
    [InlineData("v1.6.0", "1.6.1", true)]   // leading v tolerated
    [InlineData("1.6.1", "1.6.1", false)]   // same version: don't show
    [InlineData("1.7.0", "1.6.1", false)]   // downgrade: don't show
    [InlineData("garbage", "1.6.1", false)] // unparseable last-seen: don't show
    public void ShouldShow_DecidesByVersionComparison(string? lastSeen, string current, bool expected)
    {
        Assert.Equal(expected, WhatsNewGate.ShouldShow(lastSeen, current));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter "FullyQualifiedName~WhatsNewGateTests" -v minimal`
Expected: FAIL — `WhatsNewGate` does not exist / does not compile.

- [ ] **Step 3: Implement `WhatsNewGate` with the pure decision**

Create `CryptoAITerminal.TerminalUI/Services/WhatsNewGate.cs`:
```csharp
using System;
using System.IO;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Decides whether the "What's New" overlay should appear after an update, and
/// persists the last-seen app version. The decision is a pure function of two
/// version strings; all file IO is failure-tolerant.
/// </summary>
public sealed class WhatsNewGate
{
    private readonly string _markerPath;

    public WhatsNewGate(string? markerPath = null)
        => _markerPath = markerPath ?? DefaultMarkerPath;

    private static string DefaultMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal",
        ".last-version");

    /// <summary>
    /// True only when <paramref name="lastSeen"/> is a parseable version strictly
    /// lower than <paramref name="current"/> (i.e. a real update happened). A missing,
    /// empty, equal, higher, or unparseable marker returns false — never nag.
    /// </summary>
    public static bool ShouldShow(string? lastSeen, string current)
    {
        var last = ParseVersion(lastSeen);
        var cur  = ParseVersion(current);
        if (last is null || cur is null) return false;
        return last < cur;
    }

    /// <summary>Reads the persisted last-seen version, or null if absent/unreadable.</summary>
    public string? ReadLastSeen()
    {
        try
        {
            if (!File.Exists(_markerPath)) return null;
            var s = File.ReadAllText(_markerPath).Trim();
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    /// <summary>Persists the last-seen version. Failures are non-fatal.</summary>
    public void WriteLastSeen(string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
            File.WriteAllText(_markerPath, version);
        }
        catch
        {
            // Non-fatal: worst case the overlay may re-show next launch.
        }
    }

    private static Version? ParseVersion(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].Trim('.');
        if (s.Length == 0) return null;
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out var v) ? v : null;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter "FullyQualifiedName~WhatsNewGateTests" -v minimal`
Expected: PASS (8 cases).

- [ ] **Step 5: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/WhatsNewGate.cs CryptoAITerminal.Core.Tests/WhatsNewGateTests.cs
git commit -m "feat: add WhatsNewGate show-decision logic"
```

---

## Task 3: `WhatsNewGate` marker read/write round-trip (TDD)

**Files:**
- Modify: `CryptoAITerminal.Core.Tests/WhatsNewGateTests.cs`

- [ ] **Step 1: Add round-trip tests using a temp path**

Append these methods inside the `WhatsNewGateTests` class (add `using System;` and `using System.IO;` at the top of the file if not present):
```csharp
    [Fact]
    public void Marker_WriteThenRead_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cai-whatsnew-{Guid.NewGuid():N}.txt");
        try
        {
            var gate = new WhatsNewGate(path);
            Assert.Null(gate.ReadLastSeen());   // absent → null
            gate.WriteLastSeen("1.6.1");
            Assert.Equal("1.6.1", gate.ReadLastSeen());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Marker_ReadMissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cai-whatsnew-missing-{Guid.NewGuid():N}.txt");
        var gate = new WhatsNewGate(path);
        Assert.Null(gate.ReadLastSeen());
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter "FullyQualifiedName~WhatsNewGateTests" -v minimal`
Expected: PASS (all WhatsNewGate tests, including the 2 new round-trip cases). The implementation from Task 2 already supports these — no production change needed.

- [ ] **Step 3: Commit**

```bash
git add CryptoAITerminal.Core.Tests/WhatsNewGateTests.cs
git commit -m "test: cover WhatsNewGate marker round-trip"
```

---

## Task 4: `IReleaseNotesService` + `ReleaseNotesService` (TDD)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/IReleaseNotesService.cs`
- Create: `CryptoAITerminal.TerminalUI/Services/ReleaseNotesService.cs`
- Test: `CryptoAITerminal.Core.Tests/ReleaseNotesServiceTests.cs`

- [ ] **Step 1: Create the interface**

Create `CryptoAITerminal.TerminalUI/Services/IReleaseNotesService.cs`:
```csharp
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Fetches human-readable release notes (markdown) for a given app version.
/// Failure-tolerant: any error (no network, missing release, empty body) returns null.
/// </summary>
public interface IReleaseNotesService
{
    Task<string?> GetNotesAsync(string version, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing tests with a fake HttpMessageHandler**

Create `CryptoAITerminal.Core.Tests/ReleaseNotesServiceTests.cs`:
```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class ReleaseNotesServiceTests
{
    /// <summary>Returns a scripted response (or throws) for any request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;
        public StubHandler(Func<HttpResponseMessage> factory) => _factory = factory;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_factory());
    }

    private static ReleaseNotesService Make(Func<HttpResponseMessage> factory)
        => new("090TYPE/CryptoAI", new HttpClient(new StubHandler(factory)));

    [Fact]
    public async Task Returns_body_on_success()
    {
        var svc = Make(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"body\":\"## Changes\\n- faster\"}")
        });
        var notes = await svc.GetNotesAsync("1.6.1");
        Assert.Equal("## Changes\n- faster", notes);
    }

    [Fact]
    public async Task Returns_null_on_404()
    {
        var svc = Make(() => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}")
        });
        Assert.Null(await svc.GetNotesAsync("9.9.9"));
    }

    [Fact]
    public async Task Returns_null_on_empty_body()
    {
        var svc = Make(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"body\":\"\"}")
        });
        Assert.Null(await svc.GetNotesAsync("1.6.1"));
    }

    [Fact]
    public async Task Returns_null_when_request_throws()
    {
        var svc = Make(() => throw new HttpRequestException("network down"));
        Assert.Null(await svc.GetNotesAsync("1.6.1"));
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter "FullyQualifiedName~ReleaseNotesServiceTests" -v minimal`
Expected: FAIL — `ReleaseNotesService` does not exist.

- [ ] **Step 4: Implement `ReleaseNotesService`**

Create `CryptoAITerminal.TerminalUI/Services/ReleaseNotesService.cs`:
```csharp
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// <see cref="IReleaseNotesService"/> backed by the GitHub Releases REST API.
/// Reads the release whose tag is <c>v{version}</c> and returns its markdown body.
/// Never throws into the UI (except <see cref="OperationCanceledException"/>).
/// </summary>
public sealed class ReleaseNotesService : IReleaseNotesService
{
    private readonly HttpClient _http;
    private readonly string _repoSlug;

    public ReleaseNotesService(string? repoSlug = null, HttpClient? http = null)
    {
        _repoSlug = repoSlug ?? AppInfo.RepoSlug;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("CryptoAITerminal-ReleaseNotes");
    }

    public async Task<string?> GetNotesAsync(string version, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_repoSlug}/releases/tags/v{version}";
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : null;
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj --filter "FullyQualifiedName~ReleaseNotesServiceTests" -v minimal`
Expected: PASS (4 cases).

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Services/IReleaseNotesService.cs CryptoAITerminal.TerminalUI/Services/ReleaseNotesService.cs CryptoAITerminal.Core.Tests/ReleaseNotesServiceTests.cs
git commit -m "feat: add ReleaseNotesService (GitHub release body fetch)"
```

---

## Task 5: Wire the overlay into `MainWindowViewModel`

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` — constructor (`~:167`, `~:1129`), command creation (`~:1249`), command property region (`~:1693`), and the update-banner region (`~:3397`) where the new state will live alongside the existing banner state.

IMPORTANT: this is a large file (4000+ lines). Locate anchors by searching the quoted code; line numbers are approximate.

- [ ] **Step 1: Add injected fields**

In the update-banner field region (right after `private readonly IAppUpdateService _updateService;` at `~:3398`), add:
```csharp
    private readonly IReleaseNotesService _releaseNotes;
    private readonly WhatsNewGate _whatsNewGate;
```

- [ ] **Step 2: Extend the constructor and assign the fields**

The constructor is currently `public MainWindowViewModel(IAppUpdateService? updateService = null)`. Change its parameter list to:
```csharp
    public MainWindowViewModel(
        IAppUpdateService? updateService = null,
        IReleaseNotesService? releaseNotes = null,
        WhatsNewGate? whatsNewGate = null)
```
Where `_updateService = updateService ?? new VelopackUpdateService();` is assigned (around `:1129`), add right after it:
```csharp
        _releaseNotes = releaseNotes ?? new ReleaseNotesService();
        _whatsNewGate = whatsNewGate ?? new WhatsNewGate();
```
And right after the existing `StartUpdateCheck();` call, add:
```csharp
        StartWhatsNewCheck();
```

- [ ] **Step 3: Add the bindable properties**

After the `UpdateProgressText` / `IsUpdateDownloading` properties in the banner region, add:
```csharp
    // ── What's New overlay ───────────────────────────────────────────────────
    private bool _isWhatsNewVisible;
    public bool IsWhatsNewVisible
    {
        get => _isWhatsNewVisible;
        private set => this.RaiseAndSetIfChanged(ref _isWhatsNewVisible, value);
    }

    private string _whatsNewVersion = "";
    public string WhatsNewVersion
    {
        get => _whatsNewVersion;
        private set => this.RaiseAndSetIfChanged(ref _whatsNewVersion, value);
    }

    private string _whatsNewMarkdown = "";
    public string WhatsNewMarkdown
    {
        get => _whatsNewMarkdown;
        private set => this.RaiseAndSetIfChanged(ref _whatsNewMarkdown, value);
    }

    private void StartWhatsNewCheck()
    {
        var current  = AppInfo.Version;
        var lastSeen = _whatsNewGate.ReadLastSeen();
        if (!WhatsNewGate.ShouldShow(lastSeen, current))
        {
            _whatsNewGate.WriteLastSeen(current); // advance marker; nothing to show
            return;
        }
        RunLoggedAsync(async () =>
        {
            var notes = await _releaseNotes.GetNotesAsync(current).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(notes))
            {
                WhatsNewVersion   = current;
                WhatsNewMarkdown  = notes;
                IsWhatsNewVisible = true;
            }
            _whatsNewGate.WriteLastSeen(current); // advance even if notes were unavailable
        }, "What's New check");
    }

    private void CloseWhatsNew() => IsWhatsNewVisible = false;
```

- [ ] **Step 4: Add the command (creation + property)**

At the command-creation block (near `InstallUpdateCommand = ReactiveCommand.Create(...)` at `~:1249`), add:
```csharp
        CloseWhatsNewCommand = ReactiveCommand.Create(CloseWhatsNew, outputScheduler: App.UiScheduler);
```
In the command-property region (near `public ReactiveCommand<Unit, Unit> InstallUpdateCommand { get; }` at `~:1693`), add:
```csharp
    public ReactiveCommand<Unit, Unit> CloseWhatsNewCommand { get; }
```

- [ ] **Step 5: Build**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeds. (The XAML overlay is added in Task 6; the new bindings won't be referenced by any view yet, so the build passes.)

- [ ] **Step 6: Commit**

```bash
git add CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs
git commit -m "feat: wire What's New overlay logic into MainWindowViewModel"
```

---

## Task 6: Add the What's New overlay to `MainWindow.axaml`

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml` — root element (add markdown xmlns, only in the primary path) and a new overlay `Border` placed right after the existing welcome overlay (`~:1167`, the `</Border>` that closes the welcome overlay).

Choose ONE of the two render paths below based on Task 1's result.

### PRIMARY path — Markdown.Avalonia compatible (Task 1 build succeeded)

- [ ] **Step 1 (primary): Add the markdown namespace to the root `<Window>`**

In the root `<Window ...>` opening tag (top of the file, where other `xmlns:` lines are declared), add:
```xml
        xmlns:md="https://github.com/whistyun/Markdown.Avalonia"
```

- [ ] **Step 2 (primary): Add the overlay after the welcome overlay**

Immediately after the welcome overlay's closing `</Border>` (the one at `~:1167`, just before the `<!-- Update available banner -->` comment), insert:
```xml
    <!-- "What's New" after-update overlay -->
    <Border IsVisible="{Binding IsWhatsNewVisible}"
            Background="#CC050A12"
            ZIndex="210">
      <Border Width="600" MaxHeight="640"
              HorizontalAlignment="Center" VerticalAlignment="Center"
              Background="#0B1622" BorderBrush="#1FE6C2" BorderThickness="1"
              CornerRadius="14" Padding="32,28">
        <DockPanel>
          <TextBlock DockPanel.Dock="Top"
                     Text="{Binding WhatsNewVersion, StringFormat='Что нового в v{0}'}"
                     Foreground="#F0F6FC" FontSize="22" FontWeight="Bold"
                     Margin="0,0,0,16" />
          <Button DockPanel.Dock="Bottom"
                  Content="Закрыть" Command="{Binding CloseWhatsNewCommand}"
                  HorizontalAlignment="Right" Margin="0,16,0,0"
                  Padding="20,10" Background="#1FE6C2" Foreground="#06121C" FontWeight="Bold" />
          <md:MarkdownScrollViewer Markdown="{Binding WhatsNewMarkdown}"
                                   Background="Transparent" />
        </DockPanel>
      </Border>
    </Border>
```

Proceed to Step 3.

### FALLBACK path — Markdown.Avalonia incompatible (Task 1 reported DONE_WITH_CONCERNS)

- [ ] **Step 1 (fallback): Do NOT add the markdown xmlns.** Skip it.

- [ ] **Step 2 (fallback): Add the overlay with a scrollable TextBlock**

Immediately after the welcome overlay's closing `</Border>` (at `~:1167`), insert:
```xml
    <!-- "What's New" after-update overlay (plain-text fallback) -->
    <Border IsVisible="{Binding IsWhatsNewVisible}"
            Background="#CC050A12"
            ZIndex="210">
      <Border Width="600" MaxHeight="640"
              HorizontalAlignment="Center" VerticalAlignment="Center"
              Background="#0B1622" BorderBrush="#1FE6C2" BorderThickness="1"
              CornerRadius="14" Padding="32,28">
        <DockPanel>
          <TextBlock DockPanel.Dock="Top"
                     Text="{Binding WhatsNewVersion, StringFormat='Что нового в v{0}'}"
                     Foreground="#F0F6FC" FontSize="22" FontWeight="Bold"
                     Margin="0,0,0,16" />
          <Button DockPanel.Dock="Bottom"
                  Content="Закрыть" Command="{Binding CloseWhatsNewCommand}"
                  HorizontalAlignment="Right" Margin="0,16,0,0"
                  Padding="20,10" Background="#1FE6C2" Foreground="#06121C" FontWeight="Bold" />
          <ScrollViewer>
            <TextBlock Text="{Binding WhatsNewMarkdown}"
                       Foreground="#C5D2DE" FontSize="13" TextWrapping="Wrap" />
          </ScrollViewer>
        </DockPanel>
      </Border>
    </Border>
```

Proceed to Step 3.

- [ ] **Step 3: Build to verify XAML + bindings compile**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeds. (Compiled bindings validate `IsWhatsNewVisible`, `WhatsNewVersion`, `WhatsNewMarkdown`, `CloseWhatsNewCommand` against the ViewModel from Task 5 — a typo fails the build here.)

- [ ] **Step 4: Commit**

```bash
git add CryptoAITerminal.TerminalUI/Views/MainWindow.axaml
git commit -m "feat: add What's New after-update overlay UI"
```

---

## Task 7: Full verification

- [ ] **Step 1: Build the TerminalUI project**

Run: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
Expected: Build succeeded.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test CryptoAITerminal.Core.Tests/CryptoAITerminal.Core.Tests.csproj -v minimal`
Expected: All tests pass — the prior suite plus the new `WhatsNewGateTests` (10 cases) and `ReleaseNotesServiceTests` (4 cases).

- [ ] **Step 3: Manual end-to-end (documented, run by maintainer)**

1. Ensure the GitHub release `v{current}` has a non-empty body (markdown).
2. Simulate an update: set `%LocalAppData%\CryptoAITerminal\.last-version` to an older version (e.g. `1.5.0`), launch the app.
3. Confirm the "Что нового в v{current}" overlay appears with the release notes, scrolls, and closes via "Закрыть".
4. Relaunch — confirm it does NOT reappear (marker now equals current).
5. Delete `.last-version` entirely and launch — confirm it does NOT appear (first-run path; only the welcome overlay shows).

Expected: overlay shows once after a simulated update, never on first-ever run, never on an unchanged version.

---

## Self-Review Notes

- **Spec coverage:** gate decision + marker (Tasks 2, 3 → `WhatsNewGate`), content fetch from GitHub (Task 4 → `ReleaseNotesService`), Markdown.Avalonia + Avalonia-12 risk with fallback (Tasks 1, 6), VM wiring + once-per-version marker advance + show-only-after-update + no-collision-with-welcome (Task 5 `StartWhatsNewCheck`), overlay UI styled like welcome/license (Task 6), error handling = silent + advance marker (Task 5 + Task 4 returns), tests (Tasks 2–4, 7). All spec sections map to tasks.
- **Type consistency:** `WhatsNewGate` (`ShouldShow`, `ReadLastSeen`, `WriteLastSeen`, ctor `(string? markerPath = null)`), `IReleaseNotesService.GetNotesAsync(string, CancellationToken)`, and VM members (`IsWhatsNewVisible`, `WhatsNewVersion`, `WhatsNewMarkdown`, `CloseWhatsNewCommand`, `StartWhatsNewCheck`, `CloseWhatsNew`) are named identically across Tasks 2–6. Constructor optional-param order: `(IAppUpdateService?, IReleaseNotesService?, WhatsNewGate?)` — additive, so existing `new MainWindowViewModel()` / `new MainWindowViewModel(fakeUpdate)` call sites still compile.
- **Marker-advance behavior:** `StartWhatsNewCheck` writes the marker in BOTH the not-show path (synchronously) and the post-fetch path (even when notes are null), so the GitHub API is hit at most once per new version — matches the spec's "advance marker even when not showing".
- **No placeholders:** every code step contains complete code; every run step has an exact command and expected result.
