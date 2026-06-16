using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class TelegramChannelSignalMapperTests
{
    private static ParsedTelegramSignal Parse(string text) => TelegramSignalParser.Parse(text);

    [Fact]
    public void DescribeSignal_FullSignal_IncludesEveryField()
    {
        var signal = Parse("LONG $BTC\nEntry: 65000\nTargets: 66000, 67000\nStop: 63000\nLeverage: 10x");

        var text = TelegramChannelSignalMapper.DescribeSignal(signal, "Whale Calls");

        Assert.Equal("from Whale Calls • entry 65000 • TP 66000/67000 • SL 63000 • 10x", text);
    }

    [Fact]
    public void DescribeSignal_OmitsMissingFields()
    {
        var signal = Parse("LONG $DOGE");

        var text = TelegramChannelSignalMapper.DescribeSignal(signal, "Calls");

        Assert.Equal("from Calls", text);
    }

    [Fact]
    public void DescribeSignal_BlankChannelTitle_IsNotShownAsEmptyPrefix()
    {
        var signal = Parse("LONG $BTC\nEntry: 100");

        var text = TelegramChannelSignalMapper.DescribeSignal(signal, "   ");

        Assert.Equal("entry 100", text);
    }

    [Fact]
    public void DescribeSignal_FractionalPrices_AreNotPaddedWithTrailingZeros()
    {
        var signal = Parse("LONG $PEPE\nEntry: 0.0125\nStop: 0.01");

        var text = TelegramChannelSignalMapper.DescribeSignal(signal, "");

        Assert.Equal("entry 0.0125 • SL 0.01", text);
    }

    [Fact]
    public void ChannelMessage_PreservesAllFields()
    {
        var msg = new TelegramChannelMessage(123, "Alpha", 9, "LONG $BTC");

        Assert.Equal(123, msg.ChannelId);
        Assert.Equal("Alpha", msg.ChannelTitle);
        Assert.Equal(9, msg.MessageId);
        Assert.Equal("LONG $BTC", msg.Text);
    }
}
