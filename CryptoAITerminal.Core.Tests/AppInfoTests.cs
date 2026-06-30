using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>Sanity checks for the app identity constants.</summary>
public class AppInfoTests
{
    [Fact]
    public void AppInfo_HasVersionAndRepo()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
        Assert.Contains("/", AppInfo.RepoSlug);
        Assert.StartsWith("https://", AppInfo.ReleasesUrl);
        Assert.StartsWith("https://", AppInfo.RepoUrl);
    }
}
