using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class GuardedContextTests
{
    [Fact]
    public async Task Paper_mode_does_not_call_inner_place()
    {
        var inner = new FakeAppActionContext { CurrentSymbol = "BTCUSDT", TicketNotionalUsd = 100m };
        var rails = new AgentGuardrails { LiveEnabled = false, MaxTrades = 5, SessionBudgetUsd = 1000m };
        var g = new GuardedAppActionContext(inner, rails);

        var r = await g.PlaceMarketAsync(true, CancellationToken.None);

        Assert.True(r.Success);
        Assert.DoesNotContain("PlaceMarket:buy", inner.Calls); // simulated, inner not called
        Assert.Equal(1, rails.TradeCount);                     // still counted
    }

    [Fact]
    public async Task Live_authorized_delegates_to_inner()
    {
        var inner = new FakeAppActionContext { CurrentSymbol = "BTCUSDT", TicketNotionalUsd = 100m };
        var rails = new AgentGuardrails { LiveEnabled = true, MaxTrades = 5, SessionBudgetUsd = 1000m };
        var g = new GuardedAppActionContext(inner, rails);

        await g.PlaceMarketAsync(true, CancellationToken.None);

        Assert.Contains("PlaceMarket:buy", inner.Calls);
    }

    [Fact]
    public async Task Over_budget_refuses_and_does_not_call_inner()
    {
        var inner = new FakeAppActionContext { CurrentSymbol = "BTCUSDT", TicketNotionalUsd = 1000m };
        var rails = new AgentGuardrails { LiveEnabled = true, MaxTrades = 5, SessionBudgetUsd = 150m };
        var g = new GuardedAppActionContext(inner, rails);

        var r = await g.PlaceMarketAsync(true, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("guardrail blocked", r.Message);
        Assert.DoesNotContain("PlaceMarket:buy", inner.Calls);
        Assert.True(rails.Stopped);
    }

    [Fact]
    public async Task Reads_pass_through()
    {
        var inner = new FakeAppActionContext();
        var g = new GuardedAppActionContext(inner, new AgentGuardrails());
        await g.GetBalanceUsdtAsync(CancellationToken.None);
        Assert.Contains("GetBalance", inner.Calls);
    }

    [Fact]
    public void Paper_arm_limit_does_not_call_inner()
    {
        var inner = new FakeAppActionContext { CurrentSymbol = "BTCUSDT" };
        var rails = new AgentGuardrails { LiveEnabled = false, MaxTrades = 5 };
        var g = new GuardedAppActionContext(inner, rails);

        var r = g.ArmLimit(true);

        Assert.True(r.Success);
        Assert.DoesNotContain("ArmLimit:buy", inner.Calls);
        Assert.Equal(1, rails.TradeCount);
    }

    [Fact]
    public void Live_arm_limit_delegates_to_inner()
    {
        var inner = new FakeAppActionContext { CurrentSymbol = "BTCUSDT" };
        var rails = new AgentGuardrails { LiveEnabled = true, MaxTrades = 5 };
        var g = new GuardedAppActionContext(inner, rails);

        g.ArmLimit(true);

        Assert.Contains("ArmLimit:buy", inner.Calls);
    }

    [Fact]
    public void Arm_limit_blocked_when_allowlist_excludes_symbol()
    {
        var inner = new FakeAppActionContext { CurrentSymbol = "ETHUSDT" };
        var rails = new AgentGuardrails { LiveEnabled = true, AllowedSymbols = new[] { "BTCUSDT" } };
        var g = new GuardedAppActionContext(inner, rails);

        var r = g.ArmLimit(true);

        Assert.False(r.Success);
        Assert.DoesNotContain("ArmLimit:buy", inner.Calls);
    }

    [Fact]
    public void Paper_arm_stop_loss_does_not_call_inner()
    {
        var inner = new FakeAppActionContext { CurrentSymbol = "BTCUSDT" };
        var rails = new AgentGuardrails { LiveEnabled = false };
        var g = new GuardedAppActionContext(inner, rails);

        var r = g.ArmStopLoss();

        Assert.True(r.Success);
        Assert.DoesNotContain("ArmSl", inner.Calls);
    }
}
