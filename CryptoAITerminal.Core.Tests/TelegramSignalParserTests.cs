using System.Linq;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class TelegramSignalParserTests
{
    [Fact]
    public void Parse_FullLongSignal_ExtractsEveryField()
    {
        const string message =
            "🚀 LONG $BTC\n" +
            "Entry: 65000\n" +
            "Targets: 66000, 67000, 68000\n" +
            "Stop: 63000\n" +
            "Leverage: 10x";

        var signal = TelegramSignalParser.Parse(message);

        Assert.True(signal.IsValid);
        Assert.Equal("BTC", signal.Symbol);
        Assert.Equal("BUY", signal.Side);
        Assert.Equal(65000m, signal.Entry);
        Assert.Equal(new[] { 66000m, 67000m, 68000m }, signal.Targets.ToArray());
        Assert.Equal(63000m, signal.StopLoss);
        Assert.Equal(10, signal.Leverage);
    }

    [Fact]
    public void Parse_ShortPairWithTpLabels_NormalizesSymbolAndSkipsTpIndices()
    {
        const string message =
            "#ETH/USDT SHORT\n" +
            "Entry 3200\n" +
            "TP1 3100\n" +
            "TP2 3000\n" +
            "SL 3350";

        var signal = TelegramSignalParser.Parse(message);

        Assert.True(signal.IsValid);
        Assert.Equal("ETHUSDT", signal.Symbol);
        Assert.Equal("SELL", signal.Side);
        Assert.Equal(3200m, signal.Entry);
        Assert.Equal(new[] { 3100m, 3000m }, signal.Targets.ToArray());
        Assert.Equal(3350m, signal.StopLoss);
        Assert.Null(signal.Leverage);
    }

    [Fact]
    public void Parse_ConcatenatedPair_SplitsBaseAndQuote()
    {
        var signal = TelegramSignalParser.Parse("SOLUSDT LONG 20x");

        Assert.True(signal.IsValid);
        Assert.Equal("SOLUSDT", signal.Symbol);
        Assert.Equal("BUY", signal.Side);
        Assert.Equal(20, signal.Leverage);
    }

    [Fact]
    public void Parse_ThousandsSeparator_IsStrippedButListCommasKept()
    {
        var signal = TelegramSignalParser.Parse("LONG $BTC\nEntry: 65,000\nTargets: 66,000, 67,000");

        Assert.Equal(65000m, signal.Entry);
        Assert.Equal(new[] { 66000m, 67000m }, signal.Targets.ToArray());
    }

    [Fact]
    public void Parse_EntryRange_TakesFirstPrice()
    {
        var signal = TelegramSignalParser.Parse("LONG $BTC\nEntry: 65000 - 66000");

        Assert.Equal(65000m, signal.Entry);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var signal = TelegramSignalParser.Parse("long $btc entry 100 sl 90");

        Assert.True(signal.IsValid);
        Assert.Equal("BTC", signal.Symbol);
        Assert.Equal("BUY", signal.Side);
        Assert.Equal(100m, signal.Entry);
        Assert.Equal(90m, signal.StopLoss);
    }

    [Fact]
    public void Parse_StandaloneLeverageX_IsRead()
    {
        var signal = TelegramSignalParser.Parse("SHORT BTCUSDT 25X entry 60000");

        Assert.Equal(25, signal.Leverage);
        Assert.Equal("SELL", signal.Side);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrNull_IsInvalid(string? message)
    {
        Assert.False(TelegramSignalParser.Parse(message).IsValid);
    }

    [Fact]
    public void Parse_NoDirection_IsInvalid()
    {
        var signal = TelegramSignalParser.Parse("$BTC is pumping toward 70000 soon");

        Assert.False(signal.IsValid);
    }

    [Fact]
    public void Parse_ContradictoryDirections_IsInvalid()
    {
        var signal = TelegramSignalParser.Parse("I will LONG and SHORT $BTC");

        Assert.False(signal.IsValid);
    }

    [Fact]
    public void Parse_NoSymbol_IsInvalid()
    {
        var signal = TelegramSignalParser.Parse("LONG now, entry 100, target 120");

        Assert.False(signal.IsValid);
    }

    [Fact]
    public void Parse_MissingOptionalFields_StillValidWithSymbolAndSide()
    {
        var signal = TelegramSignalParser.Parse("LONG $DOGE");

        Assert.True(signal.IsValid);
        Assert.Equal("DOGE", signal.Symbol);
        Assert.Null(signal.Entry);
        Assert.Empty(signal.Targets);
        Assert.Null(signal.StopLoss);
        Assert.Null(signal.Leverage);
    }

    [Fact]
    public void Parse_PreservesRawText()
    {
        const string raw = "LONG $BTC entry 100";
        Assert.Equal(raw, TelegramSignalParser.Parse(raw).RawText);
    }
}
