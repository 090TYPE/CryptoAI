using System.Collections.Generic;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AgentGuardrailsTests
{
    [Fact]
    public void Happy_path_increments_count_and_spend()
    {
        var g = new AgentGuardrails { MaxTrades = 5, SessionBudgetUsd = 1000m };
        Assert.True(g.TryAuthorize("BTCUSDT", 100m, out _));
        Assert.Equal(1, g.TradeCount);
        Assert.Equal(100m, g.SpentUsd);
    }

    [Fact]
    public void Allowlist_rejects_symbol_not_listed()
    {
        var g = new AgentGuardrails { AllowedSymbols = new List<string> { "BTCUSDT" } };
        Assert.False(g.TryAuthorize("ETHUSDT", 10m, out var reason));
        Assert.Contains("allowlist", reason);
        Assert.Equal(0, g.TradeCount);
    }

    [Fact]
    public void Empty_allowlist_allows_any_symbol()
    {
        var g = new AgentGuardrails();
        Assert.True(g.TryAuthorize("ANYUSDT", 1m, out _));
    }

    [Fact]
    public void Max_trades_stops_the_session()
    {
        var g = new AgentGuardrails { MaxTrades = 1, SessionBudgetUsd = 1000m };
        Assert.True(g.TryAuthorize("BTCUSDT", 10m, out _));
        Assert.False(g.TryAuthorize("BTCUSDT", 10m, out var reason));
        Assert.True(g.Stopped);
        Assert.Contains("max trades", reason);
    }

    [Fact]
    public void Budget_overrun_stops_the_session()
    {
        var g = new AgentGuardrails { MaxTrades = 100, SessionBudgetUsd = 150m };
        Assert.True(g.TryAuthorize("BTCUSDT", 100m, out _));
        Assert.False(g.TryAuthorize("BTCUSDT", 100m, out var reason)); // 200 > 150
        Assert.True(g.Stopped);
        Assert.Contains("budget", reason);
    }

    [Fact]
    public void Reset_clears_counters_and_stop()
    {
        var g = new AgentGuardrails { MaxTrades = 1 };
        g.TryAuthorize("BTCUSDT", 1m, out _);
        g.TryAuthorize("BTCUSDT", 1m, out _); // stops
        g.Reset();
        Assert.False(g.Stopped);
        Assert.Equal(0, g.TradeCount);
        Assert.Equal(0m, g.SpentUsd);
    }
}
