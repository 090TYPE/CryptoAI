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
}
