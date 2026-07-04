using System.Linq;
using CryptoAITerminal.Core.Trading;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Deterministic coverage for the paper perpetuals engine that powers the DEX PERP
/// desk: market fills, leverage → margin & liquidation math, working-order triggers
/// (limit/stop), liquidation, DCA/grid plan generation, close/reverse and funding.
/// Pure domain logic — no UI, no network, no timers.
/// </summary>
public class DexPerpPaperEngineTests
{
    private static DexPerpPaperEngine NewEngine(decimal balance = 10_000m) =>
        new(startingBalance: balance, maintenanceMarginRate: 0.005m);

    [Fact]
    public void Market_Long_OpensPosition_AtMark_WithLeveragedMargin()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Long, sizeTokens: 2m, leverage: 10, PerpMarginMode.Isolated, markPrice: 100m);

        var p = e.Position!;
        Assert.Equal(PerpSide.Long, p.Side);
        Assert.Equal(2m, p.Size);
        Assert.Equal(100m, p.EntryPrice);
        Assert.Equal(10, p.Leverage);
        // notional 200 @ 10x → 20 margin
        Assert.Equal(20m, p.Margin);
        // long liquidation sits below entry
        Assert.True(p.LiquidationPrice < 100m);
    }

    [Fact]
    public void UnrealizedPnl_And_Roe_TrackMark()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Long, 2m, 10, PerpMarginMode.Isolated, 100m);
        e.Tick(110m); // +10 per token * 2 = +20

        Assert.Equal(20m, e.Position!.UnrealizedPnl);
        // ROE = 20 / 20 margin = 100%
        Assert.Equal(1.00m, e.Position!.Roe);
    }

    [Fact]
    public void Short_UnrealizedPnl_IsPositive_WhenPriceFalls()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Short, 3m, 5, PerpMarginMode.Isolated, 50m);
        e.Tick(40m); // short profits 10 * 3 = 30

        Assert.Equal(30m, e.Position!.UnrealizedPnl);
        Assert.True(e.Position!.LiquidationPrice > 50m); // short liq above entry
    }

    [Fact]
    public void LimitOrder_Fires_WhenPriceCrossesDown_ForBuy()
    {
        var e = NewEngine();
        var order = e.PlaceLimit(PerpSide.Long, triggerPrice: 90m, sizeTokens: 1m, leverage: 5, PerpMarginMode.Isolated);
        Assert.Equal(PerpOrderStatus.Working, order.Status);

        e.Tick(95m); // above trigger — still working
        Assert.Null(e.Position);
        Assert.Equal(PerpOrderStatus.Working, order.Status);

        e.Tick(89m); // crossed down to/through 90 — fills
        Assert.Equal(PerpOrderStatus.Filled, order.Status);
        Assert.NotNull(e.Position);
        Assert.Equal(PerpSide.Long, e.Position!.Side);
    }

    [Fact]
    public void StopOrder_Fires_WhenPriceCrossesUp_ForBuyStop()
    {
        var e = NewEngine();
        var stop = e.PlaceStop(PerpSide.Long, triggerPrice: 110m, sizeTokens: 1m, leverage: 5, PerpMarginMode.Isolated);

        e.Tick(105m);
        Assert.Equal(PerpOrderStatus.Working, stop.Status);

        e.Tick(111m); // crossed up through stop
        Assert.Equal(PerpOrderStatus.Filled, stop.Status);
        Assert.NotNull(e.Position);
    }

    [Fact]
    public void Liquidation_ClosesPosition_AndBooksLoss()
    {
        var e = NewEngine(balance: 1_000m);
        e.PlaceMarket(PerpSide.Long, 10m, 20, PerpMarginMode.Isolated, 100m); // margin 50
        var liq = e.Position!.LiquidationPrice;

        e.Tick(liq - 1m); // dive through liquidation

        Assert.Null(e.Position);
        Assert.True(e.RealizedPnl < 0m);
        Assert.Contains(e.Fills, f => f.Kind == "LIQ");
    }

    [Fact]
    public void Close_RealisesPnl_AndFlattens()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Long, 2m, 10, PerpMarginMode.Isolated, 100m);
        e.Tick(120m);
        e.Close();

        Assert.Null(e.Position);
        Assert.Equal(40m, e.RealizedPnl); // (120-100)*2
        Assert.Contains(e.Fills, f => f.Kind == "CLOSE");
    }

    [Fact]
    public void Reverse_FlipsSide_KeepingSize()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Long, 2m, 10, PerpMarginMode.Isolated, 100m);
        e.Reverse();

        Assert.NotNull(e.Position);
        Assert.Equal(PerpSide.Short, e.Position!.Side);
        Assert.Equal(2m, e.Position!.Size);
    }

    [Fact]
    public void DcaPlan_Schedules_N_LadderedLimitOrders()
    {
        var e = NewEngine();
        var plan = e.CreateDcaPlan(PerpSide.Long, totalSizeTokens: 3m, legs: 3, startPrice: 100m, stepPercent: 5m, leverage: 5, PerpMarginMode.Isolated);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, o => Assert.Equal(PerpOrderKind.Limit, o.Kind));
        Assert.All(plan, o => Assert.Equal(1m, o.SizeTokens)); // 3 / 3
        // laddered down for a long: 100, 95, 90.25 ...
        Assert.True(plan[1].TriggerPrice < plan[0].TriggerPrice);
        Assert.All(plan, o => Assert.Equal(plan[0].PlanId, o.PlanId));
    }

    [Fact]
    public void GridPlan_Lays_Buys_Below_And_Sells_Above_Mid()
    {
        var e = NewEngine();
        var plan = e.CreateGridPlan(lowerPrice: 80m, upperPrice: 120m, levels: 4, sizePerLevelTokens: 1m, markPrice: 100m, leverage: 5, PerpMarginMode.Isolated);

        Assert.Equal(4, plan.Count);
        Assert.Contains(plan, o => o.Side == PerpSide.Long && o.TriggerPrice < 100m);
        Assert.Contains(plan, o => o.Side == PerpSide.Short && o.TriggerPrice > 100m);
        Assert.All(plan, o => Assert.InRange(o.TriggerPrice, 80m, 120m));
    }

    [Fact]
    public void CancelOrder_RemovesWorkingOrder()
    {
        var e = NewEngine();
        var o = e.PlaceLimit(PerpSide.Long, 90m, 1m, 5, PerpMarginMode.Isolated);
        Assert.Single(e.WorkingOrders);

        e.CancelOrder(o.Id);
        Assert.Empty(e.WorkingOrders);
    }

    [Fact]
    public void Funding_Accrual_Debits_Long_When_Rate_Positive()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Long, 2m, 10, PerpMarginMode.Isolated, 100m);
        var before = e.RealizedPnl;

        e.AccrueFunding(fundingRate: 0.0001m); // long pays when positive

        Assert.True(e.RealizedPnl < before);
        Assert.True(e.FundingPaid > 0m);
    }

    [Fact]
    public void ClosePartial_Realises_Half_And_Keeps_Rest()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Long, 4m, 10, PerpMarginMode.Isolated, 100m);
        e.Tick(120m); // +20/token

        e.ClosePartial(0.5m);

        Assert.NotNull(e.Position);
        Assert.Equal(2m, e.Position!.Size);
        Assert.Equal(40m, e.RealizedPnl); // (120-100)*2 closed
    }

    [Fact]
    public void TrailingStop_Ratchets_Up_Then_Closes_Long_On_Pullback()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Long, 1m, 5, PerpMarginMode.Isolated, 100m);
        e.ArmTrailingStop(distance: 5m); // stop starts at 95

        e.Tick(120m); // stop ratchets up to 115
        Assert.NotNull(e.Position);

        e.Tick(114m); // pulls back through the trailed stop
        Assert.Null(e.Position);
        Assert.Contains(e.Fills, f => f.Kind == "TRAIL");
        Assert.True(e.RealizedPnl > 0m); // locked in a profit
    }

    [Fact]
    public void EquityCurve_Grows_And_Is_Bounded()
    {
        var e = NewEngine();
        e.PlaceMarket(PerpSide.Long, 1m, 5, PerpMarginMode.Isolated, 100m);
        for (var i = 0; i < 300; i++) e.Tick(100m + i % 10);

        Assert.NotEmpty(e.EquityCurve);
        Assert.True(e.EquityCurve.Count <= 240);
    }
}
