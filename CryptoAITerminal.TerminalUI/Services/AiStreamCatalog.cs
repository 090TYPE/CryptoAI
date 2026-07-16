using System.Collections.Generic;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>What one server-side AI stream is and how often it refreshes.</summary>
public sealed record AiStreamInfo(string Kind, string Name, string Description, string Cadence, string Group);

/// <summary>
/// Human-readable catalog of every AI function the server runs, so the terminal can name them
/// ("gas_vs_activity" → "Gas vs Activity · every 12h") and list the ones that haven't published
/// yet. Kinds must match the server's AiDigestJob.Kind values.
/// </summary>
public static class AiStreamCatalog
{
    public const string GroupMarket = "MARKET";
    public const string GroupRisk = "RISK";
    public const string GroupOpportunity = "OPPORTUNITY";
    public const string GroupPersonal = "PERSONAL";

    private static readonly AiStreamInfo[] All =
    {
        // ── Market ────────────────────────────────────────────────────────────
        new("daily",            "Daily Briefing",       "Market overview across everything tracked",      "every 24h", GroupMarket),
        new("weekly",           "Weekly Recap",         "The week's through-line, leaders and laggards",  "weekly",    GroupMarket),
        new("movers",           "Movers Explained",     "Why the biggest 24h moves happened",             "every 6h",  GroupMarket),
        new("narratives",       "Narrative Tracker",    "Which narratives are getting the flow",          "every 12h", GroupMarket),
        new("news_impact",      "News Impact",          "Which headlines actually matter here",           "every 3h",  GroupMarket),
        new("chain_rotation",   "Chain Rotation",       "Where capital is rotating between chains",       "every 12h", GroupMarket),
        new("onchain_pulse",    "On-chain Pulse",       "BTC/ETH network usage trend",                    "every 12h", GroupMarket),
        new("sentiment_read",   "Sentiment Read",       "Does positioning match the mood?",               "every 12h", GroupMarket),
        new("liquidations",     "Liquidations",         "Which side got flushed",                         "every 6h",  GroupMarket),
        new("stablecoin_flows", "Stablecoin Flows",     "Dry powder moving to risk or away",              "every 12h", GroupMarket),
        new("gas",              "Gas Advisor",          "Cheap or expensive to transact right now",       "every 6h",  GroupMarket),
        new("gas_vs_activity",  "Gas vs Activity",      "Busy with real usage, or just expensive?",       "every 12h", GroupMarket),

        // ── Risk ──────────────────────────────────────────────────────────────
        new("risk_dashboard",       "Risk Dashboard",      "Overall risk-on / risk-off call",             "every 12h", GroupRisk),
        new("liquidity_watch",      "Liquidity Watch",     "Where you can't get out at size",             "every 6h",  GroupRisk),
        new("volume_anomaly",       "Volume Anomaly",      "Real interest or wash trading?",              "every 6h",  GroupRisk),
        new("holder_concentration", "Holder Concentration","How few wallets can dump it",                 "every 12h", GroupRisk),
        new("security_roundup",     "Security Roundup",    "Honeypots and tax traps found",               "every 12h", GroupRisk),
        new("deployer_blacklist",   "Deployer Blacklist",  "Serial rug deployers and their pattern",      "every 12h", GroupRisk),
        new("worst_scored",         "Avoid List",          "Riskiest tracked tokens by AI score",         "every 12h", GroupRisk),
        new("rug_postmortem",       "Rug Post-mortem",     "What killed it, and the tell beforehand",     "every 12h", GroupRisk),
        new("dead_tokens",          "Dead Tokens",         "Tracked tokens with no volume left",          "every 24h", GroupRisk),

        // ── Opportunity ───────────────────────────────────────────────────────
        new("token_of_the_day",  "Token of the Day",  "One token, full picture",                      "every 24h", GroupOpportunity),
        new("top_scored",        "Top Scored",        "Best-scoring tokens and why",                  "every 12h", GroupOpportunity),
        new("new_listings",      "New Listing Radar", "Fresh tokens: opportunity vs trap",            "every 6h",  GroupOpportunity),
        new("momentum",          "Momentum Scan",     "1h and 24h pulling the same way",              "every 4h",  GroupOpportunity),
        new("reversals",         "Reversal Watch",    "Down on the day, turning up on the hour",      "every 4h",  GroupOpportunity),
        new("volatility",        "Volatility Report", "Realised 24h ranges — size accordingly",       "every 6h",  GroupOpportunity),
        new("liquidity_leaders", "Liquidity Leaders", "Where size can actually trade",                "every 12h", GroupOpportunity),
        new("microcaps",         "Microcap Radar",    "Small caps with real turnover",                "every 12h", GroupOpportunity),
        new("whales",            "Whale Moves",       "What the big transfers likely mean",           "every 6h",  GroupOpportunity),
        new("news_price_gap",    "News/Price Gap",    "Headlines say one thing, price another",       "every 6h",  GroupOpportunity),
    };

    private static readonly Dictionary<string, AiStreamInfo> ByKind = BuildIndex();

    private static Dictionary<string, AiStreamInfo> BuildIndex()
    {
        var d = new Dictionary<string, AiStreamInfo>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var s in All) d[s.Kind] = s;
        // Personal (inbox) kinds — delivered to this user only.
        d["alert"]            = new("alert", "Price Alert", "Your alert condition hit", "instant", GroupPersonal);
        d["anomaly"]          = new("anomaly", "AI Anomaly", "Unusual move on a token you follow", "instant", GroupPersonal);
        d["bot"]              = new("bot", "Bot Action", "Your server-side bot acted", "instant", GroupPersonal);
        d["portfolio_review"] = new("portfolio_review", "Portfolio Review", "AI review of your watchlist", "weekly", GroupPersonal);
        d["system"]           = new("system", "System", "Server notice", "", GroupPersonal);
        return d;
    }

    /// <summary>Every broadcast stream the server publishes, in display order.</summary>
    public static IReadOnlyList<AiStreamInfo> BroadcastStreams => All;

    /// <summary>Look up a kind; falls back to a readable form of the raw kind.</summary>
    public static AiStreamInfo Describe(string kind)
        => ByKind.TryGetValue(kind, out var info)
            ? info
            : new AiStreamInfo(kind, kind.Replace('_', ' '), string.Empty, string.Empty, GroupMarket);
}
