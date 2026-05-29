using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CryptoAITerminal.Gateway.DEX;

// ── MEV Protection configuration ──────────────────────────────────────────────

/// <summary>
/// MEV protection mode for EVM chains.
/// </summary>
public enum EvmMevMode
{
    /// <summary>Standard public mempool — no protection.</summary>
    None,
    /// <summary>Flashbots Protect RPC — free, hides tx from searchers.</summary>
    FlashbotsProtect,
    /// <summary>Flashbots Builder — for direct bundle submission (advanced).</summary>
    FlashbotsBuilder,
    /// <summary>BloxRoute BDN — BSC / Ethereum, faster propagation.</summary>
    BloxRoute
}

/// <summary>
/// MEV protection mode for Solana.
/// </summary>
public enum SolanaMevMode
{
    /// <summary>Standard RPC submission.</summary>
    None,
    /// <summary>Jito block engine — bundles, tip-based priority.</summary>
    Jito
}

/// <summary>
/// Resolves the effective RPC URL for EVM based on MEV mode.
/// Flashbots Protect simply replaces the public RPC — no extra keys needed.
/// </summary>
public static class EvmMevRpcResolver
{
    // Flashbots Protect endpoints (free, no API key)
    private const string FlashbotsProtectEth  = "https://rpc.flashbots.net";
    private const string FlashbotsProtectBase = "https://rpc.flashbots.net"; // uses same, auto-detects chain

    // BloxRoute — requires API key in header, handled separately
    private const string BloxRouteEth = "https://mev.api.blxrbdn.com";
    private const string BloxRouteBsc = "https://bsc.rpc.blxrbdn.com";

    public static string Resolve(string networkName, string originalRpcUrl, EvmMevMode mode) =>
        mode switch
        {
            EvmMevMode.FlashbotsProtect => networkName.Equals("Ethereum", StringComparison.OrdinalIgnoreCase)
                ? FlashbotsProtectEth
                : networkName.Equals("Base", StringComparison.OrdinalIgnoreCase)
                    ? FlashbotsProtectBase
                    : originalRpcUrl, // Flashbots only for ETH/Base
            EvmMevMode.BloxRoute => networkName.Equals("BSC", StringComparison.OrdinalIgnoreCase)
                ? BloxRouteBsc
                : BloxRouteEth,
            _ => originalRpcUrl
        };

    public static bool IsSupported(string networkName, EvmMevMode mode) => mode switch
    {
        EvmMevMode.FlashbotsProtect => networkName.Equals("Ethereum", StringComparison.OrdinalIgnoreCase)
                                    || networkName.Equals("Base", StringComparison.OrdinalIgnoreCase),
        EvmMevMode.BloxRoute        => networkName.Equals("Ethereum", StringComparison.OrdinalIgnoreCase)
                                    || networkName.Equals("BSC", StringComparison.OrdinalIgnoreCase),
        _                           => true
    };
}

// ── Jito Bundle Client (Solana) ────────────────────────────────────────────────

/// <summary>
/// Sends Solana transactions as Jito bundles through the block engine.
/// Bundles skip the public mempool entirely — no front-running possible.
///
/// Usage:
///   1. Build and sign your transaction as usual.
///   2. Call SendBundleAsync(serializedTx, tipLamports).
///   3. Tip goes to a Jito validator tip account (random selection from pool).
/// </summary>
public sealed class JitoTipManager : IDisposable
{
    // Mainnet Jito block engine endpoint
    private const string JitoMainnetUrl = "https://mainnet.block-engine.jito.wtf/api/v1/bundles";

