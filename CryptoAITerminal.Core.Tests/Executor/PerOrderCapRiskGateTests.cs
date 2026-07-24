using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.1 risk gate — a hard per-order USD notional cap before any live order.</summary>
public class PerOrderCapRiskGateTests
{
    private static BotIntent Buy(decimal? notional) => new(
        Guid.NewGuid(), Guid.NewGuid(), "binance", "spot", "live", "buy", "BTC", "USDT", notional, null, "cid");

    [Fact]
    public void Notional_within_cap_is_allowed()
    {
        var gate = new PerOrderCapRiskGate(maxNotionalUsd: 500m);

        var (ok, reason) = gate.Check(Buy(100m));

        Assert.True(ok);
        Assert.Null(reason);
    }

    [Fact]
    public void Notional_over_cap_is_blocked_with_reason()
    {
        var gate = new PerOrderCapRiskGate(maxNotionalUsd: 500m);

        var (ok, reason) = gate.Check(Buy(750m));

        Assert.False(ok);
        Assert.Contains("cap", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Notional_exactly_at_cap_is_allowed()
    {
        var gate = new PerOrderCapRiskGate(maxNotionalUsd: 500m);

        var (ok, _) = gate.Check(Buy(500m));

        Assert.True(ok);
    }
}
