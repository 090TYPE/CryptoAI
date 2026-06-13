using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DexTokenFilterTests
{
    private static DexTokenInfo Tok(string chain, decimal liq, decimal vol, decimal chg, string addr) => new()
    {
        ChainId = chain,
        LiquidityUsd = liq,
        Volume24h = vol,
        PriceChange24h = chg,
        TokenAddress = addr
    };

    private static IReadOnlyList<DexTokenInfo> Sample() => new[]
    {
        Tok("bsc",      80_000m, 500_000m,  10m, "a"),
        Tok("ethereum", 20_000m, 900_000m, -5m,  "b"),
        Tok("solana",  150_000m, 100_000m,  40m, "c"),
        Tok("base",     40_000m, 250_000m,   2m, "d"),
    };

    [Fact]
    public void NoFilters_SortsByVolumeDescending()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 0m, sortMode: "Volume");
        Assert.Equal(new[] { "b", "a", "d", "c" }, r.Select(t => t.TokenAddress).ToArray());
    }

    [Fact]
    public void ChainFilter_KeepsOnlyThatChain()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: "solana", minLiquidity: 0m, minVolume: 0m, sortMode: "Volume");
        Assert.Single(r);
        Assert.Equal("c", r[0].TokenAddress);
    }

    [Fact]
    public void MinLiquidity_DropsBelowThreshold()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 50_000m, minVolume: 0m, sortMode: "Volume");
        Assert.Equal(new[] { "a", "c" }, r.Select(t => t.TokenAddress).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void MinVolume_DropsBelowThreshold()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 300_000m, sortMode: "Volume");
        Assert.Equal(new[] { "a", "b" }, r.Select(t => t.TokenAddress).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void SortByLiquidity_Descending()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 0m, sortMode: "Liquidity");
        Assert.Equal(new[] { "c", "a", "d", "b" }, r.Select(t => t.TokenAddress).ToArray());
    }

    [Fact]
    public void SortByChange_Descending()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 0m, sortMode: "Change");
        Assert.Equal(new[] { "c", "a", "d", "b" }, r.Select(t => t.TokenAddress).ToArray());
    }

    [Fact]
    public void UnknownSort_FallsBackToVolume()
    {
        var r = DexTokenFilter.Apply(Sample(), chainId: null, minLiquidity: 0m, minVolume: 0m, sortMode: "Nonsense");
        Assert.Equal(new[] { "b", "a", "d", "c" }, r.Select(t => t.TokenAddress).ToArray());
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var r = DexTokenFilter.Apply(System.Array.Empty<DexTokenInfo>(), null, 0m, 0m, "Volume");
        Assert.Empty(r);
    }
}
