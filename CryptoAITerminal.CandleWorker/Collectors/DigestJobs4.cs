using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

// Opportunity & market-structure lens ────────────────────────────────────────

/// <summary>Which chain is getting the flow.</summary>
public sealed class ChainRotationJob : AiDigestJob
{
    public ChainRotationJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<ChainRotationJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "chain_rotation";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "From per-chain volume, liquidity and average move, say where capital is rotating and which chain is losing interest.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var c = await Digests.GetChainAggregatesAsync(ct); return c.Count == 0 ? null : Json(new { chains = c }); }
}

/// <summary>BTC/ETH network pulse.</summary>
public sealed class OnchainPulseJob : AiDigestJob
{
    public OnchainPulseJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<OnchainPulseJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "onchain_pulse";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Read the BTC/ETH on-chain series (active addresses, tx count): is real network usage rising or fading, and what that implies.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var s = await Digests.GetOnchainSeriesAsync(30, ct); return s.Count == 0 ? null : Json(new { onchain = s }); }
}

/// <summary>What Fear &amp; Greed is actually saying.</summary>
public sealed class SentimentReadJob : AiDigestJob
{
    public SentimentReadJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<SentimentReadJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "sentiment_read";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Interpret the sentiment reading against how the tracked market is actually trading — does positioning match the mood?";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var ctx = await Digests.GetContextAsync(ct);
        if (ctx.Count == 0) return null;
        return Json(new { context = ctx, movers = await Digests.GetTopMoversAsync(5, ct) });
    }
}

/// <summary>Derivatives liquidations read.</summary>
public sealed class LiquidationReportJob : AiDigestJob
{
    public LiquidationReportJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<LiquidationReportJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "liquidations";
    public override TimeSpan Period => TimeSpan.FromHours(6);
    protected override string SystemPrompt =>
        "Read the liquidation flow: which side got flushed and what that usually means for the next move. Keep it factual.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var l = await Digests.GetLiquidationsAsync(30, ct); return l.Count == 0 ? null : Json(new { liquidations = l }); }
}

/// <summary>Stablecoin whale flow → risk-on/off.</summary>
public sealed class StablecoinFlowJob : AiDigestJob
{
    public StablecoinFlowJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<StablecoinFlowJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "stablecoin_flows";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "From large stablecoin transfers, read whether dry powder is moving toward risk or away from it. " +
        "Addresses are unlabeled — flag the uncertainty.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var w = await Digests.GetWhaleTxsAsync(30, ct); return w.Count == 0 ? null : Json(new { transfers = w }); }
}

/// <summary>Realised volatility leaders from our candles.</summary>
public sealed class VolatilityReportJob : AiDigestJob
{
    public VolatilityReportJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<VolatilityReportJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "volatility";
    public override TimeSpan Period => TimeSpan.FromHours(6);
    protected override string SystemPrompt =>
        "These are 24h realised ranges from 1m candles. Say which tokens are violently volatile (position-size warning) " +
        "and which are unusually calm.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var v = await Digests.GetVolatilityAsync(15, ct); return v.Count == 0 ? null : Json(new { ranges24h = v }); }
}

/// <summary>1h and 24h pulling the same way.</summary>
public sealed class MomentumScanJob : AiDigestJob
{
    public MomentumScanJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<MomentumScanJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "momentum";
    public override TimeSpan Period => TimeSpan.FromHours(4);
    protected override string SystemPrompt =>
        "Find tokens where 1h and 24h momentum agree (sustained trend, not a single candle), backed by volume. Not a buy call.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var m = await Digests.GetMarketFactsAsync(30, ct); return m.Count == 0 ? null : Json(new { tokens = m }); }
}

/// <summary>Down on the day, turning up on the hour.</summary>
public sealed class ReversalWatchJob : AiDigestJob
{
    public ReversalWatchJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<ReversalWatchJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "reversals";
    public override TimeSpan Period => TimeSpan.FromHours(4);
    protected override string SystemPrompt =>
        "Spot possible reversals: down over 24h but turning up on the 1h with volume behind it. Say which look like dead-cat bounces.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var m = await Digests.GetMarketFactsAsync(30, ct); return m.Count == 0 ? null : Json(new { tokens = m }); }
}

/// <summary>Deepest books — where size can actually trade.</summary>
public sealed class LiquidityLeadersJob : AiDigestJob
{
    public LiquidityLeadersJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<LiquidityLeadersJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "liquidity_leaders";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Rank the tokens with the deepest liquidity — where a larger position can enter and exit without wrecking the price.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var m = await Digests.GetMarketFactsAsync(30, ct); return m.Count == 0 ? null : Json(new { tokens = m }); }
}

/// <summary>Small caps with real turnover.</summary>
public sealed class MicrocapRadarJob : AiDigestJob
{
    public MicrocapRadarJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<MicrocapRadarJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "microcaps";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Small caps that trade real volume relative to their size. Flag the asymmetry and the obvious risk in each.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var m = await Digests.GetMicrocapsAsync(15, ct); return m.Count == 0 ? null : Json(new { microcaps = m }); }
}

/// <summary>News says one thing, price says another.</summary>
public sealed class NewsPriceGapJob : AiDigestJob
{
    public NewsPriceGapJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<NewsPriceGapJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "news_price_gap";
    public override TimeSpan Period => TimeSpan.FromHours(6);
    protected override string SystemPrompt =>
        "Find divergences: headlines read bullish while price fades (or the reverse). Divergence is a question, not a signal — frame it that way.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var m = await Digests.GetMarketFactsAsync(20, ct);
        if (m.Count == 0) return null;
        return Json(new { tokens = m, headlines = await Digests.GetNewsHeadlinesAsync(20, ct) });
    }
}

/// <summary>Congestion read: gas vs actual network usage.</summary>
public sealed class GasVsActivityJob : AiDigestJob
{
    public GasVsActivityJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<GasVsActivityJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "gas_vs_activity";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Compare gas levels against on-chain activity: is the chain busy with real usage or just expensive?";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var g = await Digests.GetGasStatsAsync(ct);
        if (g.Count == 0) return null;
        return Json(new { gas24h = g, onchain = await Digests.GetOnchainSeriesAsync(20, ct) });
    }
}

/// <summary>One token, full picture, every day.</summary>
public sealed class TokenOfTheDayJob : AiDigestJob
{
    public TokenOfTheDayJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<TokenOfTheDayJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-3-5-haiku-20241022", l) { }
    public override string Kind => "token_of_the_day";
    public override TimeSpan Period => TimeSpan.FromHours(24);
    protected override string SystemPrompt =>
        "Pick ONE token from the facts that best deserves attention today (interesting, not necessarily good) and give the " +
        "full picture: what it is, the flow, the risks. Explain the pick. Not financial advice.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var m = await Digests.GetMarketFactsAsync(25, ct);
        if (m.Count == 0) return null;
        return Json(new
        {
            tokens = m,
            scored = await Digests.GetScoredAsync(true, 8, ct),
            security = await Digests.GetSecurityIssuesAsync(8, ct)
        });
    }
}
