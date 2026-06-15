# Telegram Account Connection (Part A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user sign in to their own Telegram account inside the desktop app (phone → code → optional 2FA), with an encrypted, auto-restoring session, surfaced in Settings → Telegram Account.

**Architecture:** A `TelegramUserClientService` wraps the managed MTProto client (WTelegramClient). Pure logic (phone normalization, login-step→state mapping) lives in a static `TelegramLoginFlow` and is unit-tested. The session is persisted through a `DpapiSessionStore` (a `Stream` that DPAPI-encrypts WTelegramClient's session bytes at rest, like `CredentialsService`). A thin `TelegramAccountViewModel` drives a new Settings UI section. No bot interaction (that is Part B).

**Tech Stack:** .NET 8, Avalonia 11, ReactiveUI, WTelegramClient (NuGet), `System.Security.Cryptography.ProtectedData` (DPAPI), xUnit.

---

## File structure
- Create `CryptoAITerminal.TerminalUI/Services/TelegramLoginFlow.cs` — pure helpers + `TelegramConnectionState` enum.
- Create `CryptoAITerminal.TerminalUI/Services/DpapiSessionStore.cs` — DPAPI-encrypting session `Stream`.
- Create `CryptoAITerminal.TerminalUI/Services/TelegramUserClientService.cs` — WTelegramClient wrapper + login state machine.
- Create `CryptoAITerminal.TerminalUI/ViewModels/TelegramAccountViewModel.cs` — binds UI ↔ service.
- Modify `CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj` — add the NuGet package.
- Modify `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` — expose `TelegramAccountVM`.
- Modify `CryptoAITerminal.TerminalUI/Views/SettingsView.axaml` — add the "Telegram Account" section.
- Modify `CryptoAITerminal.TerminalUI/Services/UiLocalizationService.cs` — EN→RU entries for new strings.
- Create `CryptoAITerminal.Core.Tests/TelegramLoginFlowTests.cs` and `CryptoAITerminal.Core.Tests/DpapiSessionStoreTests.cs`.

## Pre-flight (run before every build)
`Get-Process | Where-Object { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -ErrorAction SilentlyContinue`

All builds/tests use absolute project paths:
- Build UI: `dotnet build "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.TerminalUI\CryptoAITerminal.TerminalUI.csproj" -c Debug --nologo -v q`
- Tests: `dotnet test "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.Core.Tests\CryptoAITerminal.Core.Tests.csproj" --nologo -v q`

---

### Task 1: Add the WTelegramClient package

**Files:**
- Modify: `CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj`

- [ ] **Step 1: Add the package (latest stable)**

Run:
```
dotnet add "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.TerminalUI\CryptoAITerminal.TerminalUI.csproj" package WTelegramClient
```
Expected: adds `<PackageReference Include="WTelegramClient" Version="..." />` and restores.

- [ ] **Step 2: Build to confirm restore**

Run the UI build. Expected: `Сборка успешно завершена.` / `Ошибок: 0`.

- [ ] **Step 3: Commit**
```bash
git add CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj
git commit -m "build: add WTelegramClient (managed MTProto) for Telegram account login"
```

---

### Task 2: `TelegramLoginFlow` (pure logic) + tests

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/TelegramLoginFlow.cs`
- Test: `CryptoAITerminal.Core.Tests/TelegramLoginFlowTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `CryptoAITerminal.Core.Tests/TelegramLoginFlowTests.cs`:
```csharp
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class TelegramLoginFlowTests
{
    [Theory]
    [InlineData("+1 (234) 567-89-00", "+12345678900")]
    [InlineData("12345678900", "+12345678900")]
    [InlineData("  +44 7700 900123 ", "+447700900123")]
    public void NormalizePhone_StripsFormatting_AndEnsuresPlus(string input, string expected)
    {
        Assert.Equal(expected, TelegramLoginFlow.NormalizePhone(input));
    }

    [Theory]
    [InlineData(null, TelegramConnectionState.Connected)]
    [InlineData("verification_code", TelegramConnectionState.AwaitingCode)]
    [InlineData("password", TelegramConnectionState.AwaitingPassword)]
    [InlineData("name", TelegramConnectionState.Error)]
    public void MapStep_MapsWTelegramLoginResult_ToState(string? step, TelegramConnectionState expected)
    {
        Assert.Equal(expected, TelegramLoginFlow.MapStep(step));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run tests filtered:
```
dotnet test "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.Core.Tests\CryptoAITerminal.Core.Tests.csproj" --filter "FullyQualifiedName~TelegramLoginFlowTests" --nologo -v q
```
Expected: FAIL to compile / type `TelegramLoginFlow` not found.

- [ ] **Step 3: Implement `TelegramLoginFlow`**

Create `CryptoAITerminal.TerminalUI/Services/TelegramLoginFlow.cs`:
```csharp
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Connection state of the Telegram user-account login.</summary>
public enum TelegramConnectionState
{
    Disconnected,
    AwaitingCode,
    AwaitingPassword,
    Connected,
    Error
}

/// <summary>
/// Pure helpers for the Telegram login flow — kept free of the network client so they
/// can be unit-tested. WTelegramClient's <c>Login(string)</c> returns the name of the next
/// required field ("verification_code", "password", …) or <c>null</c> when authorized.
/// </summary>
public static class TelegramLoginFlow
{
    /// <summary>Removes spaces/punctuation and ensures a single leading '+'.</summary>
    public static string NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? string.Empty : "+" + digits;
    }

    /// <summary>Maps a WTelegramClient <c>Login()</c> result to a connection state.</summary>
    public static TelegramConnectionState MapStep(string? loginResult) => loginResult switch
    {
        null                 => TelegramConnectionState.Connected,
        "verification_code"  => TelegramConnectionState.AwaitingCode,
        "password"           => TelegramConnectionState.AwaitingPassword,
        _                    => TelegramConnectionState.Error
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the same filtered test command. Expected: PASS (7 cases).

- [ ] **Step 5: Commit**
```bash
git add CryptoAITerminal.TerminalUI/Services/TelegramLoginFlow.cs CryptoAITerminal.Core.Tests/TelegramLoginFlowTests.cs
git commit -m "feat: TelegramLoginFlow pure helpers + tests"
```

---

### Task 3: `DpapiSessionStore` (encrypted session stream) + round-trip test

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/DpapiSessionStore.cs`
- Test: `CryptoAITerminal.Core.Tests/DpapiSessionStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Create `CryptoAITerminal.Core.Tests/DpapiSessionStoreTests.cs`:
```csharp
using System.IO;
using System.Text;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DpapiSessionStoreTests
{
    [Fact]
    public void WrittenBytes_SurviveReopen_Encrypted()
    {
        var path = Path.Combine(Path.GetTempPath(), "cryptoai-tg-session-test-" + Path.GetRandomFileName());
        var payload = Encoding.UTF8.GetBytes("telegram-session-bytes-1234567890");
        try
        {
            using (var store = new DpapiSessionStore(path))
            {
                store.Write(payload, 0, payload.Length);
                store.Flush();
            }

            // On-disk file is encrypted (not the raw payload).
            var onDisk = File.ReadAllBytes(path);
            Assert.NotEqual(payload, onDisk);

            // Reopening decrypts the same bytes back.
            using var reopened = new DpapiSessionStore(path);
            var buffer = new byte[payload.Length];
            reopened.Position = 0;
            var read = reopened.Read(buffer, 0, buffer.Length);
            Assert.Equal(payload.Length, read);
            Assert.Equal(payload, buffer);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```
dotnet test "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.Core.Tests\CryptoAITerminal.Core.Tests.csproj" --filter "FullyQualifiedName~DpapiSessionStoreTests" --nologo -v q
```
Expected: FAIL to compile — `DpapiSessionStore` not found.

- [ ] **Step 3: Implement `DpapiSessionStore`**

Create `CryptoAITerminal.TerminalUI/Services/DpapiSessionStore.cs`:
```csharp
using System;
using System.IO;
using System.Security.Cryptography;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// A seekable in-memory <see cref="Stream"/> that WTelegramClient uses as its session store,
/// persisting its contents to disk DPAPI-encrypted (CurrentUser) — the same protection
/// CredentialsService uses for api-credentials.json. WTelegramClient rewrites the whole
/// session on change, so persisting on every Write/Flush is sufficient.
/// </summary>
public sealed class DpapiSessionStore : Stream
{
    private static readonly byte[] Entropy =
        System.Text.Encoding.UTF8.GetBytes("CryptoAITerminal.TelegramSession.v1");

    private readonly string _path;
    private readonly MemoryStream _buffer = new();

    public DpapiSessionStore(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                _buffer.Write(plain, 0, plain.Length);
                _buffer.Position = 0;
            }
            catch
            {
                _buffer.SetLength(0); // corrupt/foreign session → start fresh
            }
        }
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => true;
    public override long Length   => _buffer.Length;
    public override long Position { get => _buffer.Position; set => _buffer.Position = value; }

    public override int  Read(byte[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin)       => _buffer.Seek(offset, origin);
    public override void SetLength(long value)                      { _buffer.SetLength(value); Persist(); }
    public override void Write(byte[] buffer, int offset, int count){ _buffer.Write(buffer, offset, count); Persist(); }
    public override void Flush()                                    => Persist();

    private void Persist()
    {
        try
        {
            var plain  = _buffer.ToArray();
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var dir    = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(_path, cipher);
        }
        catch { /* best-effort persistence */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Persist();
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run the same filtered test command. Expected: PASS.

- [ ] **Step 5: Commit**
```bash
git add CryptoAITerminal.TerminalUI/Services/DpapiSessionStore.cs CryptoAITerminal.Core.Tests/DpapiSessionStoreTests.cs
git commit -m "feat: DpapiSessionStore for encrypted Telegram session at rest"
```

---

### Task 4: `TelegramUserClientService` (MTProto wrapper)

**Files:**
- Create: `CryptoAITerminal.TerminalUI/Services/TelegramUserClientService.cs`

> WTelegramClient API used here (stable, from its README):
> `new WTelegram.Client(Func<string,string?> config, Stream sessionStore)`;
> `await client.Login(string loginInfo)` returns the next required field or `null` when authorized;
> `client.User` is the logged-in user after success. If a method/property name differs in the
> installed version, adjust at compile time (e.g. the active username may be `User.MainUsername`).

- [ ] **Step 1: Implement the service**

Create `CryptoAITerminal.TerminalUI/Services/TelegramUserClientService.cs`:
```csharp
using System;
using System.IO;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Owns the WTelegramClient instance and the user-account login state machine.
/// Network/Telegram side only — no bot chat (that is Part B). Session is persisted
/// encrypted via <see cref="DpapiSessionStore"/>.
/// </summary>
public sealed class TelegramUserClientService
{
    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "telegram-session.bin");

    // api_id / api_hash from https://my.telegram.org. Baked default, overridable by env var.
    // Fill these constants for release builds (or set TELEGRAM_API_ID / TELEGRAM_API_HASH).
    private const int    BakedApiId   = 0;
    private const string BakedApiHash = "";

    private static int ApiId =>
        int.TryParse(Environment.GetEnvironmentVariable("TELEGRAM_API_ID"), out var id) ? id : BakedApiId;
    private static string ApiHash =>
        Environment.GetEnvironmentVariable("TELEGRAM_API_HASH") is { Length: > 0 } h ? h : BakedApiHash;

    private WTelegram.Client? _client;
    private DpapiSessionStore? _store;
    private string _phone = string.Empty;

    public TelegramConnectionState State { get; private set; } = TelegramConnectionState.Disconnected;
    public string? Username { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event EventHandler? StateChanged;

    public bool HasSavedSession => File.Exists(SessionPath);

    private string? Config(string what) => what switch
    {
        "api_id"   => ApiId.ToString(),
        "api_hash" => ApiHash,
        _          => null
    };

    private void EnsureClient()
    {
        if (_client is not null) return;
        _store  = new DpapiSessionStore(SessionPath);
        _client = new WTelegram.Client(Config, _store);
    }

    private void SetState(TelegramConnectionState state, string? error)
    {
        State = state;
        ErrorMessage = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CaptureUser()
    {
        Username = _client?.User?.MainUsername ?? _client?.User?.first_name;
        SetState(TelegramConnectionState.Connected, null);
    }

    /// <summary>Silent resume from a saved session on startup. Never prompts.</summary>
    public async Task<bool> TryRestoreSessionAsync()
    {
        if (!HasSavedSession) return false;
        try
        {
            EnsureClient();
            var user = await _client!.LoginUserIfNeeded();
            Username = user.MainUsername ?? user.first_name;
            SetState(TelegramConnectionState.Connected, null);
            return true;
        }
        catch
        {
            // No valid session (would need interactive input) → stay disconnected, don't prompt.
            SetState(TelegramConnectionState.Disconnected, null);
            return false;
        }
    }

    public async Task BeginLoginAsync(string phone)
    {
        try
        {
            _phone = TelegramLoginFlow.NormalizePhone(phone);
            if (_phone.Length == 0) { SetState(TelegramConnectionState.Error, "Enter a valid phone number."); return; }
            EnsureClient();
            var next = await _client!.Login(_phone);
            var mapped = TelegramLoginFlow.MapStep(next);
            if (mapped == TelegramConnectionState.Error) { SetState(mapped, $"Unsupported login step: {next}"); return; }
            if (mapped == TelegramConnectionState.Connected) { CaptureUser(); return; }
            SetState(mapped, null);
        }
        catch (Exception ex) { SetState(TelegramConnectionState.Error, ex.Message); }
    }

    public async Task SubmitCodeAsync(string code)
    {
        try
        {
            EnsureClient();
            var next = await _client!.Login(code.Trim());
            var mapped = TelegramLoginFlow.MapStep(next);
            if (mapped == TelegramConnectionState.Error) { SetState(mapped, $"Unsupported login step: {next}"); return; }
            if (mapped == TelegramConnectionState.Connected) { CaptureUser(); return; }
            SetState(mapped, null);
        }
        catch (Exception ex) { SetState(TelegramConnectionState.Error, ex.Message); }
    }

    public async Task SubmitPasswordAsync(string password)
    {
        try
        {
            EnsureClient();
            var next = await _client!.Login(password);
            if (TelegramLoginFlow.MapStep(next) == TelegramConnectionState.Connected) { CaptureUser(); return; }
            SetState(TelegramConnectionState.Error, "Two-factor password rejected.");
        }
        catch (Exception ex) { SetState(TelegramConnectionState.Error, ex.Message); }
    }

    public async Task DisconnectAsync()
    {
        try { if (_client is not null) await _client.Auth_LogOut(); } catch { /* best-effort */ }
        _client?.Dispose();
        _client = null;
        _store?.Dispose();
        _store = null;
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { /* best-effort */ }
        Username = null;
        SetState(TelegramConnectionState.Disconnected, null);
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run the UI build. Expected: `Ошибок: 0`.
If a WTelegramClient member name differs (e.g. `MainUsername`, `Auth_LogOut`), fix to the installed package's name and rebuild.

- [ ] **Step 3: Commit**
```bash
git add CryptoAITerminal.TerminalUI/Services/TelegramUserClientService.cs
git commit -m "feat: TelegramUserClientService MTProto login + encrypted session"
```

---

### Task 5: ViewModel + Settings UI + wiring + localization

**Files:**
- Create: `CryptoAITerminal.TerminalUI/ViewModels/TelegramAccountViewModel.cs`
- Modify: `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`
- Modify: `CryptoAITerminal.TerminalUI/Views/SettingsView.axaml`
- Modify: `CryptoAITerminal.TerminalUI/Services/UiLocalizationService.cs`

- [ ] **Step 1: Implement the ViewModel**

Create `CryptoAITerminal.TerminalUI/ViewModels/TelegramAccountViewModel.cs`:
```csharp
using System.Reactive;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public sealed class TelegramAccountViewModel : ReactiveObject
{
    private readonly TelegramUserClientService _svc;

    public TelegramAccountViewModel(TelegramUserClientService svc)
    {
        _svc = svc;
        _svc.StateChanged += (_, _) => App.UiScheduler.Schedule(RaiseAll);

        ConnectCommand    = ReactiveCommand.CreateFromTask(ConnectAsync,    outputScheduler: App.UiScheduler);
        DisconnectCommand = ReactiveCommand.CreateFromTask(_svc.DisconnectAsync, outputScheduler: App.UiScheduler);

        // Attempt silent restore on construction.
        _ = _svc.TryRestoreSessionAsync();
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand    { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    private string _phone = "";
    public string Phone { get => _phone; set => this.RaiseAndSetIfChanged(ref _phone, value); }

    private string _code = "";
    public string Code { get => _code; set => this.RaiseAndSetIfChanged(ref _code, value); }

    private string _password = "";
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }

    public bool IsCodeVisible     => _svc.State == TelegramConnectionState.AwaitingCode;
    public bool IsPasswordVisible => _svc.State == TelegramConnectionState.AwaitingPassword;
    public bool IsConnected       => _svc.State == TelegramConnectionState.Connected;

    public string StatusText => _svc.State switch
    {
        TelegramConnectionState.Connected        => $"Connected as @{_svc.Username}",
        TelegramConnectionState.AwaitingCode     => "Enter the code from Telegram",
        TelegramConnectionState.AwaitingPassword => "Enter your two-factor password",
        TelegramConnectionState.Error            => _svc.ErrorMessage ?? "Connection error",
        _                                        => "Not connected"
    };

    private async System.Threading.Tasks.Task ConnectAsync()
    {
        switch (_svc.State)
        {
            case TelegramConnectionState.AwaitingCode:     await _svc.SubmitCodeAsync(Code); break;
            case TelegramConnectionState.AwaitingPassword: await _svc.SubmitPasswordAsync(Password); break;
            default:                                       await _svc.BeginLoginAsync(Phone); break;
        }
    }

    private void RaiseAll()
    {
        this.RaisePropertyChanged(nameof(IsCodeVisible));
        this.RaisePropertyChanged(nameof(IsPasswordVisible));
        this.RaisePropertyChanged(nameof(IsConnected));
        this.RaisePropertyChanged(nameof(StatusText));
    }
}
```

- [ ] **Step 2: Expose the VM from `MainWindowViewModel`**

In `MainWindowViewModel.cs`, near the other VM properties (e.g. next to where service-backed VMs like `CopilotVM` are created), add a field/property and initialize it. Add:
```csharp
    public TelegramAccountViewModel TelegramAccountVM { get; }
```
And in the `MainWindowViewModel` constructor (after other VM initializations), add:
```csharp
        TelegramAccountVM = new TelegramAccountViewModel(new Services.TelegramUserClientService());
```
(Place the assignment with the other `... = new ...VM(...)` lines in the constructor.)

- [ ] **Step 3: Add the Settings UI section**

In `Views/SettingsView.axaml`, add this block inside the settings content (e.g. right after the existing Telegram **notifications** block; search for `TelegramBotToken` to locate it, and insert the section as a sibling panel):
```xml
<!-- Telegram Account (MTProto user login) -->
<Border Classes="Panel" Margin="0,12,0,0" DataContext="{Binding TelegramAccountVM}">
  <StackPanel Spacing="10">
    <TextBlock Text="Telegram Account" FontSize="14" FontWeight="SemiBold" Foreground="#E8F0FE" />
    <TextBlock Text="Sign in with your Telegram account to talk to the bot from inside the app."
               FontSize="11" Foreground="#5C6E82" TextWrapping="Wrap" />

    <TextBlock Text="{Binding StatusText}" FontSize="12" Foreground="#21E6C1" />

    <TextBox Text="{Binding Phone, Mode=TwoWay}" Watermark="+1 234 567 8900"
             IsVisible="{Binding !IsConnected}" />
    <TextBox Text="{Binding Code, Mode=TwoWay}" Watermark="Login code"
             IsVisible="{Binding IsCodeVisible}" />
    <TextBox Text="{Binding Password, Mode=TwoWay}" Watermark="Two-factor password"
             PasswordChar="●" IsVisible="{Binding IsPasswordVisible}" />

    <StackPanel Orientation="Horizontal" Spacing="10">
      <Button Content="Connect" Command="{Binding ConnectCommand}" IsVisible="{Binding !IsConnected}" />
      <Button Content="Disconnect" Command="{Binding DisconnectCommand}" IsVisible="{Binding IsConnected}" />
    </StackPanel>
  </StackPanel>
</Border>
```

- [ ] **Step 4: Add localization entries**

In `UiLocalizationService.cs`, add these to the `_englishToRussian` dictionary (before the closing `};`, ensuring the preceding line ends with a comma and no key duplicates an existing one):
```csharp
        ["Telegram Account"] = "Telegram-аккаунт",
        ["Sign in with your Telegram account to talk to the bot from inside the app."] = "Войдите в свой Telegram-аккаунт, чтобы общаться с ботом прямо из приложения.",
        ["Not connected"] = "Не подключено",
        ["Enter the code from Telegram"] = "Введите код из Telegram",
        ["Enter your two-factor password"] = "Введите пароль двухфакторной аутентификации",
        ["Connection error"] = "Ошибка подключения",
        ["Connect"] = "Подключить",
        ["Disconnect"] = "Отключить",
        ["Login code"] = "Код входа",
        ["Two-factor password"] = "Пароль 2FA"
```
(Check `Connect`/`Disconnect`/`Connection error` are not already keys; if one exists, drop that line.)

- [ ] **Step 5: Build**

Run the UI build. Expected: `Ошибок: 0`. Fix any binding/namespace errors surfaced by the Avalonia compiler.

- [ ] **Step 6: Commit**
```bash
git add CryptoAITerminal.TerminalUI/ViewModels/TelegramAccountViewModel.cs CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs CryptoAITerminal.TerminalUI/Views/SettingsView.axaml CryptoAITerminal.TerminalUI/Services/UiLocalizationService.cs
git commit -m "feat: Telegram Account section in Settings (connect/disconnect UI)"
```

---

### Task 6: Full test run, manual smoke, finish

- [ ] **Step 1: Full unit-test run**

Run the full test suite. Expected: all pass (existing 294 + new `TelegramLoginFlowTests` and `DpapiSessionStoreTests`).

- [ ] **Step 2: Manual smoke (real Telegram account)**

Set credentials, then launch:
```powershell
$env:TELEGRAM_API_ID = "<your api_id>"
$env:TELEGRAM_API_HASH = "<your api_hash>"
& "C:\Users\090\Documents\GitHub\CryptoAI\CryptoAITerminal.TerminalUI\bin\Debug\net8.0-windows\win-x64\CryptoAITerminal.TerminalUI.exe"
```
Verify in **Settings → Telegram Account**:
- Enter phone → Connect → "Enter the code from Telegram"; the code arrives in Telegram.
- Enter code → if 2FA on, "Enter your two-factor password" → enter it → "Connected as @username".
- Close and relaunch → status shows "Connected as @username" without asking for a code (session auto-restored).
- Disconnect → "Not connected"; `%LOCALAPPDATA%\CryptoAITerminal\telegram-session.bin` is gone.
- Toggle RU/EN → labels/status switch language.
Close the app afterward.

- [ ] **Step 3: Final commit (if any smoke fixes)**

Commit any fixes from smoke with a descriptive message. If none, skip.

---

## Self-review notes
- **Spec coverage:** WTelegramClient (Task 1), api_id/hash + env override (Task 4 `ApiId`/`ApiHash`), login state machine + methods (Task 4), DPAPI session + auto-restore (Tasks 3–4, `TryRestoreSessionAsync`), Settings UI + states (Task 5), error handling (Task 4 try/catch + `Error` state), localization (Task 5 Step 4), unit tests for pure logic + session round-trip (Tasks 2–3), manual smoke (Task 6). All spec sections mapped.
- **Type consistency:** `TelegramConnectionState` (defined Task 2) used in Tasks 4–5; service methods `BeginLoginAsync/SubmitCodeAsync/SubmitPasswordAsync/TryRestoreSessionAsync/DisconnectAsync` named consistently across service (Task 4) and VM (Task 5).
- **Author:** all commits by the repo user only — no Co-Authored-By trailer.
- **Known external-API risk:** WTelegramClient member names (`User.MainUsername`, `Auth_LogOut`, `Login` return contract) are from its README; verify against the installed package version during Task 4 build and adjust if needed.
