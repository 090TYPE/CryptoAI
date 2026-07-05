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
}