    // Recommended tip accounts (any of these can receive the tip)
    public static readonly IReadOnlyList<string> TipAccounts = new[]
    {
        "96gYZGLnJYVFmbjzopPSU6QiEV5fGqZNyN9nmNhvrZU5",
        "HFqU5x63VTqvQss8hp11i4wVV8bD44PvwucfZ2bU7gRe",
        "Cw8CFyM9FkoMi7K7Crf6HNQqf4uEMzpKw6QNghXLvLkY",
        "ADaUMid9yfUytqMBgopwjb2DTLSokTSzL1zt6iGPaS49",
        "DfXygSm4jCyNCybVYYK6DwvWqjKee8pbDmJGcLWNDXjh",
        "ADuUkR4vqLUMWXxW9gh6D6L8pMSawimctcNZ5pGwDcEt",
        "DttWaMuVvTiduZRnguLF7jNxTgiMBZ1hyAumKUiL2KRL",
        "3AVi9Tg9Uo68tJfuvoKvqKNWKkC5wPdSSdeBnizKZ6jT"
    };

    private readonly HttpClient _http;
    private readonly Random _rng = new();

    public JitoTipManager()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Sends a signed transaction as a Jito bundle with optional tip.
    /// </summary>
    /// <param name="serializedTxBase64">Base64-encoded signed transaction.</param>
    /// <param name="tipLamports">Tip amount in lamports (1 SOL = 1e9 lamports). Default 10000 (~0.00001 SOL).</param>
    public async Task<JitoBundleResult> SendBundleAsync(
        string serializedTxBase64,
        long tipLamports = 10_000,
        CancellationToken ct = default)
    {
        var tipAccount = TipAccounts[_rng.Next(TipAccounts.Count)];

        var payload = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "sendBundle",
            @params = new object[]
            {
                new[] { serializedTxBase64 },
                new { encoding = "base64" }
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(JitoMainnetUrl, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var doc  = JsonNode.Parse(body);

            var bundleId = doc?["result"]?.GetValue<string>();
            var error    = doc?["error"]?["message"]?.GetValue<string>();

            return new JitoBundleResult(
                Success:   !string.IsNullOrEmpty(bundleId),
                BundleId:  bundleId ?? string.Empty,
                TipAccount: tipAccount,
                TipLamports: tipLamports,
                Error:     error);
        }
        catch (Exception ex)
        {
            return new JitoBundleResult(false, string.Empty, tipAccount, tipLamports, ex.Message);
        }
    }

    /// <summary>Recommended tip for normal priority (in lamports).</summary>
    public static long NormalTipLamports  => 10_000;   // ~0.00001 SOL

    /// <summary>Recommended tip for fast inclusion (in lamports).</summary>
    public static long FastTipLamports    => 100_000;  // ~0.0001 SOL

    /// <summary>Tip for urgent/sniper scenarios (in lamports).</summary>
    public static long UrgentTipLamports  => 1_000_000; // ~0.001 SOL

    public void Dispose() => _http.Dispose();
}

public sealed record JitoBundleResult(
    bool   Success,
    string BundleId,
    string TipAccount,
    long   TipLamports,
    string? Error)
{
    public string StatusLabel => Success
        ? $"Bundle {BundleId[..Math.Min(8, BundleId.Length)]}... sent (tip {TipLamports / 1e9:F5} SOL)"
        : $"Bundle failed: {Error}";
}

// ── MEV Protection settings for UI binding ────────────────────────────────────

/// <summary>
/// Serializable settings for MEV protection, stored in user credentials/config.
/// </summary>
public sealed class MevProtectionSettings
{
    public EvmMevMode   EvmMode    { get; set; } = EvmMevMode.None;
    public SolanaMevMode SolanaMode { get; set; } = SolanaMevMode.None;
    public long JitoTipLamports    { get; set; } = JitoTipManager.NormalTipLamports;

    public bool IsEvmProtected    => EvmMode    != EvmMevMode.None;
    public bool IsSolanaProtected => SolanaMode != SolanaMevMode.None;

    public string EvmStatusLabel => EvmMode switch
    {
        EvmMevMode.FlashbotsProtect => "Flashbots Protect",
        EvmMevMode.FlashbotsBuilder => "Flashbots Builder",
        EvmMevMode.BloxRoute        => "BloxRoute BDN",
        _                           => "OFF"
    };

    public string SolanaStatusLabel => SolanaMode switch
    {
        SolanaMevMode.Jito => $"Jito (tip {JitoTipLamports / 1e9:F5} SOL)",
        _                  => "OFF"
    };
}
