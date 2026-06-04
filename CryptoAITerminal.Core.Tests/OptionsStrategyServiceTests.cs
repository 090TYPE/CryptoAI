using System;
using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class OptionsStrategyServiceTests
{
    private static OptionsStrategyInput Input(
        decimal iv = 55m, decimal skew = 0m, decimal pcr = 0.9m, string dir = "neutral", string asset = "BTC")
        => new(asset, iv, skew, pcr, 50000m, dir);

    [Fact]
    public void HighIv_Bullish_SellsPremiumViaBullPutSpread()
    {
        var r = OptionsStrategyService.Recommend(Input(iv: 85m, dir: "bullish"));
        Assert.Equal("High", r.IvRegime);
        Assert.Equal("Bull put spread", r.Strategy);
        Assert.True(r.IsFallback);
    }

    [Fact]
    public void LowIv_Neutral_BuysPremiumViaLongStraddle()
    {
        var r = OptionsStrategyService.Recommend(Input(iv: 38m, dir: "neutral"));
        Assert.Equal("Low", r.IvRegime);
        Assert.Equal("Long straddle", r.Strategy);
    }

    [Fact]
    public void ModerateIv_Bearish_UsesBearPutSpread()
    {
        var r = OptionsStrategyService.Recommend(Input(iv: 55m, dir: "bearish"));
        Assert.Equal("Moderate", r.IvRegime);
        Assert.Equal("Bear put spread", r.Strategy);
    }

    [Fact]
    public void HighPutSkew_IsFlaggedInConsiderations()
    {
        var r = OptionsStrategyService.Recommend(Input(iv: 60m, skew: 7m, dir: "neutral"));
        Assert.Contains(r.Considerations, c => c.Contains("skew", StringComparison.OrdinalIgnoreCase)
                                            && c.Contains("put", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidDirection_DefaultsToNeutral()
    {
        var r = OptionsStrategyService.Recommend(Input(iv: 55m, dir: "moon"));
        Assert.Equal("neutral", r.Direction);
        Assert.Equal("Iron condor", r.Strategy);
    }

    [Fact]
    public async Task RecommendAsync_NoKey_ReturnsDeterministicOffline()
    {
        var svc = new OptionsStrategyService();
        svc.ConfigureAi("", null);
        Assert.False(svc.UsesLiveModel);

        var input = Input(iv: 85m, dir: "bullish");
        var viaAsync = await svc.RecommendAsync(input);
        var viaPure = OptionsStrategyService.Recommend(input);

        Assert.True(viaAsync.IsFallback);
        Assert.Equal(viaPure.Strategy, viaAsync.Strategy);
        Assert.Equal(viaPure.IvRegime, viaAsync.IvRegime);
    }
}
