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
