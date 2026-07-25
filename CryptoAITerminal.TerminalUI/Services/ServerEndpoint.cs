using System;
using System.IO;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Resolves the CryptoAI server base URL (the edge API node). Precedence:
///   1. env var CRYPTOAI_SERVER_URL
///   2. file  {LocalAppData}/CryptoAITerminal/server_url.txt
///   3. none  → server features disabled (app stays fully local)
/// </summary>
public static class ServerEndpoint
{
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

        return null;
    }

    /// <summary>
    /// Persist the server URL and apply it immediately.
    ///
    /// Until this existed the only way to bind a terminal to a server was to create
    /// server_url.txt by hand, which is not something a customer can be asked to do - so every
    /// server feature silently stayed off for them, indistinguishable from being broken.
    ///
    /// The live statics are updated as well as the file: ChatClient reads ServerBaseUrl once at
    /// startup, so writing only the file would leave the setting apparently ignored until restart.
    /// Pass null or blank to unbind and go fully local again.
    /// </summary>
    /// <returns>An error message to show the user, or null on success.</returns>
    public static string? Save(string? url)
    {
        var trimmed = url?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return "Enter a full URL, for example https://api.example.com";
            }

            // Stored without a trailing slash: every caller appends "/api/...", and a double slash
            // is the kind of thing that only shows up as a 404 much later.
            trimmed = trimmed.TrimEnd('/');
        }

        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            }
            else
            {
                File.WriteAllText(ConfigPath, trimmed);
            }
        }
        catch (Exception ex)
        {
            return $"Could not save the setting: {ex.Message}";
        }

        // An environment variable still wins on the next start, so say so rather than let the user
        // wonder why their saved value was ignored.
        var env = Environment.GetEnvironmentVariable("CRYPTOAI_SERVER_URL");
        AIEngine.ChatClient.ServerBaseUrl = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;

        return string.IsNullOrWhiteSpace(env)
            ? null
            : "Saved, but CRYPTOAI_SERVER_URL is set and takes precedence on the next start.";
    }
}
