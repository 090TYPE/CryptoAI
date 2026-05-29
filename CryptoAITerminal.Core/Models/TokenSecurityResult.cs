namespace CryptoAITerminal.Core.Models;

public class TokenSecurityResult
{
    // ── Primary fields set by scanner ────────────────────────────────────────

    public bool    IsHoneypot                   { get; set; }
    public bool    HasMintFunction               { get; set; }
    public bool    IsOwnershipRenounced          { get; set; }
    public bool    HasBlacklist                  { get; set; }
    public bool    IsProxy                       { get; set; }
    public bool    HasSelfDestruct               { get; set; }
    public bool    HiddenOwner                   { get; set; }
    public decimal TopHolderConcentrationPercent { get; set; }  // 0–100
    public decimal BuyTaxPercent                 { get; set; }
    public decimal SellTaxPercent                { get; set; }
    public int     SecurityScore                 { get; set; }  // 0–100, higher = safer
    public string[] Flags                        { get; set; } = [];
    public string  Verdict                       { get; set; } = "Pending";
    public string  Source                        { get; set; } = string.Empty;
    public string? Error                         { get; set; }

    // ── Computed aliases (used by SniperCandidateViewModel & XAML) ───────────

    /// Alias for <see cref="HasMintFunction"/>
    public bool IsMintable => HasMintFunction;

    /// Alias for <see cref="IsOwnershipRenounced"/>
    public bool OwnershipRenounced => IsOwnershipRenounced;

    /// Alias for <see cref="TopHolderConcentrationPercent"/>
    public decimal TopHolderPercent => TopHolderConcentrationPercent;

    /// True when a single wallet holds > 30 % of supply.
    public bool TopHolderConcentrated => TopHolderConcentrationPercent > 30m;

    /// Alias for <see cref="SecurityScore"/>
    public int Score => SecurityScore;

    /// True if the scan failed or returned no actionable data.
    public bool ScanFailed => Error is not null || Verdict == "Unknown";

    // ── Deployer analysis (set separately, optional) ─────────────────────────

    /// <summary>Wallet that deployed this token contract. Null until analysed.</summary>
    public string?  DeployerAddress          { get; set; }
    public int      DeployerTokenCount       { get; set; }
    public int      DeployerRugpullCount     { get; set; }
    public int      DeployerWalletAgeMonths  { get; set; }
    public string?  DeployerRiskLabel        { get; set; }
    public string?  DeployerRiskBrush        { get; set; }
    public bool     HasDeployerAnalysis      => DeployerAddress is not null;

    // ── Factory helpers ───────────────────────────────────────────────────────

    public static TokenSecurityResult Unknown(string reason) => new()
    {
        Verdict       = "Unknown",
        Source        = reason,
        SecurityScore = 50,
        Error         = reason
    };

    /// <summary>Apply deployer data without creating a dependency on Gateway.DEX.</summary>
    public void ApplyDeployerRaw(
        string address, int tokenCount, int rugpulls, int walletAgeMonths,
        string riskLabel, string riskBrush, int riskLevel)
    {
        DeployerAddress         = address;
        DeployerTokenCount      = tokenCount;
        DeployerRugpullCount    = rugpulls;
        DeployerWalletAgeMonths = walletAgeMonths;
        DeployerRiskLabel       = riskLabel;
        DeployerRiskBrush       = riskBrush;

        // Downgrade security score if deployer has rugpull history
        // riskLevel: 0=Unknown, 1=Low, 2=Medium, 3=High
        if (riskLevel >= 3) SecurityScore = Math.Min(SecurityScore, 20);
        else if (riskLevel >= 2) SecurityScore = Math.Min(SecurityScore, 50);
    }
}
