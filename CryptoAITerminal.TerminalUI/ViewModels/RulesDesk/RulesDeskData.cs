using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

namespace CryptoAITerminal.TerminalUI.ViewModels.RulesDesk;

/// <summary>A starting point for the rule editor — not a stored rule and not
/// sample data. Picking one loads its clauses into the live editor; nothing is
/// added to the engine until the user saves.</summary>
public sealed class RulePreset
{
    public string Name { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Desc { get; init; } = "";
    public ConditionLogic Logic { get; init; } = ConditionLogic.And;
    public RuleCooldown Cooldown { get; init; } = RuleCooldown.Minutes15;
    public List<RuleCondition> Conditions { get; init; } = new();
    public List<RuleAction> Actions { get; init; } = new();
}

/// <summary>Palette, label maps and editor templates for the Rules desk.
/// Every rule, trigger and counter comes from <see cref="CompositeRuleViewModel"/>
/// at runtime — nothing is seeded here.</summary>
public static class RulesDeskData
{
    // ── palette ──────────────────────────────────────────────────────────────
    public const string Accent = BotsDeskData.Accent;
    public const string Green = BotsDeskData.Green;
    public const string Red = BotsDeskData.Red;
    public const string Amber = BotsDeskData.Amber;
    public const string Text = BotsDeskData.Text;
    public const string Text3 = BotsDeskData.Text3;
    public const string Dim = BotsDeskData.Dim;
    public const string Dimmer = BotsDeskData.Dimmer;
    public const string Faint = BotsDeskData.Faint;

    public static string Alpha(string hex, string aa) => BotsDeskData.Alpha(hex, aa);

    // ── reference lists ──────────────────────────────────────────────────────
    // The two type lists are the live editor's own vocabulary: those are exactly
    // the ConditionType / ActionType values RuleConditionEditorVM and
    // RuleActionEditorVM can round-trip. Offering more would silently downgrade
    // the picked type on save.

    public static readonly string[] CondTypes = RuleConditionEditorVM.AllTypes.ToArray();
    public static readonly string[] ActTypes = RuleActionEditorVM.AllTypes.ToArray();

    /// <summary>Cooldown labels derived from the real RuleCooldown enum. The
    /// wording must stay identical to CompositeRuleViewModel.CooldownOptions —
    /// the label is what the editor round-trips back into the enum.</summary>
    public static readonly string[] Cooldowns =
        Enum.GetValues<RuleCooldown>().Select(CooldownLabel).ToArray();

    public static string CooldownLabel(RuleCooldown cd) => cd switch
    {
        RuleCooldown.Once => "One-time",
        RuleCooldown.Seconds30 => "30 seconds",
        RuleCooldown.Minutes1 => "1 minute",
        RuleCooldown.Minutes5 => "5 minutes",
        RuleCooldown.Minutes15 => "15 minutes",
        RuleCooldown.Hours1 => "1 hour",
        RuleCooldown.Hours4 => "4 hours",
        RuleCooldown.Unlimited => "Always",
        _ => "5 minutes"
    };

    public static string LogicShort(ConditionLogic logic) => logic == ConditionLogic.And ? "ALL" : "ANY";

    // ── editor field labels (mirror the live editor rows) ────────────────────

    public static string CooldownTail(string cooldownLabel) => cooldownLabel switch
    {
        "Always" => " It can fire again on the very next evaluation.",
        "One-time" => " It fires once and then never again.",
        _ => " Then it waits " + cooldownLabel.ToLowerInvariant() + " before it can fire again."
    };

    /// <summary>Warning shown under actions that move real money or positions.
    /// Keyed by the editor's own action labels.</summary>
    public static string RiskNote(string actionLabel) => actionLabel switch
    {
        "Close All Positions" => "Closes every matching open position at market when this rule fires.",
        "DCA Buy" => "Places real buy orders for the configured USD amount when this rule fires.",
        _ => ""
    };

    public static bool Risky(string actionLabel) => RiskNote(actionLabel).Length > 0;

    // ── editor templates ─────────────────────────────────────────────────────

    /// <summary>Prefilled drafts for the editor. They carry no history or
    /// statistics — a preset has never run, it is only an input shortcut.</summary>
    public static List<RulePreset> Presets() => new()
    {
        new RulePreset
        {
            Name = "Oversold DCA ladder",
            Meta = "RSI + volume",
            Desc = "Buys when RSI drops under 30 with volume confirmation, then notifies you.",
            Cooldown = RuleCooldown.Hours1,
            Conditions = new List<RuleCondition>
            {
                new() { Type = ConditionType.RsiBelow,       Symbol = "BTCUSDT", Param1 = 14m, Param2 = 30m },
                new() { Type = ConditionType.VolumeAboveSma, Symbol = "BTCUSDT", Param1 = 20m, Param2 = 2m },
            },
            Actions = new List<RuleAction>
            {
                new() { Type = ActionType.StartDcaBuy, Symbol = "BTCUSDT", Amount = 200m },
                new() { Type = ActionType.Notify, Message = "Oversold ladder started" },
            }
        },
        new RulePreset
        {
            Name = "Break-even protector",
            Meta = "position P&L",
            Desc = "Moves the stop to break-even once an open position is +3% in profit.",
            Cooldown = RuleCooldown.Minutes15,
            Conditions = new List<RuleCondition> { new() { Type = ConditionType.OpenPositionPnlAbove, Symbol = "ANY", Param1 = 3m } },
            Actions = new List<RuleAction> { new() { Type = ActionType.MoveStopToBreakeven, Symbol = "ANY" } }
        },
        new RulePreset
        {
            Name = "Funding harvester",
            Meta = "funding rate",
            Desc = "Opens the funding arb leg whenever the funding rate exceeds 0.03%.",
            Cooldown = RuleCooldown.Hours4,
            Conditions = new List<RuleCondition> { new() { Type = ConditionType.FundingRateAbove, Symbol = "BTCUSDT", Param1 = 0.03m } },
            Actions = new List<RuleAction> { new() { Type = ActionType.StartFundingArb, Symbol = "BTCUSDT" } }
        },
        new RulePreset
        {
            Name = "Volatility circuit breaker",
            Meta = "volume spike",
            Desc = "Pauses the grid bot and pings you when volume runs 4× above its average.",
            Cooldown = RuleCooldown.Minutes15,
            Conditions = new List<RuleCondition> { new() { Type = ConditionType.VolumeAboveSma, Symbol = "BTCUSDT", Param1 = 20m, Param2 = 4m } },
            Actions = new List<RuleAction>
            {
                new() { Type = ActionType.PauseGridBot, Symbol = "BTCUSDT" },
                new() { Type = ActionType.Notify, Message = "Grid paused — volume spike" },
            }
        },
    };

    /// <summary>Prompt starters for the NL generator. Plain input text — they
    /// describe nothing that exists until the model answers.</summary>
    public static readonly string[] NlExamples =
    {
        "when BTC RSI drops below 30, DCA buy $200 and notify me",
        "if ETHUSDT position loses 5%, close all positions",
        "pause the grid bot when volume spikes 3× on BTCUSDT",
    };
}
