namespace CryptoAITerminal.Server.Common;

/// <summary>
/// Names of the runtime settings, in one place so a key is never spelled two ways.
///
/// Dotted and lowercase on purpose: these are read from a database row and shown in an admin
/// screen, not exported to a shell, so they do not need to look like environment variables — and
/// looking different from the SCREAMING_CASE ones makes it obvious at a glance which values are
/// live and which need a restart.
///
/// Resolution order everywhere is: this setting, then the matching environment variable, then the
/// in-code default. The environment stays in the chain so an existing .env keeps working after a
/// deploy, and so a box with no settings table behaves exactly as it did before.
/// </summary>
public static class SettingKeys
{
    /// <summary>
    /// Model for the background jobs that do not have their own. Haiku-class: these are short,
    /// high-volume, strictly-formatted calls where the cheap model is not a compromise.
    /// </summary>
    public const string DefaultBackgroundModel = "claude-haiku-4-5-20251001";

    // ── Models for the server's own jobs ──────────────────────────────────────
    public const string DigestModel  = "ai.digest.model";   // env: AI_DIGEST_MODEL
    public const string ScoreModel   = "ai.score.model";    // env: AI_SCORE_MODEL
    public const string AnomalyModel = "ai.anomaly.model";  // env: AI_ANOMALY_MODEL
    public const string AskModel     = "ai.ask.model";      // env: AI_ASK_MODEL
    public const string ReviewModel  = "ai.review.model";   // env: AI_DIGEST_MODEL (shared today)

    // ── Cost bounds on those jobs ─────────────────────────────────────────────
    public const string ScoreBatch          = "ai.score.batch";
    public const string ScoreMaxAgeHours    = "ai.score.max_age_hours";
    public const string AnomalyBatch        = "ai.anomaly.batch";
    public const string AnomalyCooldownHours = "ai.anomaly.cooldown_hours";
    public const string AnomalyMinMovePct   = "ai.anomaly.min_move_pct";
    public const string ReviewBatch         = "ai.review.batch";
    public const string ReviewDays          = "ai.review.days";

    // ── Per-feature overrides for calls that originate in the terminal ────────
    // Built from the X-AI-Feature header. Absent = the client's own request stands, which is what
    // keeps terminals shipped before this existed working unchanged.

    public static string FeatureModel(string feature) => $"ai.feature.{feature}.model";
    public static string FeatureMaxTokens(string feature) => $"ai.feature.{feature}.max_tokens";

    // ── Per-tier daily allowance ──────────────────────────────────────────────
    // The licence carries an Edition; until this existed nothing read it, so every paid tier got
    // identical service and the price list was a claim rather than a fact.

    /// <summary>Daily token allowance for a tier, e.g. <c>plan.lite.daily_tokens</c>.</summary>
    public static string PlanDailyTokens(string edition) =>
        $"plan.{edition.Trim().ToLowerInvariant()}.daily_tokens";

    /// <summary>
    /// Allowance for a licence whose tier we do not recognise. Deliberately generous rather than
    /// restrictive: an unknown edition means WE introduced a plan and forgot to configure it, and
    /// the customer should not be the one who finds out.
    /// </summary>
    public const string PlanDefaultDailyTokens = "plan.default.daily_tokens";

    /// <summary>
    /// Shipped tiers and what they get per day, matching the price list: $5 / $10 / $15 a week.
    /// Overridable per tier from the admin panel without a deploy.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, long> DefaultPlanDailyTokens =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["lite"] = 35_000,
            ["pro"] = 70_000,
            ["max"] = 105_000,
        };

    // ── Оповещения о состоянии сборщиков ──────────────────────────────────────
    // Env fallbacks: ALERT_BOT_TOKEN, ALERT_CHAT_ID, COLLECTOR_STALE_MINUTES.

    public const string AlertsEnabled       = "alerts.enabled";
    public const string AlertsTelegramToken = "alerts.telegram.token";
    public const string AlertsTelegramChat  = "alerts.telegram.chat_id";
    public const string AlertsStaleMinutes  = "alerts.stale_minutes";

    /// <summary>
    /// Master switch for every paid model call the server makes. False stops the background jobs
    /// and makes the AI proxy decline, which the terminals already handle by falling back to their
    /// own deterministic output. The point is to have one lever when spend spikes or the vendor
    /// misbehaves, instead of editing .env under time pressure.
    /// </summary>
    public const string AiEnabled = "ai.enabled";
}
