using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4 §4.5 — symbol allowlist guardrail: when configured, a live bot may only trade
/// listed symbols; an empty allowlist means no restriction. Case-insensitive.</summary>
public class SymbolAllowlistTests
{
    [Fact]
    public void Empty_allowlist_allows_anything()
    {
        Assert.True(SymbolAllowlist.IsAllowed("BTCUSDT", Array.Empty<string>()));
    }

    [Fact]
    public void Listed_symbol_is_allowed()
    {
        Assert.True(SymbolAllowlist.IsAllowed("BTCUSDT", new[] { "BTCUSDT", "ETHUSDT" }));
    }

    [Fact]
    public void Unlisted_symbol_is_blocked()
    {
        Assert.False(SymbolAllowlist.IsAllowed("DOGEUSDT", new[] { "BTCUSDT", "ETHUSDT" }));
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        Assert.True(SymbolAllowlist.IsAllowed("btcusdt", new[] { "BTCUSDT" }));
    }

    [Fact]
    public void Parses_a_comma_or_space_separated_config_value()
    {
        var set = SymbolAllowlist.Parse(" BTCUSDT, ethusdt ;SOLUSDT ");
        Assert.Contains("BTCUSDT", set);
        Assert.Contains("ETHUSDT", set); // upper-cased
        Assert.Contains("SOLUSDT", set);
        Assert.Equal(3, set.Count);
    }
}
