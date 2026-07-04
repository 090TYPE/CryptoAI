using System.Linq;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class DexExchangeDataServiceTests
{
    private const string HyperliquidJson = """
    [
      {"universe":[
        {"name":"BTC","maxLeverage":40},
        {"name":"ETH","maxLeverage":25},
        {"name":"OLD","maxLeverage":3,"isDelisted":true}
      ]},
      [
        {"funding":"0.0000125","prevDayPx":"62227.0","markPx":"63374.0","dayNtlVlm":"1558509644.87"},
        {"funding":"0.0000125","prevDayPx":"1747.0","markPx":"1796.5","dayNtlVlm":"572540964.75"},
        {"funding":"0.0","prevDayPx":"1.0","markPx":"1.0","dayNtlVlm":"5.0"}
      ]
    ]
    """;

    private const string DydxJson = """
    {"markets":{
      "BTC-USD":{"ticker":"BTC-USD","oraclePrice":"63258.38","priceChange24H":"1104.72","volume24H":"23402894.56","nextFundingRate":"-0.00003686","initialMarginFraction":"0.02"},
      "ETH-USD":{"ticker":"ETH-USD","oraclePrice":"3521.80","priceChange24H":"-40.10","volume24H":"11002894.10","nextFundingRate":"0.00001","initialMarginFraction":"0.05"}
    }}
    """;

    [Fact]
    public void ParseHyperliquid_Builds_Markets_And_SkipsDelisted()
    {
        var stats = DexExchangeDataService.ParseHyperliquid(HyperliquidJson);

        Assert.NotNull(stats);
        Assert.True(stats!.IsLive);
        Assert.Equal("HYPERLIQUID", stats.Key);
        Assert.DoesNotContain(stats.Markets, m => m.Symbol == "OLD"); // delisted skipped
        var btc = stats.Markets.First(m => m.Symbol == "BTC");
        Assert.Equal(63374.0m, btc.Price);
        Assert.True(btc.Change24hPct > 0m);         // 63374 > 62227
        Assert.Equal(40, stats.MaxLeverage);        // max across markets
        Assert.True(stats.Vol24hUsd > 2_000_000_000m);
    }

    [Fact]
    public void ParseDydx_Builds_Markets_With_Real_Numbers()
    {
        var stats = DexExchangeDataService.ParseDydx(DydxJson);

        Assert.NotNull(stats);
        Assert.Equal("DYDX", stats!.Key);
        var btc = stats.Markets.First(m => m.Symbol == "BTC");
        Assert.Equal(63258.38m, btc.Price);
        Assert.True(btc.Change24hPct > 0m);
        Assert.Equal(50, btc.MaxLeverage);          // 1 / 0.02
        var eth = stats.Markets.First(m => m.Symbol == "ETH");
        Assert.True(eth.Change24hPct < 0m);
        Assert.True(stats.Vol24hUsd > 34_000_000m);
    }

    [Fact]
    public void Parse_Rejects_Bad_Json()
    {
        Assert.Null(DexExchangeDataService.ParseHyperliquid("{}"));
        Assert.Null(DexExchangeDataService.ParseDydx("[]"));
    }

    [Theory]
    [InlineData(1_500_000_000, "$1.5B")]
    [InlineData(380_000_000, "$380M")]
    [InlineData(9_500, "$9.5K")]
    public void FormatUsdCompact_Works(long value, string expected)
    {
        Assert.Equal(expected, DexExchangeDataService.FormatUsdCompact(value));
    }
}
