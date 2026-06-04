using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Builds a <see cref="CompositeRule"/> from a natural-language instruction. Claude
/// maps the full sentence when a key is configured; otherwise a regex offline parser
/// handles the common patterns (RSI thresholds, 24h moves, close-all, notify) so the
/// feature still demos without a key.
/// </summary>
public sealed class RuleBuilderAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public sealed record Result(CompositeRule? Rule, string Source, bool IsFallback, string Note);

    public async Task<Result> BuildAsync(string instruction, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return new Result(null, "—", true, "Empty instruction.");

        if (UsesLiveModel)
        {
            try
            {
                var provider = new RuleBuilderAiProvider(ApiKey, Model);
                var spec = await provider.BuildAsync(
                    instruction,
                    Enum.GetNames<ConditionType>(),
                    Enum.GetNames<ActionType>(),
                    Enum.GetNames<RuleCooldown>(),
                    ct).ConfigureAwait(false);

                if (spec is not null)
                {
                    var rule = MapSpec(spec);
                    if (rule.Conditions.Count > 0 || rule.Actions.Count > 0)
                        return new Result(rule, spec.Source, false,
                            $"{rule.Conditions.Count} condition(s), {rule.Actions.Count} action(s).");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        return BuildOffline(instruction);
    }

    // ── Map AI spec (string enums) → CompositeRule ─────────────────────────────

    private static CompositeRule MapSpec(AiRuleSpec spec)
    {
        var rule = new CompositeRule
        {
            Name  = string.IsNullOrWhiteSpace(spec.Name) ? "AI Rule" : spec.Name,
            Logic = Enum.TryParse<ConditionLogic>(spec.Logic, true, out var lg) ? lg : ConditionLogic.And,
            Cooldown = Enum.TryParse<RuleCooldown>(spec.Cooldown, true, out var cd) ? cd : RuleCooldown.Minutes5,
        };

        foreach (var c in spec.Conditions)
            if (Enum.TryParse<ConditionType>(c.Type, true, out var ct))
                rule.Conditions.Add(new RuleCondition
                {
                    Type = ct,
                    Symbol = string.IsNullOrWhiteSpace(c.Symbol) ? "BTCUSDT" : c.Symbol.ToUpperInvariant(),
                    Param1 = c.Param1,
                    Param2 = c.Param2,
                });

        foreach (var a in spec.Actions)
            if (Enum.TryParse<ActionType>(a.Type, true, out var at))
                rule.Actions.Add(new RuleAction
                {
                    Type = at,
                    Symbol = string.IsNullOrWhiteSpace(a.Symbol) ? "BTCUSDT" : a.Symbol.ToUpperInvariant(),
                    Amount = a.Amount,
                    Message = a.Message ?? string.Empty,
                });

        return rule;
    }

    // ── Offline regex parser for common phrasings ──────────────────────────────

    private static readonly Regex SymbolRx = new(@"\b([A-Z]{2,10}USDT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RsiBelowRx = new(@"rsi\D{0,12}?(below|under|drops?\s+below|<)\s*(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RsiAboveRx = new(@"rsi\D{0,12}?(above|over|exceeds?|>)\s*(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DropRx = new(@"(drops?|falls?|down|loses?)\D{0,8}?(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RiseRx = new(@"(rises?|gains?|up|pumps?)\D{0,8}?(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Result BuildOffline(string text)
    {
        var symbol = SymbolRx.Match(text) is { Success: true } sm ? sm.Groups[1].Value.ToUpperInvariant() : "BTCUSDT";
        var rule = new CompositeRule { Name = "AI Rule (offline)" };
        var notes = new List<string>();

        if (RsiBelowRx.Match(text) is { Success: true } rb && int.TryParse(rb.Groups[2].Value, out var lvlB))
        {
            rule.Conditions.Add(new RuleCondition { Type = ConditionType.RsiBelow, Symbol = symbol, Param1 = 14m, Param2 = lvlB });
            notes.Add($"RSI<{lvlB}");
        }
        if (RsiAboveRx.Match(text) is { Success: true } ra && int.TryParse(ra.Groups[2].Value, out var lvlA))
        {
            rule.Conditions.Add(new RuleCondition { Type = ConditionType.RsiAbove, Symbol = symbol, Param1 = 14m, Param2 = lvlA });
            notes.Add($"RSI>{lvlA}");
        }
        if (DropRx.Match(text) is { Success: true } dr && decimal.TryParse(dr.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var dpct))
        {
            rule.Conditions.Add(new RuleCondition { Type = ConditionType.Price24hChangeBelow, Symbol = symbol, Param1 = -dpct });
            notes.Add($"24h≤-{dpct}%");
        }
        if (RiseRx.Match(text) is { Success: true } rr && decimal.TryParse(rr.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var rpct))
        {
            rule.Conditions.Add(new RuleCondition { Type = ConditionType.Price24hChangeAbove, Symbol = symbol, Param1 = rpct });
            notes.Add($"24h≥{rpct}%");
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("close all") || lower.Contains("close everything") || lower.Contains("flatten"))
        {
            rule.Actions.Add(new RuleAction { Type = ActionType.CloseAllPositions, Symbol = symbol });
            notes.Add("close all");
        }
        if (lower.Contains("notify") || lower.Contains("alert") || lower.Contains("tell me") || lower.Contains("ping"))
        {
            rule.Actions.Add(new RuleAction { Type = ActionType.Notify, Symbol = symbol, Message = "AI rule triggered" });
            notes.Add("notify");
        }
        if (lower.Contains("buy the dip") || lower.Contains("start dca") || lower.Contains("dca"))
        {
            rule.Actions.Add(new RuleAction { Type = ActionType.StartDcaBuy, Symbol = symbol, Amount = 50m });
            notes.Add("DCA buy");
        }
        if (lower.Contains("breakeven") || lower.Contains("break even") || lower.Contains("move stop"))
        {
            rule.Actions.Add(new RuleAction { Type = ActionType.MoveStopToBreakeven, Symbol = symbol });
            notes.Add("stop→BE");
        }

        // A rule needs at least one condition AND one action to be useful; default a Notify if missing.
        if (rule.Conditions.Count > 0 && rule.Actions.Count == 0)
        {
            rule.Actions.Add(new RuleAction { Type = ActionType.Notify, Symbol = symbol, Message = "AI rule triggered" });
            notes.Add("notify");
        }

        if (rule.Conditions.Count == 0)
            return new Result(null, "Heuristic (offline)", true,
                "Couldn't parse a condition offline — add an API key for full natural-language rules.");

        return new Result(rule, "Heuristic (offline)", true, "Parsed: " + string.Join(", ", notes) + ".");
    }
}
