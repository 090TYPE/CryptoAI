using CryptoAITerminal.Gateway.Base;

namespace CryptoAITerminal.Core.Tests.Gateway;

/// <summary>
/// 🧹-3 — the single timeframe token list all eight gateways now parse through. Each used to carry
/// its own copy with a silent default (OneHour in most, OneMinute in Binance), so an unknown token
/// produced candles of the wrong interval instead of an error.
/// </summary>
public class TimeframeParserTests
{
    [Theory]
    [InlineData("1M",  1)]
    [InlineData("3M",  3)]
    [InlineData("5M",  5)]
    [InlineData("15M", 15)]
    [InlineData("30M", 30)]
    [InlineData("1H",  60)]
    [InlineData("2H",  120)]
    [InlineData("4H",  240)]
    [InlineData("6H",  360)]
    [InlineData("8H",  480)]
    [InlineData("12H", 720)]
    [InlineData("1D",  1440)]
    [InlineData("1W",  10080)]
    [InlineData("1MN", 43200)]
    public void Parse_maps_every_supported_token_to_its_length(string token, int expectedMinutes)
    {
        Assert.Equal(expectedMinutes, (int)TimeframeParser.Parse(token).TotalMinutes);
    }

    [Theory]
    [InlineData("4h")]
    [InlineData(" 4H ")]
    [InlineData("4H")]
    public void Parse_ignores_case_and_surrounding_space(string token)
    {
        Assert.Equal(TimeSpan.FromHours(4), TimeframeParser.Parse(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1MO")]
    [InlineData("ALL")]
    [InlineData("7D")]
    public void Parse_throws_instead_of_guessing_an_interval(string? token)
    {
        Assert.Throws<ArgumentException>(() => TimeframeParser.Parse(token));
    }

    [Fact]
    public void TryParse_reports_failure_without_throwing()
    {
        Assert.False(TimeframeParser.TryParse("nonsense", out _));
        Assert.True(TimeframeParser.TryParse("1d", out var interval));
        Assert.Equal(TimeSpan.FromDays(1), interval);
    }

    [Fact]
    public void SupportedTokens_covers_the_whole_map()
    {
        Assert.Equal(14, TimeframeParser.SupportedTokens.Count);
    }
}
