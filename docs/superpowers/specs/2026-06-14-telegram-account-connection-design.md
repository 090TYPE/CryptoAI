# Telegram account connection (Part A) — design

> Sub-feature **A** of the in-app Telegram integration. Foundation only: log the user's
> Telegram **account** into the desktop app via MTProto and keep the session.
> Part **B** (in-app mini-chat with the LicenseBot + "request access" + key auto-detect)
> builds on this in a separate spec/plan.

## Goal
From **Settings → Telegram Account**, the user signs in to their own Telegram account
(phone → login code → optional 2FA password). The session is stored encrypted and
auto-restored on next launch. The UI shows "Connected as @username" / errors, and a
Disconnect action. No bot interaction yet — that is Part B.

## Why MTProto / why this approach
Chosen by the user (Approach 3). A distributed desktop app cannot securely message a bot
via the Bot API (would require shipping the bot token) and a deep link was rejected, so the
app acts as a Telegram **user client**. This part delivers the connection; it is independently
useful and testable ("Connected as @user") before any chat is built.

## Library
**WTelegramClient** (NuGet `WTelegramClient`) — pure-managed MTProto client for .NET, no
native binaries. Added to `CryptoAITerminal.TerminalUI`.

## Configuration
- `api_id` (int) + `api_hash` (string): required Telegram app credentials from
  https://my.telegram.org. **Embedded as constants** in the app, each overridable by env var
  (`TELEGRAM_API_ID` / `TELEGRAM_API_HASH`) — mirrors the existing "env var > baked value"
  pattern in `CredentialsService`.
- The user (owner) fills the embedded constants before building a release.

## Components (isolation & boundaries)
- **`Services/TelegramUserClientService`** — owns the `WTelegram.Client` instance and the
  login state machine. Public surface:
  - `ConnectionState State { get; }` — enum: `Disconnected`, `AwaitingCode`, `AwaitingPassword`,
    `Connected`, `Error`.
  - `string? Username { get; }`, `string? ErrorMessage { get; }`.
  - `event EventHandler? StateChanged;`
  - `Task BeginLoginAsync(string phone)` — starts login; drives state to `AwaitingCode`.
  - `Task SubmitCodeAsync(string code)` — drives to `AwaitingPassword` or `Connected`.
  - `Task SubmitPasswordAsync(string password)` — 2FA; drives to `Connected`.
  - `Task<bool> TryRestoreSessionAsync()` — silent auto-login from saved session on startup.
  - `Task DisconnectAsync()` — logs out / drops session and deletes the session file.
  - Wraps WTelegramClient's `Login()` loop, which returns the next required field
    (`"verification_code"`, `"password"`, …) or `null` when authorized.
- **`Services/TelegramLoginFlow`** (pure, static) — testable helpers: phone normalization
  (e.g. strip spaces, ensure leading `+`), and mapping a WTelegramClient `Login()` return
  string → `ConnectionState`. Unit-tested.
- **`ViewModels/TelegramAccountViewModel`** — binds the Settings UI to the service: phone/code/
  password inputs, visibility of the code/password fields per state, Connect/Disconnect commands,
  status text. English-source strings (RU via dictionary).
- **`Views/SettingsView.axaml`** — new "Telegram Account" section hosting the above.

## Session persistence & security
- WTelegramClient is given a **custom session store stream**; its bytes are persisted to
  `%LOCALAPPDATA%\CryptoAITerminal\telegram-session.bin`, **DPAPI-encrypted**
  (`ProtectedData.Protect/Unprotect`, `DataProtectionScope.CurrentUser`) — same mechanism
  `CredentialsService` uses for `api-credentials.json`.
- On startup, `TryRestoreSessionAsync()` decrypts and resumes without re-asking for a code.
- `DisconnectAsync()` deletes the file.
- ToS note: the user's own account performing user actions (login, later messaging a bot) is
  acceptable use; documented for transparency. api_id/api_hash and session never leave the machine.

## Data flow (login)
```
phone ─BeginLoginAsync─▶ WTelegram Login(phone) ─▶ "verification_code"
  └▶ State=AwaitingCode ─▶ UI shows code field
code ─SubmitCodeAsync─▶ Login(code) ─▶ "password" (if 2FA) | null (done)
  └▶ AwaitingPassword | Connected
password ─SubmitPasswordAsync─▶ Login(password) ─▶ null ─▶ Connected(@username)
```

## Error handling
Surface as `Error` state + `ErrorMessage` (shown in UI), never crash:
- Invalid/expired code, wrong 2FA password → ask again.
- `FLOOD_WAIT_x` (rate limit) → show "try again in N s".
- Network/timeout / missing api_id/api_hash → explanatory message.
- All service calls wrapped; failures set `Error` state.

## UI (Settings → Telegram Account)
- Phone input + "Send code" (Connect) button.
- Code input (visible only in `AwaitingCode`).
- 2FA password input (visible only in `AwaitingPassword`).
- Status line: `Connected as @username` (green) / error (amber) / `Not connected`.
- Disconnect button (visible when `Connected`).
- Tooltips/labels English-source + RU dictionary entries.

## Testing
- **Unit (xUnit, `CryptoAITerminal.Core.Tests`):** `TelegramLoginFlow` — phone normalization
  and `Login()`-return → state mapping. (Pure logic, project pattern.)
- **No unit test for the live MTProto login** (real network/Telegram) — verified by **manual
  smoke**: connect a real account end-to-end (code, and 2FA if enabled), restart app → session
  auto-restores, Disconnect clears it.

## Out of scope (Part B / YAGNI)
- No bot chat, no message send/receive, no "request access", no key detection — all Part B.
- No multi-account, no QR-login, no media.

## Files
- Add NuGet `WTelegramClient` to `CryptoAITerminal.TerminalUI.csproj`.
- Create `Services/TelegramUserClientService.cs`, `Services/TelegramLoginFlow.cs`,
  `ViewModels/TelegramAccountViewModel.cs`.
- Modify `Views/SettingsView.axaml` (+ wire VM in `MainWindowViewModel`), `Services/UiLocalizationService.cs`
  (new EN→RU entries).
- Test: `CryptoAITerminal.Core.Tests/TelegramLoginFlowTests.cs`.
