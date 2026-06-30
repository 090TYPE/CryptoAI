using System;
using System.IO;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class WhatsNewGateTests
{
    [Theory]
    [InlineData(null, "1.6.1", false)]      // first-ever run: don't show
    [InlineData("", "1.6.1", false)]        // empty marker: don't show
    [InlineData("1.6.0", "1.6.1", true)]    // updated: show
    [InlineData("1.6.0", "1.7.0", true)]    // updated (minor): show
    [InlineData("v1.6.0", "1.6.1", true)]   // leading v tolerated
    [InlineData("1.6.1", "1.6.1", false)]   // same version: don't show
    [InlineData("1.7.0", "1.6.1", false)]   // downgrade: don't show
    [InlineData("garbage", "1.6.1", false)] // unparseable last-seen: don't show
    public void ShouldShow_DecidesByVersionComparison(string? lastSeen, string current, bool expected)
    {
        Assert.Equal(expected, WhatsNewGate.ShouldShow(lastSeen, current));
    }

    [Fact]
    public void Marker_WriteThenRead_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cai-whatsnew-{Guid.NewGuid():N}.txt");
        try
        {
            var gate = new WhatsNewGate(path);
            Assert.Null(gate.ReadLastSeen());   // absent → null
            gate.WriteLastSeen("1.6.1");
            Assert.Equal("1.6.1", gate.ReadLastSeen());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Marker_ReadMissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cai-whatsnew-missing-{Guid.NewGuid():N}.txt");
        var gate = new WhatsNewGate(path);
        Assert.Null(gate.ReadLastSeen());
    }
}
