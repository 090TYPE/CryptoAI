using System.Linq;
using CryptoAITerminal.Core.Trading;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class DexKeeperEngineTests
{
    private static DexKeeperOrder Order(KeeperOrderKind kind, KeeperSide side, decimal trigger, string token = "0xTOK") => new()
    {
        TokenAddress = token,
        Kind = kind,
        Side = side,
        TriggerPrice = trigger,
        AmountUsd = 100m,
        AmountTokens = 10m,
    };

    [Fact]
    public void LimitBuy_Fires_When_Price_Falls_To_Trigger()
    {
        var e = new DexKeeperEngine();
        var o = e.Add(Order(KeeperOrderKind.Limit, KeeperSide.Buy, 90m));

        Assert.Empty(e.Tick("0xTOK", 95m));
        var fired = e.Tick("0xTOK", 89m);
        Assert.Single(fired);
        Assert.Equal(o.Id, fired[0].Id);
        Assert.Equal(KeeperOrderStatus.Triggered, o.Status);
    }

    [Fact]
    public void LimitSell_TakeProfit_Fires_When_Price_Rises()
    {
        var e = new DexKeeperEngine();
        e.Add(Order(KeeperOrderKind.Limit, KeeperSide.Sell, 120m));
        Assert.Empty(e.Tick("0xTOK", 110m));
        Assert.Single(e.Tick("0xTOK", 121m));
    }

    [Fact]
    public void StopLoss_Sell_Fires_When_Price_Drops()
    {
        var e = new DexKeeperEngine();
        e.Add(Order(KeeperOrderKind.Stop, KeeperSide.Sell, 80m));
        Assert.Empty(e.Tick("0xTOK", 85m));
        Assert.Single(e.Tick("0xTOK", 79m));
    }

    [Fact]
    public void StopBuy_Fires_When_Price_Breaks_Up()
    {
        var e = new DexKeeperEngine();
        e.Add(Order(KeeperOrderKind.Stop, KeeperSide.Buy, 110m));
        Assert.Empty(e.Tick("0xTOK", 105m));
        Assert.Single(e.Tick("0xTOK", 111m));
    }

    [Fact]
    public void Trailing_Sell_Ratchets_Up_Then_Fires_On_Pullback()
    {
        var e = new DexKeeperEngine();
        var o = e.Add(new DexKeeperOrder { TokenAddress = "0xTOK", Kind = KeeperOrderKind.Trailing, Side = KeeperSide.Sell, TriggerPrice = 100m, TrailingDistancePct = 10m, AmountTokens = 5m });

        Assert.Empty(e.Tick("0xTOK", 120m)); // peak ratchets to 120, stop = 108
        Assert.Empty(e.Tick("0xTOK", 150m)); // peak 150, stop 135
        var fired = e.Tick("0xTOK", 134m);   // pulls back below 135
        Assert.Single(fired);
        Assert.Equal(o.Id, fired[0].Id);
    }

    [Fact]
    public void Tick_Only_Affects_Matching_Token()
    {
        var e = new DexKeeperEngine();
        e.Add(Order(KeeperOrderKind.Limit, KeeperSide.Buy, 90m, "0xAAA"));
        Assert.Empty(e.Tick("0xBBB", 50m)); // different token, no fire
        Assert.Single(e.Tick("0xAAA", 89m));
    }

    [Fact]
    public void CreateDca_Ladders_Limit_Buys_Down()
    {
        var e = new DexKeeperEngine();
        var plan = e.CreateDca("0xTOK", "eth", "TOK", totalUsd: 300m, legs: 3, startPrice: 100m, stepPercent: 5m);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, o => Assert.Equal(KeeperOrderKind.Limit, o.Kind));
        Assert.All(plan, o => Assert.Equal(KeeperSide.Buy, o.Side));
        Assert.All(plan, o => Assert.Equal(100m, o.AmountUsd));
        Assert.True(plan[1].TriggerPrice < plan[0].TriggerPrice);
        Assert.All(plan, o => Assert.Equal(plan[0].PlanId, o.PlanId));
    }

    [Fact]
    public void Cancel_Removes_Working_Order()
    {
        var e = new DexKeeperEngine();
        var o = e.Add(Order(KeeperOrderKind.Limit, KeeperSide.Buy, 90m));
        Assert.Single(e.WorkingOrders);
        e.Cancel(o.Id);
        Assert.Empty(e.WorkingOrders);
    }

    [Fact]
    public void Fired_Order_Does_Not_Fire_Again()
    {
        var e = new DexKeeperEngine();
        e.Add(Order(KeeperOrderKind.Limit, KeeperSide.Buy, 90m));
        Assert.Single(e.Tick("0xTOK", 89m));
        Assert.Empty(e.Tick("0xTOK", 88m)); // already triggered
    }
}
