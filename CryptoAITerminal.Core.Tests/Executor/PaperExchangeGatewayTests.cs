using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.2 — the paper gateway simulates limit fills by price cross so a grid bot can be
/// driven end-to-end without a real exchange: a resting buy fills once price drops to/through it,
/// a resting sell fills once price rises to/through it.</summary>
public class PaperExchangeGatewayTests
{
    private static Order Buy(string id, decimal price) => new()
    { Id = id, Side = OrderSide.Buy, Type = OrderType.Limit, Price = price };
    private static Order Sell(string id, decimal price) => new()
    { Id = id, Side = OrderSide.Sell, Type = OrderType.Limit, Price = price };

    [Fact]
    public async Task Resting_buy_below_price_is_still_open()
    {
        var gw = new PaperExchangeGateway(price: 150m, new[] { Buy("b1", 100m) });
        var open = await gw.GetOpenOrdersAsync("BTCUSDT");
        Assert.Single(open);
        Assert.Equal("b1", open[0].Id);
    }

    [Fact]
    public async Task Buy_fills_when_price_reaches_it()
    {
        var gw = new PaperExchangeGateway(price: 100m, new[] { Buy("b1", 100m), Buy("b2", 90m) });
        var open = await gw.GetOpenOrdersAsync();
        // price 100: b1 (@100) crossed → filled/gone; b2 (@90) still resting
        Assert.DoesNotContain(open, o => o.Id == "b1");
        Assert.Contains(open, o => o.Id == "b2");
    }

    [Fact]
    public async Task Sell_fills_when_price_rises_to_it()
    {
        var gw = new PaperExchangeGateway(price: 150m, new[] { Sell("s1", 150m), Sell("s2", 175m) });
        var open = await gw.GetOpenOrdersAsync();
        Assert.DoesNotContain(open, o => o.Id == "s1"); // @150 crossed
        Assert.Contains(open, o => o.Id == "s2");       // @175 still above
    }

    [Fact]
    public async Task Placed_order_gets_a_paper_id_and_rests_this_tick()
    {
        var gw = new PaperExchangeGateway(price: 150m, Array.Empty<Order>());
        var placed = await gw.PlaceOrderAsync(new Order { Symbol = "BTCUSDT", Side = OrderSide.Buy, Type = OrderType.Limit, Price = 120m });
        Assert.StartsWith("paper-", placed.Id);
        var open = await gw.GetOpenOrdersAsync();
        Assert.Contains(open, o => o.Id == placed.Id); // just placed @120, price 150 → not crossed
    }
}
