using Avalonia.Input;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Persisted trading hotkey bindings (single-key, no modifier).
/// Keys are stored as <see cref="Key"/> enum name strings so they survive
/// an Avalonia version bump.
/// </summary>
public sealed class HotkeySettings
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CryptoAITerminal", "hotkeys.json");

    // ── Stored key names ──────────────────────────────────────────────────────
    public string BuyMarket     { get; set; } = "B";
    public string SellMarket    { get; set; } = "S";
    public string Allocation25  { get; set; } = "D1";
    public string Allocation50  { get; set; } = "D2";
    public string Allocation100 { get; set; } = "D3";
    public string CancelOrders  { get; set; } = "Escape";
    public string FocusPair     { get; set; } = "F";

    // ── Parsed Key properties (not serialised) ────────────────────────────────
    [JsonIgnore] public Key BuyMarketKey     => Parse(BuyMarket,     Key.B);
    [JsonIgnore] public Key SellMarketKey    => Parse(SellMarket,    Key.S);
    [JsonIgnore] public Key Allocation25Key  => Parse(Allocation25,  Key.D1);
    [JsonIgnore] public Key Allocation50Key  => Parse(Allocation50,  Key.D2);
    [JsonIgnore] public Key Allocation100Key => Parse(Allocation100, Key.D3);
    [JsonIgnore] public Key CancelOrdersKey  => Parse(CancelOrders,  Key.Escape);
    [JsonIgnore] public Key FocusPairKey     => Parse(FocusPair,     Key.F);

    // ── Display labels ────────────────────────────────────────────────────────
    [JsonIgnore] public string BuyMarketDisplay     => FormatKey(BuyMarket);
    [JsonIgnore] public string SellMarketDisplay    => FormatKey(SellMarket);
    [JsonIgnore] public string Allocation25Display  => FormatKey(Allocation25);
    [JsonIgnore] public string Allocation50Display  => FormatKey(Allocation50);
    [JsonIgnore] public string Allocation100Display => FormatKey(Allocation100);
    [JsonIgnore] public string CancelOrdersDisplay  => FormatKey(CancelOrders);
    [JsonIgnore] public string FocusPairDisplay     => FormatKey(FocusPair);

    // ── Persistence ───────────────────────────────────────────────────────────

    public static HotkeySettings Load()
    {
        if (!File.Exists(StoragePath)) return new HotkeySettings();
        try
        {
            var json    = File.ReadAllText(StoragePath);
            return JsonSerializer.Deserialize<HotkeySettings>(json, JsonOpts) ?? new HotkeySettings();
        }
        catch { return new HotkeySettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            File.WriteAllText(StoragePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-critical */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Key Parse(string name, Key fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        return Enum.TryParse<Key>(name, ignoreCase: true, out var key) ? key : fallback;
    }

    private static string FormatKey(string name) => name switch
    {
        "D1" => "1", "D2" => "2", "D3" => "3",
        "D4" => "4", "D5" => "5", "D6" => "6",
        "D7" => "7", "D8" => "8", "D9" => "9",
        "D0" => "0", "Escape" => "Esc",
        _ => name
    };
}
