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

    public Task DisconnectAsync()
    {
        // Local disconnect: drop the client and the encrypted session. (Dispose re-persists the
        // session, so delete the file afterwards.) The server-side auth key is left for the user
        // to revoke from Telegram's own session manager if desired.
        _client?.Dispose();
        _client = null;
        _store?.Dispose();
        _store = null;
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { /* best-effort */ }
        Username = null;
        SetState(TelegramConnectionState.Disconnected, null);
        return Task.CompletedTask;
    }
}
