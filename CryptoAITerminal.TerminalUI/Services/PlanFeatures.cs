using System;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Tiers, cheapest first. Order matters — comparisons rely on it.</summary>
public enum PlanTier
{
    /// <summary>No licence yet, or one the build does not recognise. Treated as the trial.</summary>
    None = 0,
    Lite = 1,
    Pro = 2,
    Max = 3
}

/// <summary>
/// What each tier may use, on the client side.
///
/// The server is the authority — it holds the key and refuses what a tier is not entitled to — so
/// this exists purely so the terminal does not offer something it will then be denied. A panel
/// that runs, spins, and returns an error is a worse answer than a panel that says "доступно в Pro"
/// before it is touched.
///
/// Deliberately fail-open: an unknown tier gets everything. An unrecognised edition means WE added
/// a plan and did not update this list, and the person who should discover that is us, not a
/// customer locked out of what they paid for. The server still enforces the real limit.
/// </summary>
public static class PlanFeatures
{
    public static PlanTier Parse(string? edition) => edition?.Trim().ToLowerInvariant() switch
    {
        "lite" => PlanTier.Lite,
        "pro" => PlanTier.Pro,
        "max" => PlanTier.Max,
        _ => PlanTier.None
    };

    /// <summary>Human label for a paywall notice.</summary>
    public static string Label(PlanTier tier) => tier switch
    {
        PlanTier.Lite => "Lite",
        PlanTier.Pro => "Pro",
        PlanTier.Max => "Max",
        _ => "—"
    };

    // ── Gates ────────────────────────────────────────────────────────────────
    // One method per capability rather than a generic Has(feature) lookup: each of these is a
    // decision someone made about the price list, and a named method is where that decision is
    // findable when the price list changes.

    /// <summary>Heavy analysis panels: Signal Desk, market ranking, portfolio review.</summary>
    public static bool CanUseSignalDesk(PlanTier tier) => tier is PlanTier.None or PlanTier.Pro or PlanTier.Max;

    /// <summary>The agent in paper mode.</summary>
    public static bool CanUseAgentPaper(PlanTier tier) => tier is PlanTier.None or PlanTier.Pro or PlanTier.Max;

    /// <summary>The agent placing real orders. Top tier only — this is the one that moves money.</summary>
    public static bool CanUseAgentLive(PlanTier tier) => tier is PlanTier.None or PlanTier.Max;

    /// <summary>The tier a customer needs for a capability, for the upgrade prompt.</summary>
    public static PlanTier RequiredFor(string capability) => capability switch
    {
        "agent_live" => PlanTier.Max,
        _ => PlanTier.Pro
    };

    /// <summary>Text for a locked panel. Names the tier so the next step is obvious.</summary>
    public static string LockedNotice(string capability) =>
        $"Доступно на тарифе {Label(RequiredFor(capability))} и выше. Открыть — в боте лицензий, командой /buy.";
}
