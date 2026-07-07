using System.Linq;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class GasOracleServiceTests
{
    [Fact]
    public void BuildTiers_AreAscending_AndPositive()
    {
        var tiers = GasOracleService.BuildTiers(10m);

        Assert.Equal(4, tiers.Count);
        Assert.All(tiers, t => Assert.True(t.Gwei > 0m));
        for (var i = 1; i < tiers.Count; i++)
        {
            Assert.True(tiers[i].Gwei >= tiers[i - 1].Gwei);
        }
        Assert.Equal(new[] { "Slow", "Standard", "Fast", "Instant" }, tiers.Select(t => t.Key).ToArray());
    }

    [Fact]
    public void BuildTiers_FloorsTinyBase()
    {
        var tiers = GasOracleService.BuildTiers(0m);
        Assert.All(tiers, t => Assert.True(t.Gwei > 0m));
    }

    [Theory]
    [InlineData("0x3b9aca00", 1)]      // 1,000,000,000 wei = 1 gwei
    [InlineData("0x77359400", 2)]      // 2,000,000,000 wei = 2 gwei
    public void HexWeiToGwei_Parses(string hex, int expectedGwei)
    {
        Assert.Equal(expectedGwei, GasOracleService.HexWeiToGwei(hex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zzz")]
    [InlineData(null)]
    public void HexWeiToGwei_Rejects_Bad_Input(string? hex)
    {
        Assert.Null(GasOracleService.HexWeiToGwei(hex));
    }

    [Fact]
    public void DefaultBaseGwei_Differs_By_Chain()
    {
        Assert.True(GasOracleService.DefaultBaseGwei("bsc") > 0m);
        Assert.True(GasOracleService.DefaultBaseGwei("base") > 0m);
        Assert.NotEqual(GasOracleService.DefaultBaseGwei("bsc"), GasOracleService.DefaultBaseGwei("base"));
    }
}
