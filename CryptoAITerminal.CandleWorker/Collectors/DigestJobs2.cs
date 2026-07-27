using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>Week in review across everything we track.</summary>
public sealed class WeeklyRecapJob : AiDigestJob
{
    public WeeklyRecapJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<WeeklyRecapJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "weekly";
    public override TimeSpan Period => TimeSpan.FromDays(7);

    protected override string SystemPrompt =>
        "Write the week-in-review for traders on this terminal: the through-line of the week, what led and lagged, " +
        "how the mood shifted, and what to watch next. Ground it in the facts; if the data only shows a snapshot, say so.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var market = await Digests.GetMarketFactsAsync(30, ct);
        if (market.Count == 0) return null;
        return Json(new
        {
            weekEnding = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            tokens = market,
            movers = await Digests.GetTopMoversAsync(8, ct),
            context = await Digests.GetContextAsync(ct),
            headlines = await Digests.GetNewsHeadlinesAsync(40, ct)
        });
    }
}

/// <summary>Fresh listings: early opportunity vs. risk, judged from security + holders.</summary>
public sealed class NewListingRadarJob : AiDigestJob
{
    public NewListingRadarJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<NewListingRadarJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "new_listings";
    public override TimeSpan Period => TimeSpan.FromHours(6);

    protected override string SystemPrompt =>
        "Review tokens that just appeared on the radar. For each, weigh early-opportunity signals against risk " +
        "(honeypot, taxes, holder concentration, thin liquidity). Be blunt about which look like traps. Not financial advice.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var fresh = await Digests.GetNewTokensAsync(24, 15, ct);
        return fresh.Count == 0 ? null : Json(new { newTokens = fresh });
    }
}

/// <summary>What the big transfers likely mean.</summary>
public sealed class WhaleInterpreterJob : AiDigestJob
{
    public WhaleInterpreterJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<WhaleInterpreterJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "whales";
    public override TimeSpan Period => TimeSpan.FromHours(6);

    protected override string SystemPrompt =>
        "Interpret large stablecoin/whale transfers: possible exchange inflow/outflow, accumulation or de-risking. " +
        "Addresses are unlabeled — state uncertainty rather than inventing attribution.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var whales = await Digests.GetWhaleTxsAsync(25, ct);
        return whales.Count == 0 ? null : Json(new { transfers = whales });
    }
}

/// <summary>When it's cheap to transact.</summary>
public sealed class GasAdvisorJob : AiDigestJob
{
    public GasAdvisorJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<GasAdvisorJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "gas";
    public override TimeSpan Period => TimeSpan.FromHours(6);

    protected override string SystemPrompt =>
        "Advise on transaction timing from 24h gas stats per chain: is now cheap or expensive versus the day's range, " +
        "and which chain is currently the cheapest to act on.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var gas = await Digests.GetGasStatsAsync(ct);
        return gas.Count == 0 ? null : Json(new { gas24h = gas });
    }
}

/// <summary>Post-mortem on tokens that just collapsed — warn the holders.</summary>
public sealed class RugPostMortemJob : AiDigestJob
{
    public RugPostMortemJob(ProviderKeyStore k, AiProxy ai, AiDigestRepository d, IConfiguration cfg, SettingsStore s, ILogger<RugPostMortemJob> log)
        : base(k, ai, d, s, cfg["AI_DIGEST_MODEL"], log) { }

    public override string Kind => "rug_postmortem";
    public override TimeSpan Period => TimeSpan.FromHours(12);

    protected override string SystemPrompt =>
        "These tokens collapsed. For each, explain what the evidence suggests happened (rug, sell-off, honeypot, " +
        "deployer history) and the tell that was visible beforehand, so readers spot the next one. " +
        "Say 'unclear' when the data doesn't support a conclusion.";

    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var dead = await Digests.GetCollapsedTokensAsync(-50m, 10, ct);
        return dead.Count == 0 ? null : Json(new { collapsed = dead });
    }
}
