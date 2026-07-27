using System;
using System.IO;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Resolves the CryptoAI server base URL (the edge API node). Precedence:
///   1. env var CRYPTOAI_SERVER_URL          — operator override, wins over everything
///   2. file  {LocalAppData}/CryptoAITerminal/server_url.txt — a machine pinned elsewhere
///   3. <see cref="DefaultBaseUrl"/>         — the production server, built in
/// </summary>
public static class ServerEndpoint
{
    /// <summary>
    /// The server every shipped terminal talks to unless it is explicitly told otherwise.
    ///
    /// Built in rather than asked for. A customer has no way to know this address, and asking them
    /// to type it is asking them to get it wrong; before it was here, every server feature was off
    /// for them out of the box — no 24/7 candles for their watchlist, no shared AI digests, AI on
    /// their own key — and none of that announced itself, so a correctly working product looked
    /// broken.
    ///
    /// Moving customers to a different server is a change to THIS line plus a release build. That
    /// includes the move to a domain and TLS: until it happens the licence token in the X-License
    /// header travels unencrypted on every request, because http:// is what is written here.
    /// </summary>
    public const string DefaultBaseUrl = "http://46.19.68.215";

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "server_url.txt");

    public static string? ResolveBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("CRYPTOAI_SERVER_URL");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        try
        {
            if (File.Exists(ConfigPath))
            {
                var url = File.ReadAllText(ConfigPath).Trim();
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        catch { /* ignore */ }

        // Deliberately not null. Null meant "run fully locally", which was the right default only
        // while there was no server to point at.
        return string.IsNullOrWhiteSpace(DefaultBaseUrl) ? null : DefaultBaseUrl;
    }

    // There is deliberately no Save(): nothing in the product writes this any more. The address is
    // built in, and the two override paths above are for an operator, not a customer — both are set
    // from outside the app (an environment variable, or dropping server_url.txt in place), which is
    // exactly the audience they are meant for.
}
