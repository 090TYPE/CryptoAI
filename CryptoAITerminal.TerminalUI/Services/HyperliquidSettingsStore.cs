using System;
using System.IO;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Persisted opt-in for live Hyperliquid order placement. Off + testnet by default.</summary>
public sealed record HyperliquidSettings(bool EnableLiveOrders, bool Testnet)
{
    public static readonly HyperliquidSettings Default = new(EnableLiveOrders: false, Testnet: true);
}

/// <summary>Best-effort JSON store for the Hyperliquid live-order opt-in. Never throws.</summary>
public sealed class HyperliquidSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _path;

    public HyperliquidSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal",
            "hyperliquid_settings.json");
    }

    public HyperliquidSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return HyperliquidSettings.Default;
            var json = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(json)
                ? HyperliquidSettings.Default
                : JsonSerializer.Deserialize<HyperliquidSettings>(json, Options) ?? HyperliquidSettings.Default;
        }
        catch
        {
            return HyperliquidSettings.Default;
        }
    }

    public void Save(HyperliquidSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // best-effort
        }
    }
}
