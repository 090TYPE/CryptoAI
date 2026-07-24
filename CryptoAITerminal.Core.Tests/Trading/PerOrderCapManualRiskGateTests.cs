using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Executor;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Trading;

public class PerOrderCapManualRiskGateTests
{
    private static PlaceMarketCommand Cmd(decimal qty) => new(
        "okx", "BTCUSDT", OrderSide.Buy, qty, false, 10, FuturesMarginMode.Cross, FuturesPositionSide.Long, "c1");

    [Fact]
    public void Blocks_when_notional_exceeds_cap()
    {
        var gate = new PerOrderCapManualRiskGate(maxNotionalUsd: 1000m);
        var (ok, reason) = gate.Check(Cmd(1m), price: 50000m);
        Assert.False(ok);
        Assert.Contains("cap", reason);
    }

    [Fact]
    public void Allows_within_cap()
    {
        var gate = new PerOrderCapManualRiskGate(maxNotionalUsd: 100000m);
        var (ok, _) = gate.Check(Cmd(0.001m), price: 50000m);
        Assert.True(ok);
    }
}
