using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

// Risk & safety lens ─────────────────────────────────────────────────────────

/// <summary>Thin or shrinking liquidity — where you can't get out.</summary>
public sealed class LiquidityWatchJob : AiDigestJob
{
    public LiquidityWatchJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<LiquidityWatchJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "liquidity_watch";
    public override TimeSpan Period => TimeSpan.FromHours(6);
    protected override string SystemPrompt =>
        "Flag tokens whose liquidity is too thin for their volume — where a normal-size exit would move price hard. " +
        "Rank by danger and say roughly what size the book can absorb.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var m = await Digests.GetMarketFactsAsync(30, ct); return m.Count == 0 ? null : Json(new { tokens = m }); }
}

/// <summary>Volume/liquidity churn outliers — real interest or wash?</summary>
public sealed class VolumeAnomalyJob : AiDigestJob
{
    public VolumeAnomalyJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<VolumeAnomalyJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "volume_anomaly";
    public override TimeSpan Period => TimeSpan.FromHours(6);
    protected override string SystemPrompt =>
        "These tokens turn over their liquidity unusually fast. Judge which look like genuine interest and which " +
        "smell like wash trading or a pump. Say when it's ambiguous.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var v = await Digests.GetVolumeLiqRatioAsync(15, ct); return v.Count == 0 ? null : Json(new { churn = v }); }
}

/// <summary>Supply concentration — how few wallets can dump it.</summary>
public sealed class HolderConcentrationJob : AiDigestJob
{
    public HolderConcentrationJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<HolderConcentrationJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "holder_concentration";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Report supply concentration risk: which tokens are held by so few wallets that a single holder can crater them.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var h = await Digests.GetConcentrationAsync(15, ct); return h.Count == 0 ? null : Json(new { concentration = h }); }
}

/// <summary>Honeypots and tax traps found today.</summary>
public sealed class SecurityRoundupJob : AiDigestJob
{
    public SecurityRoundupJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<SecurityRoundupJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "security_roundup";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Round up the security problems found across tracked tokens (honeypots, punitive taxes, low scores). " +
        "Name the worst offenders plainly so users avoid them.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var s = await Digests.GetSecurityIssuesAsync(15, ct); return s.Count == 0 ? null : Json(new { issues = s }); }
}

/// <summary>Serial rug deployers.</summary>
public sealed class DeployerBlacklistJob : AiDigestJob
{
    public DeployerBlacklistJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<DeployerBlacklistJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "deployer_blacklist";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "These deployers have prior rugs. Summarise the pattern (how many tokens, how many rugs, wallet age) so users " +
        "can recognise the same hand next time.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var d = await Digests.GetDeployerRiskyAsync(15, ct); return d.Count == 0 ? null : Json(new { deployers = d }); }
}

/// <summary>Riskiest tokens by our cached AI score.</summary>
public sealed class WorstScoredJob : AiDigestJob
{
    public WorstScoredJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<WorstScoredJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "worst_scored";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt => "List the riskiest tracked tokens by score and why, as an avoid-list.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var s = await Digests.GetScoredAsync(false, 12, ct); return s.Count == 0 ? null : Json(new { worst = s }); }
}

/// <summary>Best-scoring tokens leaderboard.</summary>
public sealed class TopScoredJob : AiDigestJob
{
    public TopScoredJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<TopScoredJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "top_scored";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Leaderboard of the best-scoring tracked tokens with the reason each scores well. Note this is safety scoring, not a buy call.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var s = await Digests.GetScoredAsync(true, 12, ct); return s.Count == 0 ? null : Json(new { best = s }); }
}

/// <summary>Tokens that went quiet.</summary>
public sealed class DeadTokensJob : AiDigestJob
{
    public DeadTokensJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<DeadTokensJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "dead_tokens";
    public override TimeSpan Period => TimeSpan.FromHours(24);
    protected override string SystemPrompt =>
        "These tracked tokens have almost no volume left. Point out which are effectively dead so holders stop waiting.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    { var q = await Digests.GetQuietTokensAsync(15, ct); return q.Count == 0 ? null : Json(new { quiet = q }); }
}

/// <summary>Overall market risk read across every signal we hold.</summary>
public sealed class RiskDashboardJob : AiDigestJob
{
    public RiskDashboardJob(ProviderKeyStore k, AiProxy a, AiDigestRepository d, IConfiguration c, ILogger<RiskDashboardJob> l)
        : base(k, a, d, c["AI_DIGEST_MODEL"] ?? "claude-haiku-4-5-20251001", l) { }
    public override string Kind => "risk_dashboard";
    public override TimeSpan Period => TimeSpan.FromHours(12);
    protected override string SystemPrompt =>
        "Give the overall market risk read (risk-on / neutral / risk-off) from sentiment, chain flow, security findings " +
        "and movers. One clear call plus the evidence behind it.";
    protected override async Task<string?> BuildFactsAsync(CancellationToken ct)
    {
        var m = await Digests.GetMarketFactsAsync(20, ct);
        if (m.Count == 0) return null;
        return Json(new
        {
            context = await Digests.GetContextAsync(ct),
            chains = await Digests.GetChainAggregatesAsync(ct),
            movers = await Digests.GetTopMoversAsync(5, ct),
            security = await Digests.GetSecurityIssuesAsync(8, ct)
        });
    }
}
