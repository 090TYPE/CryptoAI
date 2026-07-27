using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>Daily market overview across everything we track — published to all users.</summary>
public sealed class DailyDigestJob : AiDigestJob
{
    public DailyDigestJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<DailyDigestJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "daily";
    public override TimeSpan Period => TimeSpan.FromHours(24);

    protected override string SystemPrompt =>
        "You are a crypto desk analyst writing the daily briefing for traders using this terminal. " +
        "Summarise the market: what moved, notable risk, overall tone. Practical, no hype, no advice to buy/sell.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var market = await Digests.GetMarketFactsAsync(25, ct);
        if (market.Count == 0) return null;
        return Json(new
        {
            date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            tokens = market,
            movers = await Digests.GetTopMoversAsync(5, ct),
            context = await Digests.GetContextAsync(ct),
            headlines = await Digests.GetNewsHeadlinesAsync(15, ct)
        });
    }
}

/// <summary>Why the biggest gainers/losers moved — data + news, explained.</summary>
public sealed class MoversDigestJob : AiDigestJob
{
    public MoversDigestJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<MoversDigestJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "movers";
    public override TimeSpan Period => TimeSpan.FromHours(6);

    protected override string SystemPrompt =>
        "Explain the biggest 24h movers to a trader: for each, the likely driver based on the facts and headlines. " +
        "If the data doesn't explain a move, say so plainly instead of guessing.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var movers = await Digests.GetTopMoversAsync(6, ct);
        if (movers.Count == 0) return null;
        return Json(new { movers, headlines = await Digests.GetNewsHeadlinesAsync(15, ct) });
    }
}

/// <summary>Clusters what we track into narratives (AI agents, memes, RWA, L2s…).</summary>
public sealed class NarrativeDigestJob : AiDigestJob
{
    public NarrativeDigestJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<NarrativeDigestJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "narratives";
    public override TimeSpan Period => TimeSpan.FromHours(12);

    protected override string SystemPrompt =>
        "Group the tracked tokens into market narratives (e.g. AI agents, memecoins, RWA, L2, DeFi) and report " +
        "which narratives are getting flow right now, with the tokens as evidence. Note when a grouping is uncertain.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var market = await Digests.GetMarketFactsAsync(40, ct);
        return market.Count == 0 ? null : Json(new { tokens = market });
    }
}

/// <summary>Reads the news feed and maps it onto the tokens we track.</summary>
public sealed class NewsImpactDigestJob : AiDigestJob
{
    public NewsImpactDigestJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<NewsImpactDigestJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "news_impact";
    public override TimeSpan Period => TimeSpan.FromHours(3);

    protected override string SystemPrompt =>
        "From the headlines, pick only what actually matters for the tracked tokens/sectors and say what it implies. " +
        "Skip filler news. If nothing is material, say that in one line.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var headlines = await Digests.GetNewsHeadlinesAsync(25, ct);
        if (headlines.Count == 0) return null;
        return Json(new { headlines, tracked = await Digests.GetMarketFactsAsync(25, ct) });
    }
}
