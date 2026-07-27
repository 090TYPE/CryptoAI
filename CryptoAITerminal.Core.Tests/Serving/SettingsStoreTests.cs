using CryptoAITerminal.Server.Data;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Coercion rules for runtime settings. These are the whole risk surface of the store that does
/// not need a database: every value arrives as a string typed by a human into the admin screen,
/// and the contract is that a bad one falls back to the caller's default instead of throwing on
/// the hot path — a typo in a batch size must not take the AI proxy down.
/// </summary>
public class SettingsStoreTests
{
    [Theory]
    [InlineData("5", 5)]
    [InlineData(" 5 ", 5)]        // the admin screen does not trim for us
    [InlineData("not a number", 42)]
    [InlineData("", 42)]
    [InlineData(null, 42)]
    [InlineData("0", 42)]         // zero would disable the knob, which is never the intent
    [InlineData("-3", 42)]
    [InlineData("2147483648", 42)] // one past int.MaxValue
    public void AsInt_falls_back_on_anything_unusable(string? raw, int expected) =>
        Assert.Equal(expected, SettingsStore.AsInt(raw, 42));

    [Theory]
    [InlineData("200000", 200_000L)]
    [InlineData("9999999999999", 9_999_999_999_999L)]  // beyond int, must still parse
    [InlineData("0", 1000L)]
    [InlineData("junk", 1000L)]
    public void AsLong_handles_values_past_int_range(string raw, long expected) =>
        Assert.Equal(expected, SettingsStore.AsLong(raw, 1000L));

    [Theory]
    [InlineData("15.5", 15.5)]
    [InlineData("15", 15.0)]
    [InlineData("0", 7.0)]
    [InlineData("nope", 7.0)]
    public void AsDecimal_uses_invariant_culture(string raw, double expected) =>
        Assert.Equal((decimal)expected, SettingsStore.AsDecimal(raw, 7m));

    [Fact]
    public void AsDecimal_rejects_comma_decimals()
    {
        // The admin API speaks one number format. Accepting "15,5" would mean the same string
        // parses differently depending on the server's locale, which is how a threshold silently
        // becomes 155.
        Assert.Equal(7m, SettingsStore.AsDecimal("15,5", 7m));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    [InlineData(" On ", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    public void AsBool_accepts_what_a_human_would_type(string raw, bool expected) =>
        Assert.Equal(expected, SettingsStore.AsBool(raw, fallback: !expected));

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData(null)]
    public void AsBool_falls_back_on_anything_else(string? raw)
    {
        // Both directions: the fallback must win regardless of which way it points, otherwise an
        // unparsable value silently means "false" and a kill switch reads as disarmed.
        Assert.True(SettingsStore.AsBool(raw, fallback: true));
        Assert.False(SettingsStore.AsBool(raw, fallback: false));
    }

    [Fact]
    public void Ttl_is_short_enough_to_feel_immediate()
    {
        // The design promise is "change it and it applies", not "change it and wait". If this
        // ever grows past half a minute the admin screen has to start saying so.
        Assert.InRange(SettingsStore.Ttl.TotalSeconds, 1, 30);
    }
}
