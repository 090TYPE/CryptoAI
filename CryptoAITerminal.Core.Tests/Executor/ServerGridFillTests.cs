using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.2 — reaction to a grid fill, ported from GridBot.PollFills: a filled buy at
/// level i places a sell at i+1; a filled sell at level i places a buy at i-1 and closes a cycle.</summary>
public class ServerGridFillTests
{
    private static readonly decimal[] Levels = { 100m, 125m, 150m, 175m, 200m };

    [Fact]
    public void Filled_buy_places_sell_one_level_up()
    {
        var next = ServerGridStrategy.OnFill(Levels, filledSide: "buy", filledLevel: 1);

        Assert.Equal("sell", next.Order.Side);
        Assert.Equal(150m, next.Order.Price);   // level 2
        Assert.False(next.CycleClosed);
    }

    [Fact]
    public void Filled_sell_places_buy_one_level_down_and_closes_cycle()
    {
        var next = ServerGridStrategy.OnFill(Levels, filledSide: "sell", filledLevel: 2);

        Assert.Equal("buy", next.Order.Side);
        Assert.Equal(125m, next.Order.Price);   // level 1
        Assert.True(next.CycleClosed);
    }

    [Fact]
    public void Filled_buy_at_top_level_has_no_opposite()
    {
        var next = ServerGridStrategy.OnFill(Levels, "buy", filledLevel: 4);  // top → no i+1
        Assert.Null(next.Order.Side);
    }

    [Fact]
    public void Filled_sell_at_bottom_level_has_no_opposite()
    {
        var next = ServerGridStrategy.OnFill(Levels, "sell", filledLevel: 0); // bottom → no i-1
        Assert.Null(next.Order.Side);
    }
}
