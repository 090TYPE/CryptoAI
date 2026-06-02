using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class Tier3AiServiceTests
{
    // ── #9 DEX trending ──
    [Fact]
    public async Task DexTrending_Offline_PenalisesThinLiquidity_AndRanks()
    {
        var svc = new DexTrendingAiService { ApiKey = "" };
        var tokens = new List<DexTokenInfo>
        {
            new() { Symbol = "RUG", TokenAddress = "0xrug", PriceChange1h = 60m, PriceChange5m = 20m, LiquidityUsd = 2_000m, Volume24h = 50_000m },
            new() { Symbol = "MOMO", TokenAddress = "0xmomo", PriceChange1h = 25m, PriceChange5m = 4m, LiquidityUsd = 150_000m, Volume24h = 400_000m },
        };

        var r = await svc.RankAsync(tokens, topN: 5);

        Assert.True(r.IsFallback);
        Assert.Equal("AVOID", r.Tokens.Single(t => t.Symbol == "RUG").Signal);   // thin liq → avoid
        Assert.Equal("MOMENTUM", r.Tokens.Single(t => t.Symbol == "MOMO").Signal);
        Assert.True(r.Tokens.First().Symbol == "MOMO");                          // outranks the rug
    }

    // ── #10 Dynamic TP/SL ──
    [Fact]
    public async Task DynamicTpSl_Offline_WidensWithVolatility_AndKeepsRR()
    {
        var svc = new DynamicTpSlAiService { ApiKey = "" };
        var calm  = await svc.SuggestAsync(new TpSlContext("BTCUSDT", "Long", 100, 100, 4m, 1m, 1m, "up"));
        var choppy = await svc.SuggestAsync(new TpSlContext("BTCUSDT", "Long", 100, 100, 40m, 8m, 5m, "up"));

        Assert.True(choppy.SlPercent > calm.SlPercent);   // wider stop in volatile market
        Assert.True(calm.TpPercent > calm.SlPercent);     // reward:risk > 1
        Assert.True(choppy.Trailing);                     // trail when choppy
    }

    // ── #11 StatArb pair ──
    [Fact]
    public async Task StatArb_Offline_StretchedZ_GivesDirectionalEntry()
    {
        var svc = new StatArbPairAiService { ApiKey = "" };
        var enter = await svc.EvaluateAsync(new StatArbPairStats("BTCUSDT", "ETHUSDT", 0.9m, -2.6m, 20, 2.0m, 0.5m));
        var wait  = await svc.EvaluateAsync(new StatArbPairStats("BTCUSDT", "ETHUSDT", 0.9m, 0.3m, 20, 2.0m, 0.5m));
        var avoid = await svc.EvaluateAsync(new StatArbPairStats("BTCUSDT", "DOGEUSDT", 0.2m, 3m, 500, 2.0m, 0.5m));

        Assert.Equal("LONG_A_SHORT_B", enter.Signal);  // z very negative → A cheap
        Assert.True(enter.Tradeable);
        Assert.Equal("WAIT", wait.Signal);
        Assert.Equal("AVOID", avoid.Signal);
        Assert.False(avoid.Tradeable);
    }

    // ── #12 Execution scheduler ──
    [Fact]
    public async Task Execution_Offline_LargeOrder_GetsMoreSlices()
    {
        var svc = new ExecutionScheduleAiService { ApiKey = "" };
        var big   = await svc.PlanAsync(new OrderExecutionContext("BTCUSDT", "Buy", 100_000m, 50_000_000m, 20_000m, "medium"));
        var small = await svc.PlanAsync(new OrderExecutionContext("BTCUSDT", "Buy", 5_000m, 50_000_000m, 20_000m, "medium"));

        Assert.True(big.IsFallback);
        Assert.True(big.Slices >= small.Slices);
        Assert.True(big.SliceUsd > 0m);
        Assert.Equal(100_000m, big.Slices * big.SliceUsd, 0); // slices * sliceUsd ≈ total
    }

    [Fact]
    public async Task Execution_Urgency_ShortensInterval()
    {
        var svc = new ExecutionScheduleAiService { ApiKey = "" };
        var urgent = await svc.PlanAsync(new OrderExecutionContext("ETHUSDT", "Sell", 20_000m, 10_000_000m, 10_000m, "high"));
        var relaxed = await svc.PlanAsync(new OrderExecutionContext("ETHUSDT", "Sell", 20_000m, 10_000_000m, 10_000m, "low"));
        Assert.True(urgent.IntervalSeconds < relaxed.IntervalSeconds);
    }
}
