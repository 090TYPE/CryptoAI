using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Saves and loads named configuration profiles.
/// Each profile is a JSON file in %AppData%/CryptoAITerminal/profiles/.
///
/// Profiles contain all tunable settings (bot params, sniper limits, risk config)
/// but NOT API keys — those stay in DPAPI-encrypted credentials storage.
/// </summary>
public sealed class ConfigurationProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public string ProfileDir { get; }

    /// <summary>Folder for sharing: export drops files here, import reads them back.</summary>
    public string SharedDir { get; }

    private const string ShareExtension = ".caiprofile";

    /// <param name="profileDir">Override the profile store dir (tests). Null → %AppData%.</param>
    /// <param name="sharedDir">Override the shared dir (tests). Null → Documents.</param>
    public ConfigurationProfileService(string? profileDir = null, string? sharedDir = null)
    {
        ProfileDir = profileDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal", "profiles");
        SharedDir = sharedDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CryptoAITerminal", "SharedProfiles");

        Directory.CreateDirectory(ProfileDir);
        Directory.CreateDirectory(SharedDir);
    }

    // ── Profile management ────────────────────────────────────────────────────

    public IReadOnlyList<string> ListProfiles() =>
        Directory.GetFiles(ProfileDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n)
            .ToList();

    public void Save(string name, TradingProfile profile)
    {
        profile.Name      = name;
        profile.SavedUtc  = DateTime.UtcNow;
        var path = GetPath(name);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOpts));
    }

    public TradingProfile? Load(string name)
    {
        var path = GetPath(name);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TradingProfile>(json, JsonOpts);
        }
        catch { return null; }
    }

    public bool Delete(string name)
    {
        var path = GetPath(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public bool Exists(string name) => File.Exists(GetPath(name));

    // ── Sharing: export / import ──────────────────────────────────────────────

    /// <summary>Write a saved profile to the shared folder as a portable file. Returns its path.</summary>
    public string ExportToShared(string name)
    {
        var profile = Load(name) ?? throw new FileNotFoundException($"Profile '{name}' not found.");
        Directory.CreateDirectory(SharedDir);
        var safe = SanitizeName(name);
        var dest = Path.Combine(SharedDir, safe + ShareExtension);
        File.WriteAllText(dest, JsonSerializer.Serialize(profile, JsonOpts));
        return dest;
    }

    /// <summary>
    /// Import every shareable profile (*.caiprofile, *.json) found in the shared
    /// folder into the local profile store. Returns the names imported.
    /// </summary>
    public IReadOnlyList<string> ImportFromShared()
    {
        var imported = new List<string>();
        if (!Directory.Exists(SharedDir)) return imported;

        var files = Directory.GetFiles(SharedDir, "*" + ShareExtension)
            .Concat(Directory.GetFiles(SharedDir, "*.json"));

        foreach (var file in files)
        {
            try
            {
                var profile = JsonSerializer.Deserialize<TradingProfile>(File.ReadAllText(file), JsonOpts);
                if (profile is null) continue;

                var name = string.IsNullOrWhiteSpace(profile.Name)
                    ? Path.GetFileNameWithoutExtension(file)
                    : profile.Name;

                // Don't overwrite an existing profile that has different content —
                // import under "name (2)", "name (3)", … instead.
                var finalName = name;
                if (Exists(name) && IsDifferent(name, profile))
                {
                    var n = 2;
                    while (Exists($"{name} ({n})") && IsDifferent($"{name} ({n})", profile))
                        n++;
                    finalName = $"{name} ({n})";
                }

                Save(finalName, profile);
                imported.Add(finalName);
            }
            catch { /* skip malformed files */ }
        }
        return imported;
    }

    private bool IsDifferent(string name, TradingProfile incoming)
    {
        var existing = Load(name);
        if (existing is null) return true;
        return JsonSerializer.Serialize(existing, JsonOpts) != JsonSerializer.Serialize(incoming, JsonOpts);
    }

    private static string SanitizeName(string name) =>
        string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' '));

    private string GetPath(string name)
    {
        var safe = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' '));
        return Path.Combine(ProfileDir, safe + ".json");
    }
}

