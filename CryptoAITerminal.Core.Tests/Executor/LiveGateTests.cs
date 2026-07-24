using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.5 — kill-switch: live orders are blocked when the user (or global switch) has
/// halted live trading; paper is never gated.</summary>
public class LiveGateTests
{
    [Fact]
    public void Paper_is_never_blocked_even_when_halted()
    {
        var (blocked, _) = LiveGate.Check("paper", halted: true);
        Assert.False(blocked);
    }

    [Fact]
    public void Live_blocked_when_halted()
    {
        var (blocked, reason) = LiveGate.Check("live", halted: true);
        Assert.True(blocked);
        Assert.Contains("kill-switch", reason);
    }

    [Fact]
    public void Live_allowed_when_not_halted()
    {
        var (blocked, _) = LiveGate.Check("live", halted: false);
        Assert.False(blocked);
    }
}
