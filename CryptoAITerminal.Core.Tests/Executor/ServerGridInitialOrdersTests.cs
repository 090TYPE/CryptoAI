using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.2 — which grid orders to place at start, ported from GridBot.PlaceInitialOrders:
/// buys on cells fully below price; sells above only on futures (short entries).</summary>
public class ServerGridInitialOrdersTests
{
    private static readonly decimal[] Levels = { 100m, 125m, 150m, 175m, 200m };

    [Fact]
    public void Spot_places_buys_below_price_only()
    {
        var orders = ServerGridStrategy.InitialOrders(Levels, currentPrice: 150m, futures: false);

        Assert.All(orders, o => Assert.Equal("buy", o.Side));
        Assert.Equal(new[] { 100m, 125m }, orders.Select(o => o.Price).ToArray());
    }

    [Fact]
    public void Futures_adds_sells_above_price()
    {
        var orders = ServerGridStrategy.InitialOrders(Levels, currentPrice: 150m, futures: true);

        Assert.Equal(new[] { 100m, 125m }, orders.Where(o => o.Side == "buy").Select(o => o.Price).ToArray());
        Assert.Equal(new[] { 175m, 200m }, orders.Where(o => o.Side == "sell").Select(o => o.Price).ToArray());
    }

    [Fact]
    public void Price_at_top_places_all_buys()
    {
        var orders = ServerGridStrategy.InitialOrders(Levels, currentPrice: 200m, futures: false);

        Assert.Equal(4, orders.Count);
        Assert.All(orders, o => Assert.Equal("buy", o.Side));
    }
}
