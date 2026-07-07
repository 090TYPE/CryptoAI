using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class DexAggregatorServiceTests
{
    private const string Json = """
    {"code":200,"data":{
      "inToken":{"address":"0xIN","decimals":18,"symbol":"USDT"},
      "outToken":{"address":"0xOUT","decimals":18,"symbol":"BUSD"},
      "inAmount":"100000000000000000000",
      "outAmount":"99848457949627196415",
      "price_impact":"-0.19%",
      "dexes":[{"dexCode":"Pancake"}],
      "path":{"routes":[{"subRoutes":[{"dexes":[{"dex":"PancakeV3"},{"dex":"Biswap"}]}]}]}
    }}
    """;

    [Fact]
    public void Parse_Extracts_Output_Impact_And_Route()
    {
        var q = DexAggregatorService.Parse(Json);

        Assert.NotNull(q);
        Assert.True(q!.IsLive);
        Assert.Equal("BUSD", q.OutSymbol);
        Assert.Equal("-0.19%", q.PriceImpact);
        // ~99.85 tokens out (18 decimals)
        Assert.InRange(q.OutTokens, 99m, 100m);
        Assert.Contains("PancakeV3", q.Route);
    }

    [Fact]
    public void Parse_Rejects_Bad_Json() => Assert.Null(DexAggregatorService.Parse("{}"));

    [Theory]
    [InlineData("ethereum", "eth")]
    [InlineData("bsc", "bsc")]
    [InlineData("solana", null)]
    public void ChainSlug_Maps_Evm_Only(string chain, string? expected)
        => Assert.Equal(expected, DexAggregatorService.ChainSlug(chain));
}
