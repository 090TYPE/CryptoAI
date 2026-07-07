using System.Text.Json;
using System.Threading;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class NavigationActionsTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async System.Threading.Tasks.Task Goto_calls_NavigateTo_with_section()
    {
        var ctx = new FakeAppActionContext();
        var action = new NavGotoAction();
        var r = await action.ExecuteAsync(Args(new { section = "trading" }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("NavigateTo:trading", ctx.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Goto_is_not_mutating()
        => Assert.False(new NavGotoAction().IsMutating);

    [Fact]
    public async System.Threading.Tasks.Task ReadPositions_returns_count()
    {
        var ctx = new FakeAppActionContext
        {
            Positions = new[] { new ActionPositionLine("BTCUSDT", 0.1m, 100m, 110m, 1m) }
        };
        var r = await new ReadPositionsAction().ExecuteAsync(Args(new { }), ctx, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Contains("GetPositions", ctx.Calls);
    }
}