// ── Profile model ─────────────────────────────────────────────────────────────

/// <summary>
/// Complete snapshot of all user-configurable settings.
/// API keys are NOT included — they live in DPAPI storage.
/// </summary>
public sealed class TradingProfile
{
    public string   Name      { get; set; } = string.Empty;
    public DateTime SavedUtc  { get; set; } = DateTime.UtcNow;
    public string   Description { get; set; } = string.Empty;

    // ── AI Bot ────────────────────────────────────────────────────────────────
    public string  BotExchange         { get; set; } = "Binance";
    public string  BotMarketMode       { get; set; } = "Spot";
    public string  BotSymbol           { get; set; } = "BTCUSDT";
    public decimal BotQuantity         { get; set; } = 0.001m;
    public decimal BotMaxRiskPerTrade  { get; set; } = 100m;
    public string  BotStrategy         { get; set; } = "MA Cross";
    public int     BotMaFastPeriod     { get; set; } = 10;
    public int     BotMaSlowPeriod     { get; set; } = 30;
    public int     BotRsiPeriod        { get; set; } = 14;
    public decimal BotRsiOverbought    { get; set; } = 70m;
    public decimal BotRsiOversold      { get; set; } = 30m;
    public int     BotBbPeriod         { get; set; } = 20;
    public decimal BotBbDeviation      { get; set; } = 2m;
    public int     BotBreakoutPeriod   { get; set; } = 20;
    public int     BotMacdFast         { get; set; } = 12;
    public int     BotMacdSlow         { get; set; } = 26;
    public int     BotMacdSignal       { get; set; } = 9;
    public decimal BotVwapBandPct      { get; set; } = 0.05m;
    public bool    BotTpEnabled        { get; set; } = true;
    public decimal BotTpPercent        { get; set; } = 2m;
    public bool    BotSlEnabled        { get; set; } = true;
    public decimal BotSlPercent        { get; set; } = 1m;
    public bool    BotTrailingStop     { get; set; }
    public bool    BotPartialTp        { get; set; }
    public decimal BotPartialTpClose   { get; set; } = 50m;
    public decimal BotPartialTp2Pct    { get; set; } = 4m;
    public int     BotFuturesLeverage  { get; set; } = 3;
    public string  BotFuturesMargin    { get; set; } = "Cross";

    // ── DEX / Sniper ──────────────────────────────────────────────────────────
    public decimal DexSlippagePercent  { get; set; } = 3m;
    public decimal DexBuyAmount        { get; set; } = 0.01m;
    public string  DexQuoteAsset       { get; set; } = string.Empty;

    public decimal SniperBuyAmount          { get; set; } = 0.05m;
    public int     SniperMaxPositions       { get; set; } = 5;
    public decimal SniperMinLiquidity       { get; set; } = 5000m;
    public decimal SniperMaxSlippage        { get; set; } = 5m;
    public decimal SniperTakeProfitPct      { get; set; } = 50m;
    public decimal SniperStopLossPct        { get; set; } = 20m;
    public int     SniperMaxHoldMinutes     { get; set; } = 60;
    public decimal SniperDailyLossCapUsd    { get; set; } = 50m;
    public int     SniperMaxDailyBuys       { get; set; } = 10;

    // ── Grid Bot ──────────────────────────────────────────────────────────────
    public string  GridSymbol          { get; set; } = "BTCUSDT";
    public decimal GridLower           { get; set; } = 90000m;
    public decimal GridUpper           { get; set; } = 100000m;
    public int     GridLevels          { get; set; } = 10;
    public decimal GridQtyPerLevel     { get; set; } = 0.001m;

    // ── DCA Bot ───────────────────────────────────────────────────────────────
    public string  DcaExchange         { get; set; } = "Binance";

    // ── Risk ──────────────────────────────────────────────────────────────────
    public decimal RiskMaxDailyLossUsd { get; set; } = 200m;
}
