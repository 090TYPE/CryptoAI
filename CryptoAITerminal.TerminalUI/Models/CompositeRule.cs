using System;
using System.Collections.Generic;

namespace CryptoAITerminal.TerminalUI.Services;

// ── Condition enumeration ─────────────────────────────────────────────────────

public enum ConditionType
{
    RsiBelow,
    RsiAbove,
    PriceAboveMa,
    PriceBelowMa,
    VolumeAboveSma,
    OpenPositionPnlAbove,
    OpenPositionPnlBelow,
    FundingRateAbove,
    Price24hChangeAbove,
    Price24hChangeBelow,
}

public enum ConditionLogic { And, Or }

// ── Action enumeration ────────────────────────────────────────────────────────

public enum ActionType
{
    StartDcaBuy,
    MoveStopToBreakeven,
    StartFundingArb,
    PauseGridBot,
    ResumeGridBot,
    CloseAllPositions,
    Notify,
}

// ── Cooldown ──────────────────────────────────────────────────────────────────

public enum RuleCooldown
{
    Once,
    Seconds30,
    Minutes1,
    Minutes5,
    Minutes15,
    Hours1,
    Hours4,
    Unlimited,
}

// ── Condition & Action records ────────────────────────────────────────────────

public sealed class RuleCondition
{
    public Guid         Id      { get; init; } = Guid.NewGuid();
    public ConditionType Type   { get; set; }  = ConditionType.RsiBelow;
    /// <summary>Trading symbol, e.g. "BTCUSDT" or the sentinel "ANY".</summary>
    public string       Symbol  { get; set; }  = "BTCUSDT";
    /// <summary>Primary parameter: period or threshold.</summary>
    public decimal      Param1  { get; set; }  = 14m;
    /// <summary>Secondary parameter: threshold or multiplier.</summary>
    public decimal      Param2  { get; set; }  = 30m;
}

public sealed class RuleAction
{
    public Guid       Id      { get; init; } = Guid.NewGuid();
    public ActionType Type    { get; set; }  = ActionType.StartDcaBuy;
    public string     Symbol  { get; set; }  = "BTCUSDT";
    public decimal    Amount  { get; set; }  = 50m;
    public string     Message { get; set; }  = string.Empty;
}

// ── Top-level rule ────────────────────────────────────────────────────────────

public sealed class CompositeRule
{
    public Guid             Id           { get; init; } = Guid.NewGuid();
    public string           Name         { get; set; }  = "New Rule";
    public bool             IsEnabled    { get; set; }  = true;
    public ConditionLogic   Logic        { get; set; }  = ConditionLogic.And;
    public List<RuleCondition> Conditions { get; set; } = [];
    public List<RuleAction>    Actions   { get; set; }  = [];
    public RuleCooldown     Cooldown     { get; set; }  = RuleCooldown.Minutes5;
    public DateTime?        LastTriggeredAt { get; set; }
    public int              TriggerCount { get; set; }
}
