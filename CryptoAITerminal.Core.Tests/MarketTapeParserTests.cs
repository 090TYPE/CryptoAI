using System.Linq;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class MarketTapeParserTests
{
    private const string SampleJson = """
    [
      {"id":101,"price":"65000.50","qty":"0.5","quoteQty":"32500.25","time":1700000000000,"isBuyerMaker":false},
      {"id":102,"price":"64999.00","qty":"1.0","quoteQty":"64999.00","time":1700000001000,"isBuyerMaker":true}
    ]
    """;

    [Fact]
    public void Parse_ReadsAllFields()
    {
        var trades = MarketTapeParser.Parse(SampleJson);

        Assert.Equal(2, trades.Count);
        var first = trades[0];
        Assert.Equal(101, first.Id);
        Assert.Equal(65000.50m, first.Price);
        Assert.Equal(0.5m, first.Quantity);
        Assert.Equal(32500.25m, first.QuoteQty);
    }

    [Fact]
    public void Parse_BuyerMakerFalse_IsBuyAggressor()
    {
        var trades = MarketTapeParser.Parse(SampleJson);

        Assert.Equal("BUY", trades[0].Side);  // isBuyerMaker=false -> taker bought
        Assert.Equal("SELL", trades[1].Side); // isBuyerMaker=true  -> taker sold
    }

    [Fact]
    public void Parse_MissingQuoteQty_DerivesFromPriceTimesQty()
    {
        var trades = MarketTapeParser.Parse("""[{"id":1,"price":"10","qty":"3","time":1700000000000,"isBuyerMaker":false}]""");

        Assert.Single(trades);
        Assert.Equal(30m, trades[0].QuoteQty);
    }

    [Fact]
    public void Parse_SkipsRowsWithNonPositivePriceOrQty()
    {
        var trades = MarketTapeParser.Parse("""[{"id":1,"price":"0","qty":"3","time":1,"isBuyerMaker":false},{"id":2,"price":"10","qty":"0","time":1,"isBuyerMaker":false}]""");

        Assert.Empty(trades);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    public void Parse_BadInput_ReturnsEmpty(string? json)
    {
        Assert.Empty(MarketTapeParser.Parse(json));
    }

    [Fact]
    public void Stats_ComputesBuySellVolumeAndPressure()
    {
        var trades = MarketTapeParser.Parse(SampleJson);

        var stats = MarketTapeStats.Compute(trades, largePrintQuoteThreshold: 0m);

        Assert.Equal(2, stats.TradeCount);
        Assert.Equal(32500.25m, stats.BuyQuoteVolume);
        Assert.Equal(64999.00m, stats.SellQuoteVolume);
        Assert.True(stats.BuyPressure is > 0.33m and < 0.34m); // 32500 / 97499
    }

    [Fact]
    public void Stats_CountsLargePrintsAtOrAboveThreshold()
    {
        var trades = MarketTapeParser.Parse(SampleJson);

        var stats = MarketTapeStats.Compute(trades, largePrintQuoteThreshold: 50_000m);

        Assert.Equal(1, stats.LargePrintCount); // only the 64999 fill clears 50k
    }

    [Fact]
    public void Stats_Empty_HasZeroPressure()
    {
        var stats = MarketTapeStats.Compute(Enumerable.Empty<TapeTrade>(), 1000m);

        Assert.Equal(0, stats.TradeCount);
        Assert.Equal(0m, stats.BuyPressure);
    }
}
