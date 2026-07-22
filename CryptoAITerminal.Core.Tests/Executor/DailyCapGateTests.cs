using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4 §4 — per-user daily live-spend cap: block a live order once today's placed live
/// notional plus this order would exceed the cap. Cap 0 = unlimited.</summary>
public class DailyCapGateTests
{
    [Fact]
    public void Zero_cap_is_unlimited()
    {
        var (blocked, _) = DailyCapGate.Check(spentToday: 9999m, orderNotional: 500m, capUsd: 0m);
        Assert.False(blocked);
    }

    [Fact]
    public void Blocks_when_order_would_exceed_cap()
    {
        var (blocked, reason) = DailyCapGate.Check(spentToday: 90m, orderNotional: 20m, capUsd: 100m);
        Assert.True(blocked);
        Assert.Contains("daily", reason);
    }

    [Fact]
    public void Allows_when_within_cap()
    {
        var (blocked, _) = DailyCapGate.Check(spentToday: 50m, orderNotional: 20m, capUsd: 100m);
        Assert.False(blocked);
    }

    [Fact]
    public void Exactly_at_cap_is_allowed()
    {
        var (blocked, _) = DailyCapGate.Check(spentToday: 80m, orderNotional: 20m, capUsd: 100m);
        Assert.False(blocked); // 100 == cap, not over
    }
}
