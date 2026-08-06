namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Current app identity — single source of truth for version + repo.</summary>
public static class AppInfo
{
    // Читается из сборки, а не пишется руками: константа отставала от csproj на три релиза
    // (1.7.0 против 1.8.10) и подставлялась в баннер обновления как «ваша текущая версия»,
    // то есть предлагала обновиться на то, что уже стоит. Версию задаёт только csproj.
    public static readonly string Version =
        typeof(AppInfo).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";
    public const string RepoSlug = "090TYPE/CryptoAI";
    public static string ReleasesUrl => $"https://github.com/{RepoSlug}/releases";
    public static string RepoUrl     => $"https://github.com/{RepoSlug}";
}
