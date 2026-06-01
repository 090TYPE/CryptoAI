using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Covers the pure version-comparison logic of <see cref="UpdateCheckService"/>.
/// The network path is not exercised here.
/// </summary>
public class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.0.0", "v1.0.1", true)]   // leading v tolerated
    [InlineData("1.0", "1.0.1", true)]      // bare major.minor current
    [InlineData("1.0.0", "1.1.0-beta", true)] // pre-release suffix on a higher version
    public void IsNewer_DetectsHigherVersions(string current, string latest, bool expected)
    {
        Assert.Equal(expected, UpdateCheckService.IsNewer(current, latest));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]   // same
    [InlineData("1.2.0", "1.1.9")]   // older
    [InlineData("2.0.0", "v1.9.9")]  // older with v
    public void IsNewer_RejectsSameOrOlder(string current, string latest)
    {
        Assert.False(UpdateCheckService.IsNewer(current, latest));
    }

    [Theory]
    [InlineData("1.0.0", "")]
    [InlineData("1.0.0", "not-a-version")]
    [InlineData("", "1.0.0")]
    public void IsNewer_UnparseableInputs_FailSafeToFalse(string current, string latest)
    {
        Assert.False(UpdateCheckService.IsNewer(current, latest));
    }

    [Fact]
    public void AppInfo_HasVersionAndRepo()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
        Assert.Contains("/", AppInfo.RepoSlug);
        Assert.StartsWith("https://", AppInfo.ReleasesUrl);
    }
}
