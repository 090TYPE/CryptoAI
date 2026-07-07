using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AppAgentServiceTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task Confirm_mode_queues_mutating_action_and_does_not_execute()
    {
        var ctx = new FakeAppActionContext();
        var proposals = new List<(string id, string preview)>();
        var svc = new AppAgentService(AppActionRegistry.Default(), ctx,
            proposalSink: p => { proposals.Add((p.ActionId, p.Preview)); return Task.FromResult(AppActionResult.Ok("queued")); },
            autoMode: () => false);

        var result = await svc.InvokeActionToolAsync("trade_place_market", Args(new { side = "buy" }), CancellationToken.None);

        Assert.Single(proposals);
        Assert.Equal("trade.place_market", proposals[0].id);
        Assert.DoesNotContain("PlaceMarket:buy", ctx.Calls);
        Assert.Contains("queued", result);
    }

    [Fact]
    public async Task Auto_mode_executes_mutating_action_immediately()
    {
        var ctx = new FakeAppActionContext();
        var svc = new AppAgentService(AppActionRegistry.Default(), ctx,
            proposalSink: _ => Task.FromResult(AppActionResult.Ok("queued")),
            autoMode: () => true);

        await svc.InvokeActionToolAsync("trade_place_market", Args(new { side = "buy" }), CancellationToken.None);

        Assert.Contains("PlaceMarket:buy", ctx.Calls);
    }

    [Fact]
    public async Task Read_action_executes_regardless_of_mode()
    {
        var ctx = new FakeAppActionContext();
        var svc = new AppAgentService(AppActionRegistry.Default(), ctx,
            proposalSink: _ => Task.FromResult(AppActionResult.Ok("queued")),
            autoMode: () => false);

        await svc.InvokeActionToolAsync("read_balance", Args(new { }), CancellationToken.None);

        Assert.Contains("GetBalance", ctx.Calls);
    }
}
