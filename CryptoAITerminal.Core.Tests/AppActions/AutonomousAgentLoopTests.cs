using System;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AutonomousAgentLoopTests
{
    [Fact]
    public async Task Loop_stops_itself_when_guardrails_trip()
    {
        var rails = new AgentGuardrails { MaxTrades = 3, SessionBudgetUsd = 1_000_000m, LiveEnabled = false };
        var turns = 0;
        // each turn simulates one trade authorization; the 4th call trips MaxTrades and stops the session
        var loop = new AutonomousAgentLoop((objective, ct) =>
        {
            turns++;
            rails.TryAuthorize("BTCUSDT", 1m, out _);
            return Task.FromResult("did a turn");
        }, rails);

        await loop.RunAsync("trade", TimeSpan.Zero, CancellationToken.None);

        Assert.True(rails.Stopped);
        Assert.False(loop.IsRunning);
        Assert.Equal(4, turns); // 3 authorized + 1 that trips the cap
    }

    [Fact]
    public async Task Stop_halts_the_loop()
    {
        var rails = new AgentGuardrails { MaxTrades = 1_000_000, SessionBudgetUsd = 1_000_000m };
        AutonomousAgentLoop? loop = null;
        var turns = 0;
        loop = new AutonomousAgentLoop((objective, ct) =>
        {
            turns++;
            if (turns == 1) loop!.Stop(); // stop after the first turn
            return Task.FromResult("turn");
        }, rails);

        await loop.RunAsync("trade", TimeSpan.Zero, CancellationToken.None);

        Assert.False(loop.IsRunning);
        Assert.True(turns >= 1 && turns <= 2); // stopped promptly
    }

    [Fact]
    public async Task RunAsync_resets_guardrails_at_start()
    {
        var rails = new AgentGuardrails { MaxTrades = 1 };
        rails.TryAuthorize("X", 0m, out _);
        rails.TryAuthorize("X", 0m, out _); // trips → Stopped before start
        Assert.True(rails.Stopped);

        var loop = new AutonomousAgentLoop((o, ct) => Task.FromResult("noop"), rails);
        // Reset happens at start; a short cancellation keeps the idle loop from spinning forever.
        await loop.RunAsync("trade", TimeSpan.Zero, new CancellationTokenSource(50).Token);

        // After the reset-at-start, the pre-run stop is cleared and no authorizing turn ran.
        Assert.Equal(0, rails.TradeCount);
    }
}
