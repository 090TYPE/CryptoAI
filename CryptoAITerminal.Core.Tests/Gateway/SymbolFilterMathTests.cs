using System.Globalization;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Base;

namespace CryptoAITerminal.Core.Tests.Gateway;

/// <summary>
/// 🔴-13 — rounding an order onto the venue's tick/lot grid. The grid bot used to derive its tick
/// size from the number of decimals the user typed, so <c>(Upper - Lower) / GridLevels</c> produced
/// 1428.571428571428571428571429 and Binance answered -1013.
/// </summary>
public class SymbolFilterMathTests
{
    // xUnit's InlineData cannot carry decimal literals, so the cases travel as strings — parsed
    // invariantly, because the developer box runs a comma-decimal locale.
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    // ── FloorToStep ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.123456789", "0.001", "0.123")]
    [InlineData("1.9999",      "0.5",   "1.5")]
    [InlineData("7",           "3",     "6")]
    [InlineData("0.3",         "0.1",   "0.3")]   // exact multiple stays put — decimal, not double
    [InlineData("0.00000009",  "0.0001", "0")]    // below one step → nothing tradable
    public void FloorToStep_rounds_down_to_the_lot_grid(string value, string step, string expected)
    {
        Assert.Equal(D(expected), SymbolFilterMath.FloorToStep(D(value), D(step)));
    }

    [Fact]
    public void FloorToStep_result_is_an_exact_multiple_of_the_step()
    {
        var result = SymbolFilterMath.FloorToStep(1428.571428571428571428571429m, 0.01m);
        Assert.Equal(1428.57m, result);
        Assert.Equal(0m, result % 0.01m);
    }

    [Fact]
    public void FloorToStep_without_a_published_step_leaves_the_value_alone()
    {
        // Zero means "the venue publishes no lot rule" — rounding to zero would kill the order.
        Assert.Equal(1.23456789m, SymbolFilterMath.FloorToStep(1.23456789m, 0m));
        Assert.Equal(1.23456789m, SymbolFilterMath.FloorToStep(1.23456789m, -1m));
    }

    [Fact]
    public void FloorToStep_of_a_non_positive_quantity_is_zero()
    {
        Assert.Equal(0m, SymbolFilterMath.FloorToStep(0m, 0.1m));
        Assert.Equal(0m, SymbolFilterMath.FloorToStep(-5m, 0.1m));
    }

    // ── CeilToStep ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.1234", "0.001", "0.124")]
    [InlineData("0.123",  "0.001", "0.123")]  // already on the grid — do not add a step
    [InlineData("7",      "3",     "9")]
    public void CeilToStep_rounds_up_to_the_lot_grid(string value, string step, string expected)
    {
        Assert.Equal(D(expected), SymbolFilterMath.CeilToStep(D(value), D(step)));
    }

    // ── RoundToTick ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundToTick_down_never_returns_a_price_above_the_input()
    {
        Assert.Equal(1428.57m, SymbolFilterMath.RoundToTick(1428.571428571428571428571429m, 0.01m, up: false));
    }

    [Fact]
    public void RoundToTick_up_never_returns_a_price_below_the_input()
    {
        Assert.Equal(1428.58m, SymbolFilterMath.RoundToTick(1428.571428571428571428571429m, 0.01m, up: true));
    }

    [Fact]
    public void RoundToTick_leaves_a_price_already_on_the_grid_untouched_in_both_directions()
    {
        Assert.Equal(100.50m, SymbolFilterMath.RoundToTick(100.50m, 0.01m, up: false));
        Assert.Equal(100.50m, SymbolFilterMath.RoundToTick(100.50m, 0.01m, up: true));
    }

    [Fact]
    public void RoundToTick_handles_ticks_coarser_than_one()
    {
        // BTC perps on some venues quote in whole dollars, or in 0.5.
        Assert.Equal(67000m,   SymbolFilterMath.RoundToTick(67000.49m, 1m,   up: false));
        Assert.Equal(67001m,   SymbolFilterMath.RoundToTick(67000.49m, 1m,   up: true));
        Assert.Equal(67000.5m, SymbolFilterMath.RoundToTick(67000.49m, 0.5m, up: true));
    }

    [Fact]
    public void RoundToTick_without_a_published_tick_leaves_the_price_alone()
    {
        Assert.Equal(123.456789m, SymbolFilterMath.RoundToTick(123.456789m, 0m, up: true));
        Assert.Equal(123.456789m, SymbolFilterMath.RoundToTick(123.456789m, 0m, up: false));
    }

    // ── MeetsMinNotional ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("10", "0.5", "5", true)]    // exactly on the floor passes
    [InlineData("10", "0.4", "5", false)]
    [InlineData("10", "0.6", "5", true)]
    [InlineData("10", "0.4", "0", true)]    // venue publishes no floor
    public void MeetsMinNotional_compares_price_times_quantity(string price, string qty, string floor, bool expected)
    {
        Assert.Equal(expected, SymbolFilterMath.MeetsMinNotional(D(price), D(qty), D(floor)));
    }

    [Fact]
    public void MeetsMinNotional_passes_when_the_gateway_could_not_resolve_filters()
    {
        Assert.True(SymbolFilterMath.MeetsMinNotional(10m, 0.0001m, (SymbolFilters?)null));
    }

    // ── SymbolFilters-aware wrappers ───────────────────────────────────────────

    private static SymbolFilters Btc() => new("BTCUSDT", TickSize: 0.01m, StepSize: 0.00001m,
                                              MinQuantity: 0.0001m, MinNotional: 5m);

    [Fact]
    public void NormalizeQuantity_floors_to_the_step()
    {
        Assert.Equal(0.12345m, SymbolFilterMath.NormalizeQuantity(0.123456789m, Btc()));
    }

    [Fact]
    public void NormalizeQuantity_returns_zero_below_the_minimum_instead_of_a_smaller_order()
    {
        // 0.00005 is a valid multiple of the step but under minQty — the venue would reject it,
        // so the caller has to see "cannot trade this size", not a silently shrunk order.
        Assert.Equal(0m, SymbolFilterMath.NormalizeQuantity(0.00005m, Btc()));
    }

    [Fact]
    public void NormalizePrice_snaps_to_the_tick_in_the_requested_direction()
    {
        Assert.Equal(1428.57m, SymbolFilterMath.NormalizePrice(1428.5714m, Btc(), up: false));
        Assert.Equal(1428.58m, SymbolFilterMath.NormalizePrice(1428.5714m, Btc(), up: true));
    }

    [Fact]
    public void Null_filters_pass_every_value_through_unchanged()
    {
        Assert.Equal(1428.5714m, SymbolFilterMath.NormalizePrice(1428.5714m, null, up: false));
        Assert.Equal(0.123456789m, SymbolFilterMath.NormalizeQuantity(0.123456789m, null));
    }

    // ── SymbolFilters.Create ───────────────────────────────────────────────────

    [Fact]
    public void Create_collapses_missing_and_non_positive_venue_values_to_no_constraint()
    {
        var filters = SymbolFilters.Create("BTCUSDT", 0.01m, null, -1m, 0m);

        Assert.Equal(0.01m, filters.TickSize);
        Assert.Equal(0m, filters.StepSize);
        Assert.Equal(0m, filters.MinQuantity);
        Assert.Equal(0m, filters.MinNotional);
        Assert.False(filters.IsEmpty);
    }

    [Fact]
    public void Create_with_nothing_published_is_empty()
    {
        Assert.True(SymbolFilters.Create("FOOUSDT", null, null, null, null).IsEmpty);
    }
}
