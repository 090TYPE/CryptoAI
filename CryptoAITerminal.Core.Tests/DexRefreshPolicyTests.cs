using CryptoAITerminal.TerminalUI.Services;
using static CryptoAITerminal.TerminalUI.Services.DexRefreshPolicy;

namespace CryptoAITerminal.Core.Tests;

public class DexRefreshPolicyTests
{
    [Fact]
    public void Search_TakesPriority_EvenWithChain()
    {
        Assert.Equal(AutoRefreshAction.Search, NextAutoRefresh("bsc", "doge"));
        Assert.Equal(AutoRefreshAction.Search, NextAutoRefresh(null, "doge"));
    }

    [Fact]
    public void NoSearch_NoChain_ReloadsLatest()
    {
        Assert.Equal(AutoRefreshAction.ReloadLatest, NextAutoRefresh(null, null));
        Assert.Equal(AutoRefreshAction.ReloadLatest, NextAutoRefresh("", "   "));
    }

    [Fact]
    public void NoSearch_SpecificChain_Skips()
    {
        Assert.Equal(AutoRefreshAction.Skip, NextAutoRefresh("bsc", null));
        Assert.Equal(AutoRefreshAction.Skip, NextAutoRefresh("tron", ""));
    }
}
