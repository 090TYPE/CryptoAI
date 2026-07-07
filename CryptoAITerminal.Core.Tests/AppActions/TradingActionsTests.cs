using System.Text.Json;
using System.Threading;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class TradingActionsTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async System.Threading.Tasks.Task SetTicket_applies_symbol_side_and_usd()
    {
        var ctx = new FakeAppActionContext();
        var r = await new SetTicketAction().ExecuteAsync(
            Args(new { symbol = "ETHUSDT", side = "long", usd = 500, leverage = 5, mode = "futures" }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("SetSymbol:ETHUSDT", ctx.Calls);
        Assert.Contains("SetSide:buy", ctx.Calls);
        Assert.Contains("SetUsd:500", ctx.Calls);
        Assert.Contains("SetLeverage:5", ctx.Calls);
        Assert.Contains("SetMarketMode:Futures", ctx.Calls);
    }

    [Fact]
    public void PlaceMarket_is_mutating() => Assert.True(new PlaceMarketAction().IsMutating);

    [Fact]
    public async System.Threading.Tasks.Task ArmLimit_sets_price_then_arms()
    {
        var ctx = new FakeAppActionContext();
        var r = await new ArmLimitAction().ExecuteAsync(Args(new { side = "buy", price = 3200 }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("SetLimit:3200", ctx.Calls);
        Assert.Contains("ArmLimit:buy", ctx.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task PlaceMarket_routes_side()
    {
        var ctx = new FakeAppActionContext();
        await new PlaceMarketAction().ExecuteAsync(Args(new { side = "sell" }), ctx, CancellationToken.None);
        Assert.Contains("PlaceMarket:sell", ctx.Calls);
    }
}
