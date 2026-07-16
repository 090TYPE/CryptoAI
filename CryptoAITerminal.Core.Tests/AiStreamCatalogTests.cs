using System.Linq;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>The catalog is what turns server kinds ("gas_vs_activity") into something a user can read.</summary>
public class AiStreamCatalogTests
{
    /// <summary>Every kind the server's AiDigestJob subclasses publish. Keep in sync with the worker.</summary>
    private static readonly string[] ServerBroadcastKinds =
    {
        "daily", "weekly", "movers", "narratives", "news_impact", "new_listings", "whales", "gas",
        "rug_postmortem", "liquidity_watch", "volume_anomaly", "holder_concentration", "security_roundup",
        "deployer_blacklist", "worst_scored", "top_scored", "dead_tokens", "risk_dashboard", "chain_rotation",
        "onchain_pulse", "sentiment_read", "liquidations", "stablecoin_flows", "volatility", "momentum",
        "reversals", "liquidity_leaders", "microcaps", "news_price_gap", "gas_vs_activity", "token_of_the_day",
    };

    [Fact]
    public void Covers_every_server_stream()
    {
        var catalogued = AiStreamCatalog.BroadcastStreams.Select(s => s.Kind).ToHashSet();
        var missing = ServerBroadcastKinds.Where(k => !catalogued.Contains(k)).ToArray();
        Assert.Empty(missing); // a new server stream must get a catalog entry, or the UI shows a raw kind
        Assert.Equal(ServerBroadcastKinds.Length, AiStreamCatalog.BroadcastStreams.Count);
    }

    [Fact]
    public void Every_stream_is_presentable()
    {
        foreach (var s in AiStreamCatalog.BroadcastStreams)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name), $"{s.Kind} has no name");
            Assert.False(string.IsNullOrWhiteSpace(s.Description), $"{s.Kind} has no description");
            Assert.False(string.IsNullOrWhiteSpace(s.Cadence), $"{s.Kind} has no cadence");
        }
        Assert.Equal(AiStreamCatalog.BroadcastStreams.Count,
                     AiStreamCatalog.BroadcastStreams.Select(s => s.Kind).Distinct().Count());
    }

    [Fact]
    public void Describes_known_and_personal_kinds()
    {
        Assert.Equal("Daily Briefing", AiStreamCatalog.Describe("daily").Name);
        Assert.Equal("Gas vs Activity", AiStreamCatalog.Describe("gas_vs_activity").Name);
        Assert.Equal(AiStreamCatalog.GroupPersonal, AiStreamCatalog.Describe("portfolio_review").Group);
        Assert.Equal(AiStreamCatalog.GroupPersonal, AiStreamCatalog.Describe("alert").Group);
    }

    [Fact]
    public void Falls_back_readably_for_unknown_kind()
        => Assert.Equal("some new stream", AiStreamCatalog.Describe("some_new_stream").Name);
}
