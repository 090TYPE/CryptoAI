using System.Text.Json;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AgentActionTrayTests
{
    private static ActionProposal P() => new("trade.place_market", "PLACE MARKET BUY", JsonSerializer.SerializeToElement(new { side = "buy" }));

    [Fact]
    public async Task Enqueue_adds_pending_and_approve_executes()
    {
        var executed = 0;
        var tray = new AgentActionTrayViewModel(p => { executed++; return Task.FromResult(AppActionResult.Ok("done")); });
        tray.Enqueue(P());
        Assert.Single(tray.Pending);

        await tray.ApproveAsync(tray.Pending[0]);
        Assert.Equal(1, executed);
        Assert.Empty(tray.Pending);
    }

    [Fact]
    public void Reject_removes_without_executing()
    {
        var executed = 0;
        var tray = new AgentActionTrayViewModel(p => { executed++; return Task.FromResult(AppActionResult.Ok("done")); });
        tray.Enqueue(P());
        tray.Reject(tray.Pending[0]);
        Assert.Empty(tray.Pending);
        Assert.Equal(0, executed);
    }
}
