using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Russian plurals for the subscription countdown. Worth testing because the naive version — a
/// switch on ranges — is wrong exactly where a monthly plan lands after every renewal, and wrong
/// text in a paid product reads as carelessness about everything else.
/// </summary>
public class SubscriptionTextTests
{
    [Theory]
    [InlineData(1, "день")]
    [InlineData(2, "дня")]
    [InlineData(4, "дня")]
    [InlineData(5, "дней")]
    [InlineData(7, "дней")]
    [InlineData(20, "дней")]
    [InlineData(21, "день")]     // the case a range-based switch gets wrong
    [InlineData(22, "дня")]
    [InlineData(25, "дней")]
    [InlineData(30, "дней")]
    [InlineData(31, "день")]
    public void Day_word_follows_the_rule(int days, string expected) =>
        Assert.Equal(expected, SubscriptionText.DayWord(days));

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Eleven_to_fourteen_are_the_exception(int days)
    {
        // These end in 1–4 but take the plural anyway. A digit-only check produces
        // "остался 11 день", which is the tell that nobody read the string.
        Assert.Equal("дней", SubscriptionText.DayWord(days));
    }

    [Fact]
    public void The_verb_agrees_with_the_number()
    {
        Assert.Equal("остался 1 день", SubscriptionText.DaysLeft(1));
        Assert.Equal("осталось 3 дня", SubscriptionText.DaysLeft(3));
        Assert.Equal("осталось 21 день", SubscriptionText.DaysLeft(21));
    }

    [Fact]
    public void Expired_and_unknown_say_so_plainly()
    {
        Assert.Equal("истекла", SubscriptionText.DaysLeft(0));
        Assert.Equal("истекла", SubscriptionText.DaysLeft(-5));
        Assert.Equal("—", SubscriptionText.DaysLeft(null));
    }

    [Fact]
    public void The_expiry_notice_says_what_actually_happens()
    {
        // Not "доступ ограничен" — the terminal keeps working on its own calculations, and saying
        // so is the difference between a warning and a scare.
        Assert.Contains("встроенных расчётах", SubscriptionText.ExpiryNotice(0));
        Assert.Contains("завтра", SubscriptionText.ExpiryNotice(1));
        Assert.Contains("3 дня", SubscriptionText.ExpiryNotice(3));
        Assert.Equal("", SubscriptionText.ExpiryNotice(null));
    }
}
