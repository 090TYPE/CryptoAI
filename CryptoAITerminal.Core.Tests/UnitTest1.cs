using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;
using System.Threading.Tasks;

namespace CryptoAITerminal.Core.Tests;

public class RiskManagerTests
{
    // ── Существующие тесты ────────────────────────────────────────────────────

    [Fact]
    public void Rejects_Order_When_Position_Value_Exceeds_Maximum()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "BTCUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 2m, MarketType = TradingMarketType.Spot
        };
        Assert.False(manager.CanPlaceOrder(order, currentPrice: 600m, availableBalanceUsd: 10_000m));
    }

    [Fact]
    public void Allows_Futures_Order_When_Leverage_Covers_Margin()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "ETHUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 1m, MarketType = TradingMarketType.FuturesUsdM, Leverage = 10
        };
        Assert.True(manager.CanPlaceOrder(order, currentPrice: 900m, availableBalanceUsd: 100m));
    }

    [Fact]
    public void Rejects_New_Orders_After_Daily_Loss_Cap_Is_Reached()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 100m);
        manager.RecordLoss(100m);
        var order = new Order
        {
            Symbol = "SOLUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 1m, MarketType = TradingMarketType.Spot
        };
        Assert.False(manager.CanPlaceOrder(order, currentPrice: 100m, availableBalanceUsd: 500m));
    }

    [Fact]
    public void Allows_ReduceOnly_Order_To_Decrease_Exposure()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "BNBUSDT", Side = OrderSide.Sell, Type = OrderType.Market,
            Quantity = 2m, MarketType = TradingMarketType.FuturesUsdM, Leverage = 5, ReduceOnly = true
        };
        Assert.True(manager.CanPlaceOrder(order, currentPrice: 100m, availableBalanceUsd: 0m, currentOpenExposureUsd: 900m));
    }

    // ── Новые граничные условия ───────────────────────────────────────────────

    [Fact]
    public void Rejects_Order_With_Zero_Quantity()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "BTCUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 0m, MarketType = TradingMarketType.Spot
        };
        Assert.False(manager.CanPlaceOrder(order, currentPrice: 100m, availableBalanceUsd: 1_000m));
    }

    [Fact]
    public void Rejects_Order_With_Zero_Price()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "BTCUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 1m, MarketType = TradingMarketType.Spot
        };
        Assert.False(manager.CanPlaceOrder(order, currentPrice: 0m, availableBalanceUsd: 1_000m));
    }

    [Fact]
    public void Rejects_Order_When_Insufficient_Balance()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "ETHUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 5m, MarketType = TradingMarketType.Spot
        };
        // orderValue = 5 * 100 = 500, but balance = 400
        Assert.False(manager.CanPlaceOrder(order, currentPrice: 100m, availableBalanceUsd: 400m));
    }

    [Fact]
    public void RecordLoss_Negative_Amount_Is_Ignored()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 50m);
        manager.RecordLoss(-999m); // negative loss should be ignored
        var order = new Order
        {
            Symbol = "BTCUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 1m, MarketType = TradingMarketType.Spot
        };
        // daily loss should still be 0 — order allowed
        Assert.True(manager.CanPlaceOrder(order, currentPrice: 10m, availableBalanceUsd: 1_000m));
    }

    [Fact]
    public void Daily_Loss_Accumulates_Across_Multiple_Losses()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 100m);
        manager.RecordLoss(60m);
        manager.RecordLoss(41m); // total 101 > 100
        var order = new Order
        {
            Symbol = "BTCUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 1m, MarketType = TradingMarketType.Spot
        };
        Assert.False(manager.CanPlaceOrder(order, currentPrice: 10m, availableBalanceUsd: 1_000m));
    }

    [Fact]
    public void Rejects_Futures_When_Insufficient_Margin()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "ETHUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 1m, MarketType = TradingMarketType.FuturesUsdM, Leverage = 2
        };
        // orderValue = 1 * 900 = 900, margin = 900/2 = 450, balance = 400 < 450
        Assert.False(manager.CanPlaceOrder(order, currentPrice: 900m, availableBalanceUsd: 400m));
    }

    [Fact]
    public void Allows_Order_Within_Position_And_Balance_Limits()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "SOLUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 5m, MarketType = TradingMarketType.Spot
        };
        // orderValue = 5 * 100 = 500 <= 1000, balance 600 >= 500
        Assert.True(manager.CanPlaceOrder(order, currentPrice: 100m, availableBalanceUsd: 600m));
    }

    [Fact]
    public void Rejects_When_Open_Exposure_Plus_New_Order_Exceeds_Max()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 1_000m, maxDailyLossUsd: 500m);
        var order = new Order
        {
            Symbol = "SOLUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 5m, MarketType = TradingMarketType.Spot
        };
        // orderValue = 5*100=500, existingExposure=600 → projected=1100 > 1000
        Assert.False(manager.CanPlaceOrder(order, currentPrice: 100m, availableBalanceUsd: 1_000m, currentOpenExposureUsd: 600m));
    }

    [Fact]
    public void Thread_Safe_RecordLoss_Under_Concurrent_Calls()
    {
        var manager = new RiskManager.RiskManager(maxPositionSizeUsd: 10_000m, maxDailyLossUsd: 10_000m);

        // 100 threads each record 1.0 loss → total should be exactly 100
        Parallel.For(0, 100, _ => manager.RecordLoss(1m));

        // After 100 × $1 loss the $100 limit should be exceeded
        var limitedManager = new RiskManager.RiskManager(maxPositionSizeUsd: 10_000m, maxDailyLossUsd: 99m);
        Parallel.For(0, 100, _ => limitedManager.RecordLoss(1m));

        var order = new Order
        {
            Symbol = "BTCUSDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 1m, MarketType = TradingMarketType.Spot
        };
        Assert.False(limitedManager.CanPlaceOrder(order, currentPrice: 1m, availableBalanceUsd: 10_000m));
    }
}
