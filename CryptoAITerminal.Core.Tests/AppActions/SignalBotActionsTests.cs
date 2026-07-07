using System.Text.Json;
using System.Threading;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class SignalBotActionsTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async System.Threading.Tasks.Task AddAlert_passes_symbol_price_direction()
    {
        var ctx = new FakeAppActionContext();
        await new AddAlertAction().ExecuteAsync(Args(new { symbol = "BTCUSDT", price = 120000, direction = "above" }), ctx, CancellationToken.None);
        Assert.Contains("Alert:BTCUSDT:120000:above", ctx.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task ApplySignal_forwards_symbol_and_side()
    {
        var ctx = new FakeAppActionContext();
        await new ApplySignalAction().ExecuteAsync(Args(new { symbol = "ETHUSDT", side = "long" }), ctx, CancellationToken.None);
        Assert.Contains("ApplySignal:ETHUSDT:long", ctx.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task GridBot_forwards_bounds()
    {
        var ctx = new FakeAppActionContext();
        await new ConfigureGridBotAction().ExecuteAsync(Args(new { symbol = "BTCUSDT", lower = 90000, upper = 110000, levels = 8 }), ctx, CancellationToken.None);
        Assert.Contains("Grid:BTCUSDT:90000:110000:8", ctx.Calls);
    }
}
