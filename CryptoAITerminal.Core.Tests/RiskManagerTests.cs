using System.Linq;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Core.Tests;

// Covers the observability/configuration surface added on top of the existing
// boolean CanPlaceOrder gate (which is covered in UnitTest1.cs): structured block
// reasons via Evaluate, the live budget snapshot, and runtime limit updates.
public class RiskManagerObservabilityTests
{
    private static Order SpotOrder(decimal quantity) => new()
    {
        MarketType = TradingMarketType.Spot,
        Quantity = quantity
    };

    private static RiskManager.RiskManager NewManager() =>
        new(maxPositionSizeUsd: 1000m, maxDailyLossUsd: 500m);

    [Fact]
    public void Evaluate_WithinAllLimits_AllowedWithEmptyReason()
    {
        var result = NewManager().Evaluate(SpotOrder(1m), currentPrice: 500m, availableBalanceUsd: 1000m);

        Assert.True(result.Allowed);
        Assert.Equal(string.Empty, result.Reason);
    }

    [Fact]
    public void Evaluate_DailyLossLimitReached_ReasonMentionsDailyLoss()
    {
        var risk = NewManager();
        risk.RecordLoss(500m);

        var result = risk.Evaluate(SpotOrder(1m), 500m, 1000m);

        Assert.False(result.Allowed);
        Assert.Contains("Daily loss", result.Reason);
    }

    [Fact]
    public void Evaluate_OrderValueExceedsMaxPosition_ReasonMentionsMax()
    {
        var result = NewManager().Evaluate(SpotOrder(3m), 500m, 10_000m); // 1500 > 1000

        Assert.False(result.Allowed);
        Assert.Contains("exceeds max", result.Reason);
    }

    [Fact]
    public void Evaluate_ProjectedExposureExceedsMax_ReasonMentionsExposure()
    {
        var result = NewManager().Evaluate(SpotOrder(1m), 500m, 10_000m, currentOpenExposureUsd: 700m);

        Assert.False(result.Allowed);
        Assert.Contains("Projected exposure", result.Reason);
    }

    [Fact]
    public void Evaluate_InsufficientBalance_ReasonMentionsBalance()
    {
        var result = NewManager().Evaluate(SpotOrder(1m), 500m, availableBalanceUsd: 100m);

        Assert.False(result.Allowed);
        Assert.Contains("Insufficient balance", result.Reason);
    }

    [Fact]
    public void Evaluate_NonPositiveInput_ReasonMentionsPositive()
    {
        var result = NewManager().Evaluate(SpotOrder(0m), 500m, 1000m);

        Assert.False(result.Allowed);
        Assert.Contains("positive", result.Reason);
    }

    [Fact]
    public void GetBudgetSnapshot_FreshManager_ReportsLimitsAndFullHeadroom()
    {
        var snapshot = NewManager().GetBudgetSnapshot();

        Assert.Equal(1000m, snapshot.MaxPositionSizeUsd);
        Assert.Equal(500m, snapshot.MaxDailyLossUsd);
        Assert.Equal(0m, snapshot.DailyLossUsd);
        Assert.Equal(500m, snapshot.RemainingDailyLossBudgetUsd);
        Assert.Equal(string.Empty, snapshot.LastBlockReason);
    }

    [Fact]
    public void GetBudgetSnapshot_AfterLosses_ReflectsAccumulatedLossAndHeadroom()
    {
        var risk = NewManager();
        risk.RecordLoss(100m);
        risk.RecordLoss(50m);

        var snapshot = risk.GetBudgetSnapshot();

        Assert.Equal(150m, snapshot.DailyLossUsd);
        Assert.Equal(350m, snapshot.RemainingDailyLossBudgetUsd);
    }

    [Fact]
    public void GetBudgetSnapshot_RemainingNeverNegative()
    {
        var risk = NewManager();
        risk.RecordLoss(900m); // beyond the 500 cap

        Assert.Equal(0m, risk.GetBudgetSnapshot().RemainingDailyLossBudgetUsd);
    }

    [Fact]
    public void UpdateLimits_RaisingPositionCap_UnblocksFormerlyTooLargeOrder()
    {
        var risk = NewManager();
        Assert.False(risk.Evaluate(SpotOrder(3m), 500m, 10_000m).Allowed); // 1500 > 1000

        risk.UpdateLimits(maxPositionSizeUsd: 2000m, maxDailyLossUsd: 500m);

        Assert.True(risk.Evaluate(SpotOrder(3m), 500m, 10_000m).Allowed); // 1500 < 2000
        Assert.Equal(2000m, risk.MaxPositionSizeUsd);
    }

