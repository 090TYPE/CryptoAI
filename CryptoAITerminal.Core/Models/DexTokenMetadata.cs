namespace CryptoAITerminal.Core.Models;

/// <summary>
/// Rich profile metadata for a DEX token (contract, socials/community, logo + header
/// image, description, holders, security signals). Sourced from GeckoTerminal's token
/// info endpoint. Also serialized into the AI context so the copilot answers about it.
/// </summary>
public sealed class DexTokenMetadata
{
    public string Address { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;

    public string ImageUrl { get; set; } = string.Empty;
    public string BannerImageUrl { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Twitter { get; set; } = string.Empty;
    public string Telegram { get; set; } = string.Empty;
    public string Discord { get; set; } = string.Empty;

    public long Holders { get; set; }
    public decimal GtScore { get; set; }
    public bool GtVerified { get; set; }
    public bool? IsHoneypot { get; set; }
    public string Categories { get; set; } = string.Empty;
    public string CoingeckoId { get; set; } = string.Empty;

    public decimal DeveloperHoldingPercentage { get; set; }
    public string MintAuthority { get; set; } = string.Empty;
    public string FreezeAuthority { get; set; } = string.Empty;
    public string DeveloperAddress { get; set; } = string.Empty;

    public int Decimals { get; set; }
    public decimal TotalSupply { get; set; }   // normalized (human-readable) circulating/total supply
    public decimal Fdv { get; set; }
    public decimal MarketCap { get; set; }

    // Holder distribution (whale concentration), % of supply held by each cohort.
    public decimal HoldersTop10Pct { get; set; }
    public decimal Holders11To30Pct { get; set; }
    public decimal Holders31To50Pct { get; set; }
    public decimal HoldersRestPct { get; set; }
}
