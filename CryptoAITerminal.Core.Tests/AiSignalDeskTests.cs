using System.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Deterministic coverage for the AI Signal desk: the pure JSON parser
/// (<see cref="AiSignalDeskProvider.Parse"/>) and the host service's degrade-to-null
/// behaviour when no key is configured. No network — the wire call is out of scope here.
/// </summary>
public class AiSignalDeskTests
{
    private const string GoodJson = """
    {
      "regime": {"label":"trending up","tone":"bull","summary":"Momentum strong.","meta":"ADX 38"},
      "news": {"bias":"bullish","summary":"ETF inflows.","bullets":[{"text":"Inflows +$840M","tone":"bull"},{"text":"Mt. Gox risk","tone":"warn"}]},
      "signals": [
        {"sym":"btc","dir":"LONG","conf":85,"tf":"15m","exch":"Binance","reason":"Breakout."},
        {"sym":"SOL","dir":"SELL","conf":0.72,"tf":"5m","exch":"Bybit","reason":"Rejection."}
      ],
      "opportunities": [{"sym":"INJ/USDT","exch":"Binance","score":91,"bias":"LONG","reason":"Accumulation."}],
      "insights": [{"label":"whale flow","signal":"ACCUM","tone":"bull","summary":"Wallets adding."}],
      "coach": {"summary":"Exits too early.","strengths":["Trend read"],"leaks":["Early TP"],"suggestions":["Hold longer"]}
    }
    """;

    [Fact]
    public void Parse_MapsAllSections_AndNormalises()
    {
        var r = AiSignalDeskProvider.Parse(GoodJson, "Claude test");
        Assert.NotNull(r);

        // regime + news
        Assert.Equal("trending up", r!.Regime.Label);
        Assert.Equal("bull", r.Regime.Tone);
        Assert.Equal("BULLISH", r.News.Bias);
        Assert.Equal(2, r.News.Bullets.Count);
        Assert.Equal("warn", r.News.Bullets[1].Tone);

        // signals: LONG→BUY, 85→0.85, string conf 0.72 kept
        Assert.Equal(2, r.Signals.Count);
        Assert.Equal("BTC", r.Signals[0].Sym);
        Assert.Equal("BUY", r.Signals[0].Dir);
        Assert.Equal(0.85, r.Signals[0].Conf, 3);
        Assert.Equal("SELL", r.Signals[1].Dir);
        Assert.Equal(0.72, r.Signals[1].Conf, 3);

        Assert.Single(r.Opportunities);
        Assert.Equal("LONG", r.Opportunities[0].Bias);
        Assert.Equal(91, r.Opportunities[0].Score);

        Assert.Single(r.Insights);
        Assert.Equal("WHALE FLOW", r.Insights[0].Label);

        Assert.Equal("Exits too early.", r.Coach.Summary);
        Assert.Single(r.Coach.Suggestions);
        Assert.Equal("Claude test", r.Source);
    }

    [Fact]
    public void Parse_AcceptsMarkdownFencedJson()
    {
        var fenced = "```json\n" + GoodJson + "\n```";
        var r = AiSignalDeskProvider.Parse(fenced, "src");
        Assert.NotNull(r);
        Assert.Equal(2, r!.Signals.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"signals\":[]}")]                 // no usable signals → fall back
    [InlineData("{\"regime\":{\"label\":\"x\"}}")]   // no signals key → fall back
    public void Parse_ReturnsNull_OnUnusableResponse(string text)
        => Assert.Null(AiSignalDeskProvider.Parse(text, "src"));

    [Fact]
    public async Task Service_NoKey_DegradesToNull()
    {
        var svc = new AiSignalDeskService { ApiKey = "" };
        Assert.False(svc.IsConfigured);
        Assert.Equal("offline · heuristic", svc.SourceLabel);

        var ctx = new AiSignalDeskContext(
            [new AiSignalMarketRow("BTC", "BTC/USDT", "Binance", 67000m, 2.3m, 0.01m, 5m, "BTC", "#0c2218")],
            []);

        Assert.Null(await svc.GenerateAsync(ctx));
        Assert.Null(await svc.AskAsync("system", "question"));
    }
}