    [Fact]
    public void UpdateLimits_IgnoresNonPositiveValues()
    {
        var risk = NewManager();
        risk.UpdateLimits(maxPositionSizeUsd: 0m, maxDailyLossUsd: -5m);

        Assert.Equal(1000m, risk.MaxPositionSizeUsd);
        Assert.Equal(500m, risk.MaxDailyLossUsd);
    }

    [Fact]
    public void LastBlockReason_ClearedAfterAllowedEvaluation()
    {
        var risk = NewManager();
        risk.Evaluate(SpotOrder(0m), 500m, 1000m); // blocks, sets reason
        Assert.NotEqual(string.Empty, risk.LastBlockReason);

        risk.Evaluate(SpotOrder(1m), 500m, 1000m); // allowed

        Assert.Equal(string.Empty, risk.LastBlockReason);
    }

    [Theory]
    [InlineData(0, RiskManager.RiskBudgetLevel.Ok)]        // 0%
    [InlineData(200, RiskManager.RiskBudgetLevel.Ok)]      // 40%
    [InlineData(250, RiskManager.RiskBudgetLevel.Caution)] // 50% -> caution
    [InlineData(350, RiskManager.RiskBudgetLevel.Caution)] // 70%
    [InlineData(400, RiskManager.RiskBudgetLevel.Critical)]// 80% -> critical
    [InlineData(500, RiskManager.RiskBudgetLevel.Critical)]// 100%
    [InlineData(900, RiskManager.RiskBudgetLevel.Critical)]// breached
    public void Snapshot_Level_ReflectsDailyLossConsumption(decimal lossToRecord, RiskManager.RiskBudgetLevel expected)
    {
        var risk = NewManager(); // 500 daily-loss cap
        if (lossToRecord > 0m)
        {
            risk.RecordLoss(lossToRecord);
        }

        Assert.Equal(expected, risk.GetBudgetSnapshot().Level);
    }

    [Fact]
    public void Snapshot_DailyLossUsedFraction_ZeroWhenNoCap()
    {
        var risk = new RiskManager.RiskManager(maxPositionSizeUsd: 1000m, maxDailyLossUsd: 0m);
        risk.RecordLoss(100m);

        var snapshot = risk.GetBudgetSnapshot();
        Assert.Equal(0m, snapshot.DailyLossUsedFraction);
        Assert.Equal(RiskManager.RiskBudgetLevel.Ok, snapshot.Level);
    }

    [Fact]
    public void GetRecentBlocks_FreshManager_IsEmpty()
    {
        Assert.Empty(NewManager().GetRecentBlocks());
    }

    [Fact]
    public void GetRecentBlocks_RecordsBlockedOrdersNewestFirst()
    {
        var risk = NewManager();
        risk.Evaluate(SpotOrder(0m), 500m, 1000m);   // "positive"
        risk.Evaluate(SpotOrder(3m), 500m, 10_000m); // "exceeds max"

        var blocks = risk.GetRecentBlocks();

        Assert.Equal(2, blocks.Count);
        Assert.Contains("exceeds max", blocks[0].Reason); // newest first
        Assert.Contains("positive", blocks[1].Reason);
    }

    [Fact]
    public void GetRecentBlocks_AllowedOrdersAreNotLogged()
    {
        var risk = NewManager();
        risk.Evaluate(SpotOrder(1m), 500m, 1000m); // allowed

        Assert.Empty(risk.GetRecentBlocks());
    }

    [Fact]
    public void GetRecentBlocks_IsBoundedToTwentyEntries()
    {
        var risk = NewManager();
        for (var i = 0; i < 30; i++)
        {
            risk.Evaluate(SpotOrder(0m), 500m, 1000m); // always blocks
        }

        Assert.Equal(20, risk.GetRecentBlocks().Count);
    }

    [Fact]
    public void GetDailyLossHistory_AccumulatesCumulativeReadings()
    {
        var risk = NewManager();
        risk.RecordLoss(100m);
        risk.RecordLoss(50m);

        Assert.Equal(new[] { 100m, 150m }, risk.GetDailyLossHistory().ToArray());
    }

    [Fact]
    public void ResetDailyLoss_ClearsLossBlocksAndHistory()
    {
        var risk = NewManager();
        risk.RecordLoss(300m);
        risk.Evaluate(SpotOrder(0m), 500m, 1000m); // logs a block + sets reason

        risk.ResetDailyLoss();

        var snapshot = risk.GetBudgetSnapshot();
        Assert.Equal(0m, snapshot.DailyLossUsd);
        Assert.Equal(string.Empty, snapshot.LastBlockReason);
        Assert.Empty(risk.GetRecentBlocks());
        Assert.Empty(risk.GetDailyLossHistory());
    }
}
