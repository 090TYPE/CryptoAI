using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class SniperRankingModelTests
{
    private static readonly DateTime FixedNow = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private static DexTokenInfo MakeToken(
        decimal change5m = 0m,
        decimal liquidity = 50_000m,
        decimal volume24h = 100_000m,
        int dexQuality = 80,
        TimeSpan? age = null) => new()
    {
        ChainId = "bsc",
        DexId = "pancakeswap",
        TokenAddress = "0xabc",
        Symbol = "TEST",
        PriceChange5m = change5m,
        LiquidityUsd = liquidity,
        Volume24h = volume24h,
        DexQualityScore = dexQuality,
        ObservedFirstSeenUtc = age.HasValue ? FixedNow - age.Value : DateTime.MinValue
    };

    // ── Score bounds ────────────────────────────────────────────────────────

    [Fact]
    public void Score_Is_Always_Between_Zero_And_Hundred()
    {
        var token = MakeToken(change5m: 1000m, liquidity: 1_000_000_000m, volume24h: 999_999_999m, dexQuality: 100, age: TimeSpan.FromMinutes(1));
        var rank = SniperRankingModel.Compute(token, FixedNow);
        Assert.InRange(rank.Total, 0, 100);
    }

    [Fact]
    public void Negative_Momentum_Reduces_Momentum_Component()
    {
        var dump  = MakeToken(change5m: -40m, liquidity: 100_000m, volume24h: 100_000m, age: TimeSpan.FromMinutes(30));
        var pump  = MakeToken(change5m:  40m, liquidity: 100_000m, volume24h: 100_000m, age: TimeSpan.FromMinutes(30));
        Assert.True(SniperRankingModel.Compute(pump, FixedNow).Total
                  > SniperRankingModel.Compute(dump, FixedNow).Total);
    }

    [Fact]
    public void Higher_Liquidity_Yields_Higher_Liquidity_Component()
    {
        var low  = MakeToken(liquidity: 1_000m);
        var high = MakeToken(liquidity: 1_000_000m);
        Assert.True(SniperRankingModel.Compute(high, FixedNow).LiquidityComponent
                  > SniperRankingModel.Compute(low,  FixedNow).LiquidityComponent);
    }

    // ── Pool age ────────────────────────────────────────────────────────────

    [Fact]
    public void Younger_Pool_Yields_Higher_Age_Component()
    {
        var fresh = MakeToken(age: TimeSpan.FromMinutes(30));
        var old   = MakeToken(age: TimeSpan.FromDays(7));
        Assert.True(SniperRankingModel.Compute(fresh, FixedNow).AgeComponent
                  > SniperRankingModel.Compute(old,   FixedNow).AgeComponent);
    }

    [Fact]
    public void Unknown_Age_Yields_Mid_Score()
    {
        // ObservedFirstSeenUtc == DateTime.MinValue → middle (7.5 баллов)
        var token = MakeToken(); // no age supplied
        var rank = SniperRankingModel.Compute(token, FixedNow);
        Assert.Equal(7.5d, rank.AgeComponent);
    }

    // ── Volume / liquidity ratio ────────────────────────────────────────────

    [Fact]
    public void Optimal_Volume_To_Liquidity_Ratio_Earns_Full_Score()
    {
        // ratio = 2.0 — внутри оптимального коридора 0.5..3.0
        var token = MakeToken(liquidity: 100_000m, volume24h: 200_000m);
        var rank = SniperRankingModel.Compute(token, FixedNow);
        Assert.Equal(20d, rank.VolumeComponent);
    }

    [Fact]
    public void Extreme_Volume_Ratio_Earns_Zero()
    {
        // ratio = 100.0 — вне диапазона
        var token = MakeToken(liquidity: 1_000m, volume24h: 100_000m);
        var rank = SniperRankingModel.Compute(token, FixedNow);
        Assert.Equal(0d, rank.VolumeComponent);
    }

    [Fact]
    public void Zero_Liquidity_Gives_Zero_Volume_Component()
    {
        var token = MakeToken(liquidity: 0m, volume24h: 50_000m);
        var rank = SniperRankingModel.Compute(token, FixedNow);
        Assert.Equal(0d, rank.VolumeComponent);
    }

    // ── Band ────────────────────────────────────────────────────────────────

    [Fact]
    public void Top_Sniper_Sets_Receive_S_Band()
    {
        var token = MakeToken(change5m: 50m, liquidity: 1_000_000m, volume24h: 1_500_000m, dexQuality: 100, age: TimeSpan.FromMinutes(15));
        var rank = SniperRankingModel.Compute(token, FixedNow);
        Assert.Equal("S", rank.Band);
    }

    [Fact]
    public void Weak_Sets_Receive_D_Band()
    {
        var token = MakeToken(change5m: -50m, liquidity: 100m, volume24h: 1m, dexQuality: 0, age: TimeSpan.FromDays(60));
        var rank = SniperRankingModel.Compute(token, FixedNow);
        Assert.Equal("D", rank.Band);
    }
}
