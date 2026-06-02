using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class Tier2AiServiceTests
{
    // ── #5 Portfolio rebalance ──
    [Fact]
    public async Task Rebalance_Offline_WeightsSumTo100_AndRespectsProfile()
    {
        var svc = new PortfolioRebalanceAiService { ApiKey = "" };
        var holdings = new List<HoldingRow>
        {
            new("BTC", 5000m, 50m),
            new("ETH", 3000m, 30m),
            new("SOL", 1500m, 15m),
            new("USDT", 500m, 5m),
        };

        var aggressive = await svc.SuggestAsync(holdings, "Aggressive");
        var conservative = await svc.SuggestAsync(holdings, "Conservative");

        Assert.True(aggressive.IsFallback);
        Assert.Equal(100m, aggressive.Targets.Sum(t => t.TargetPct), 0);
        Assert.Equal(100m, conservative.Targets.Sum(t => t.TargetPct), 0);

        // Conservative should hold more stablecoin than aggressive.
        decimal Cash(RebalancePlan p) => p.Targets.Where(t => t.Symbol == "USDT").Sum(t => t.TargetPct);
        Assert.True(Cash(conservative) > Cash(aggressive));
    }

    [Fact]
    public async Task Rebalance_EmptyHoldings_ReturnsEmpty()
    {
        var svc = new PortfolioRebalanceAiService { ApiKey = "" };
        var plan = await svc.SuggestAsync(new List<HoldingRow>(), "Balanced");
        Assert.Empty(plan.Targets);
    }

    // ── #6 Whale flow heuristic ──
    [Fact]
    public void WhaleFlow_NetInflow_IsDistribution()
    {
        var r = InsightHeuristics.WhaleFlow(inflowUsd: 8_000_000m, outflowUsd: 1_000_000m, transferCount: 12);
        Assert.Equal("DISTRIBUTION", r.Signal);
        Assert.True(r.IsFallback);
        Assert.NotEmpty(r.Bullets);
    }

    [Fact]
    public void WhaleFlow_NetOutflow_IsAccumulation()
    {
        var r = InsightHeuristics.WhaleFlow(inflowUsd: 500_000m, outflowUsd: 6_000_000m, transferCount: 9);
        Assert.Equal("ACCUMULATION", r.Signal);
    }

    [Fact]
    public void WhaleFlow_NoTransfers_IsNeutral()
    {
        var r = InsightHeuristics.WhaleFlow(0, 0, 0);
        Assert.Equal("NEUTRAL", r.Signal);
    }

    // ── #6/#7 Insight service routing ──
    [Fact]
    public async Task Insight_NoKey_UsesOfflineDelegate()
    {
        var svc = new MarketInsightAiService { ApiKey = "" };
        var sentinel = new InsightResult("offline!", "NEUTRAL", [], "Heuristic (offline)", true);
        var result = await svc.InterpretAsync("You are a test.", new[] { "line" }, new[] { "NEUTRAL" }, () => sentinel);
        Assert.Same(sentinel, result);
    }

    // ── #8 Bot parameters ──
    [Fact]
    public async Task GridParams_Offline_BoundsBracketPrice()
    {
        var svc = new BotParameterAiService { ApiKey = "" };
        var s = await svc.SuggestAsync(BotParameterAiService.Grid, price: 100m, high24h: 108m, low24h: 95m, changePct24h: 4m);

        Assert.True(s.IsFallback);
        Assert.True(s.Params["LowerPrice"] < 100m);
        Assert.True(s.Params["UpperPrice"] > 100m);
        Assert.InRange(s.Params["GridCount"], 8m, 40m);
    }

    [Fact]
    public async Task DcaParams_Offline_ProducesSaneValues()
    {
        var svc = new BotParameterAiService { ApiKey = "" };
        var s = await svc.SuggestAsync(BotParameterAiService.Dca, price: 100m, high24h: 110m, low24h: 90m, changePct24h: -5m);

        Assert.True(s.IsFallback);
        Assert.InRange(s.Params["IntervalHours"], 4m, 24m);
        Assert.InRange(s.Params["DipBuyPercent"], 1.5m, 10m);
        Assert.True(s.Params["AmountUsd"] > 0m);
    }
}
