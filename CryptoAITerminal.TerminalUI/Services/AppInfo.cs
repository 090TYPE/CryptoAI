namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Current app identity — single source of truth for version + repo.</summary>
public static class AppInfo
{
    public const string Version  = "1.6.0";
    public const string RepoSlug = "090TYPE/CryptoAI";
    public static string ReleasesUrl => $"https://github.com/{RepoSlug}/releases";
    public static string RepoUrl     => $"https://github.com/{RepoSlug}";
}
