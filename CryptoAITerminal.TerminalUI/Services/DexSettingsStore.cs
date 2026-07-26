using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// DEX execution settings — per-network RPC endpoints, MEV protection and swap defaults.
/// </summary>
/// <remarks>
/// All of this was already implemented in the gateway layer with no way in. Every gateway
/// exposed a <c>MevMode</c> that nothing ever assigned, so Flashbots and Jito were dead code;
/// the RPC endpoints were a private table of free public nodes that rate-limit under load,
/// with no way to point the terminal at a paid one.
/// </remarks>
public sealed class DexSettings
{
    /// <summary>Name of an <see cref="Gateway.DEX.EvmMevMode"/> member. Unknown values fall back to None.</summary>
    public string EvmMev { get; set; } = nameof(EvmMevMode.None);

    /// <summary>Name of a <see cref="Gateway.DEX.SolanaMevMode"/> member. Unknown values fall back to None.</summary>
    public string SolanaMev { get; set; } = nameof(SolanaMevMode.None);

    /// <summary>Jito bundle tip. Only read when <see cref="SolanaMev"/> is Jito.</summary>
    public long JitoTipLamports { get; set; } = 10_000;

    /// <summary>Slippage the swap panel starts on. Clamped to the panel's own 0.1–50 range.</summary>
    public decimal DefaultSlippagePercent { get; set; } = 3m;

    /// <summary>Whether manual buys route through the cross-DEX aggregator by default.</summary>
    public bool BestRouteByDefault { get; set; } = true;

    /// <summary>Network name → RPC URL. A missing or blank entry means "use the built-in endpoint".</summary>
    public Dictionary<string, string> RpcOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An independent copy. The Settings desk edits a clone, so a half-typed endpoint
    /// never reaches the gateways before the user presses save.</summary>
    public DexSettings Clone() => new()
    {
        EvmMev = EvmMev,
        SolanaMev = SolanaMev,
        JitoTipLamports = JitoTipLamports,
        DefaultSlippagePercent = DefaultSlippagePercent,
        BestRouteByDefault = BestRouteByDefault,
        RpcOverrides = new Dictionary<string, string>(RpcOverrides, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>Best-effort JSON store for <see cref="DexSettings"/>. Never throws on read.</summary>
public static class DexSettingsStore
{
    /// <summary>What <c>SolanaTradeGateway</c> falls back to when no endpoint is supplied.</summary>
    public const string SolanaDefaultRpc = "https://api.mainnet-beta.solana.com";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "dex_settings.json");

    private static DexSettings? _cached;

    /// <summary>The live settings. Read on first use, then kept in memory.</summary>
    public static DexSettings Current => _cached ??= Load();

    public static DexSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new DexSettings();
            var json = File.ReadAllText(FilePath);
            return string.IsNullOrWhiteSpace(json)
                ? new DexSettings()
                : JsonSerializer.Deserialize<DexSettings>(json, Options) ?? new DexSettings();
        }
        catch
        {
            return new DexSettings();
        }
    }

    /// <summary>Persists and swaps in the new settings. Returns an error message, or null on success.</summary>
    public static string? Save(DexSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            _cached = settings.Clone();
            return null;
        }
        catch (Exception ex)
        {
            return "Could not save the DEX settings: " + ex.Message;
        }
    }

    /// <summary>The user's endpoint for a network, or null when the built-in one should be used.</summary>
    public static string? RpcOverride(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return null;
        return Current.RpcOverrides.TryGetValue(network.Trim(), out var url) && !string.IsNullOrWhiteSpace(url)
            ? url.Trim()
            : null;
    }

    public static EvmMevMode EvmMode =>
        Enum.TryParse<EvmMevMode>(Current.EvmMev, ignoreCase: true, out var m) ? m : EvmMevMode.None;

    public static SolanaMevMode SolanaMode =>
        Enum.TryParse<SolanaMevMode>(Current.SolanaMev, ignoreCase: true, out var m) ? m : SolanaMevMode.None;

    /// <summary>Jito tip formatted for display, e.g. "0.00001 SOL".</summary>
    public static string TipLabel(long lamports) =>
        (lamports / 1_000_000_000m).ToString("0.#########", CultureInfo.InvariantCulture) + " SOL";
}
