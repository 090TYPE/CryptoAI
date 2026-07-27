using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Per-tier allowance and the durability of the counter behind it.
///
/// Both halves are money-shaped. A cap that resets on restart is not a cap, and a tier lookup that
/// silently hands everyone the same number turns the price list into a claim the server does not
/// implement — which is exactly the state this replaces.
/// </summary>
public class AiBudgetTierTests
{
    private static AiBudget Budget(long defaultCap = 200_000) => new(defaultCap);

    [Fact]
    public void Tiers_get_the_allowance_the_price_list_promises()
    {
        Assert.Equal(35_000, SettingKeys.DefaultPlanDailyTokens["lite"]);
        Assert.Equal(70_000, SettingKeys.DefaultPlanDailyTokens["pro"]);
        Assert.Equal(105_000, SettingKeys.DefaultPlanDailyTokens["max"]);
    }

    [Fact]
    public void Tier_lookup_is_case_insensitive()
    {
        // The edition is a free-text field in a signed licence written by the bot. "Pro" today,
        // "PRO" after someone edits a plan definition — the allowance must not depend on it.
        Assert.True(SettingKeys.DefaultPlanDailyTokens.ContainsKey("PRO"));
        Assert.Equal("plan.pro.daily_tokens", SettingKeys.PlanDailyTokens(" Pro "));
    }

    [Fact]
    public void A_licence_is_held_to_its_own_cap_not_a_shared_one()
    {
        var budget = Budget();
        budget.Charge("lite-licence", 40_000);

        Assert.False(budget.HasHeadroom("lite-licence", 35_000));   // over the Lite allowance
        Assert.True(budget.HasHeadroom("lite-licence", 105_000));   // would still be fine on Max
    }

    [Fact]
    public void A_misconfigured_cap_falls_back_instead_of_locking_the_customer_out()
    {
        // Zero must not mean "no allowance". A tier someone forgot to configure should behave like
        // it did before tiers existed, not like a suspended account.
        var budget = Budget(defaultCap: 1_000);
        Assert.True(budget.HasHeadroom("someone", 0));
        budget.Charge("someone", 1_000);
        Assert.False(budget.HasHeadroom("someone", 0));
    }

    [Fact]
    public void Seed_restores_a_counter_after_a_restart()
    {
        var budget = Budget();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        budget.Seed("licence", today, 60_000);

        Assert.Equal(60_000, budget.Used("licence"));
        Assert.False(budget.HasHeadroom("licence", 50_000));
    }

    [Fact]
    public void Seed_never_gives_back_allowance_already_spent()
    {
        // The loader runs at startup but requests can already be arriving. A load that overwrote a
        // fresh charge would hand back allowance that was just used, which is the one direction
        // this must never fail in.
        var budget = Budget();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        budget.Charge("licence", 90_000);
        budget.Seed("licence", today, 10_000);

        Assert.Equal(90_000, budget.Used("licence"));
    }

    [Fact]
    public void Seed_for_an_older_day_does_not_carry_into_today()
    {
        var budget = Budget();
        budget.Seed("licence", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), 200_000);

        // Yesterday's spend must not eat today's allowance.
        Assert.Equal(0, budget.Used("licence"));
        Assert.True(budget.HasHeadroom("licence", 1_000));
    }

    [Fact]
    public void Snapshot_returns_what_the_flusher_has_to_write()
    {
        var budget = Budget();
        budget.Charge("a", 100);
        budget.Charge("b", 250);
        budget.Charge("a", 50);

        var snapshot = budget.Snapshot().ToDictionary(e => e.License, e => e.Tokens);

        Assert.Equal(150, snapshot["a"]);
        Assert.Equal(250, snapshot["b"]);
    }
}
