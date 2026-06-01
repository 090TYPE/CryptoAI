namespace CryptoAITerminal.Core.Models;

/// <summary>
/// AI risk assessment of a single token, produced either by an LLM
/// (Claude) or a deterministic heuristic fallback when no API key is
/// configured. Surfaced on the sniper candidate card.
/// </summary>
public sealed class TokenAiVerdict
{
    /// <summary>0–100, higher = riskier (matches the sniper RiskScore convention).</summary>
    public int RiskScore { get; init; }

    /// <summary>One of: AVOID, RISKY, NEUTRAL, FAVORABLE.</summary>
    public string Verdict { get; init; } = "NEUTRAL";

    /// <summary>Short bullet flags, e.g. "low liquidity", "fresh deployer".</summary>
    public string[] RedFlags { get; init; } = [];

    /// <summary>One-sentence rationale.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Where the verdict came from, e.g. "Claude claude-sonnet-4-6" or "Heuristic".</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>True when produced by the offline heuristic rather than a live model call.</summary>
    public bool IsFallback { get; init; }

    public string RedFlagsText => RedFlags is { Length: > 0 }
        ? string.Join(" · ", RedFlags)
        : "no flags";

    /// <summary>Accent color for the verdict badge (XAML binds to this hex string).</summary>
    public string AccentHex => Verdict.ToUpperInvariant() switch
    {
        "AVOID"     => "#FF5D73",
        "RISKY"     => "#FF8A4C",
        "FAVORABLE" => "#3DDC84",
        _           => "#8FA3B8"
    };

    public static TokenAiVerdict Pending() => new()
    {
        Verdict = "PENDING",
        Reason  = "AI assessment not run yet.",
        Source  = string.Empty
    };
}
