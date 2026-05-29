using CryptoAITerminal.Gateway.KuCoin;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class KucoinSymbolHelperTests
{
    // ── ToSpotSymbol ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("BTCUSDT", "BTC-USDT")]
    [InlineData("ETHUSDT", "ETH-USDT")]
    [InlineData("SOLUSDC", "SOL-USDC")]
    [InlineData("ETHBTC",  "ETH-BTC")]
    [InlineData("btcusdt", "BTC-USDT")]  // case-insensitive
    public void ToSpotSymbol_InsertsDashBetweenBaseAndQuote(string input, string expected)
    {
        Assert.Equal(expected, KucoinSymbolHelper.ToSpotSymbol(input));
    }

    [Fact]
    public void ToSpotSymbol_AlreadyDashed_PassesThroughUppercase()
    {
        Assert.Equal("BTC-USDT", KucoinSymbolHelper.ToSpotSymbol("btc-usdt"));
    }

    [Fact]
    public void ToSpotSymbol_EmptyOrUnknownQuote_LeavesUntouched()
    {
        Assert.Equal("", KucoinSymbolHelper.ToSpotSymbol(""));
        // Незнакомый quote → возвращается uppercase без дефиса.
        Assert.Equal("FOOBAR", KucoinSymbolHelper.ToSpotSymbol("foobar"));
    }

    [Fact]
    public void ToSpotSymbol_PrefersLongestQuoteMatch()
    {
        // USDT длиннее чем USDC; должен правильно сматчиться.
        Assert.Equal("USDT-USDC", KucoinSymbolHelper.ToSpotSymbol("USDTUSDC"));
    }

    // ── ToFuturesSymbol ────────────────────────────────────────────────────────

    [Fact]
    public void ToFuturesSymbol_BtcMappedToXbtWithSuffixM()
    {
        Assert.Equal("XBTUSDTM", KucoinSymbolHelper.ToFuturesSymbol("BTCUSDT"));
    }

    [Theory]
    [InlineData("ETHUSDT", "ETHUSDTM")]
    [InlineData("SOLUSDT", "SOLUSDTM")]
    [InlineData("BTC-USDT", "XBTUSDTM")]   // принимает и dashed-вход
    public void ToFuturesSymbol_AppendsM(string input, string expected)
    {
        Assert.Equal(expected, KucoinSymbolHelper.ToFuturesSymbol(input));
    }

    [Fact]
    public void ToFuturesSymbol_AlreadyHasM_DoesNotDoubleAppend()
    {
        Assert.Equal("ETHUSDTM", KucoinSymbolHelper.ToFuturesSymbol("ETHUSDTM"));
    }

    [Fact]
    public void ToFuturesSymbol_Empty_ReturnsEmpty()
    {
        Assert.Equal("", KucoinSymbolHelper.ToFuturesSymbol(""));
    }

    // ── FromKucoinSymbol (обратное преобразование) ─────────────────────────────

    [Theory]
    [InlineData("BTC-USDT", "BTCUSDT")]
    [InlineData("ETH-USDT", "ETHUSDT")]
    [InlineData("XBTUSDTM", "BTCUSDT")]   // perpetual → spot-form
    [InlineData("ETHUSDTM", "ETHUSDT")]
    public void FromKucoinSymbol_NormalizesToTerminalForm(string input, string expected)
    {
        Assert.Equal(expected, KucoinSymbolHelper.FromKucoinSymbol(input));
    }

    [Fact]
    public void FromKucoinSymbol_Empty_ReturnsEmpty()
    {
        Assert.Equal("", KucoinSymbolHelper.FromKucoinSymbol(""));
    }
}
