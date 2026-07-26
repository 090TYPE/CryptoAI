using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

namespace CryptoAITerminal.TerminalUI.ViewModels.SettingsDesk;

/// <summary>Palette, nav map and static UI metadata for the Settings desk.
/// Holds no settings values — every value shown by the desk comes from the shell VM.</summary>
public static class SettingsData
{
    public const string Accent = BotsDeskData.Accent;
    public const string Green = BotsDeskData.Green;
    public const string Red = BotsDeskData.Red;
    public const string Amber = BotsDeskData.Amber;
    public const string Violet = "#b48cff";
    public const string Text = BotsDeskData.Text;
    public const string Text3 = BotsDeskData.Text3;
    public const string Dim = BotsDeskData.Dim;
    public const string Dimmer = BotsDeskData.Dimmer;
    public const string Faint = BotsDeskData.Faint;
    public const string Ghost = BotsDeskData.Ghost;

    public static string Alpha(string hex, string aa) => BotsDeskData.Alpha(hex, aa);

    // group label, (id,label)[]
    public static readonly (string label, (string id, string label)[] items)[] Nav =
    {
        ("ASSISTANT", new[] { ("ai", "AI provider") }),
        ("ACCOUNT", new[] { ("license", "License & profiles") }),
        ("CONNECTIVITY", new[] { ("keys", "Exchange keys"), ("dex", "DEX & networks"), ("data", "Data providers"), ("notify", "Notifications") }),
        ("TRADING", new[] { ("alerts", "Alerts"), ("exec", "Execution & hotkeys") }),
    };

    // Exchange id, display name, needs a passphrase
    public static readonly (string id, string name, bool passphrase)[] Exchanges =
    {
        ("binance", "Binance", false),
        ("bybit", "Bybit", false),
        ("okx", "OKX", true),
        ("kucoin", "KuCoin", true),
    };

    /// <summary>Hotkey rows — key matches the <c>HotkeySettings</c> property name.</summary>
    public static readonly (string k, string label, string color, string placeholder)[] Hotkeys =
    {
        ("BuyMarket", "Buy market", Green, "e.g. B"),
        ("SellMarket", "Sell market", Red, "e.g. S"),
        ("Allocation25", "Allocation 25%", Amber, "e.g. D1"),
        ("Allocation50", "Allocation 50%", Amber, "e.g. D2"),
        ("Allocation100", "Allocation 100%", Amber, "e.g. D3"),
        ("CancelOrders", "Cancel all orders", Red, "e.g. Escape"),
        ("FocusPair", "Focus trading pair", Text3, "e.g. F"),
    };
}
