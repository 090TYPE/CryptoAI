using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

/// <summary>
/// Right-hand AI rail. Every panel is a thin projection of an existing shell
/// view-model — pre-trade risk, correlation insight, options advisor, the
/// copilot and the autonomous agent. The desk adds no analysis of its own.
/// </summary>
public partial class BotsDeskViewModel
{
    private string? _rail = "risk";

    public ICommand RailRiskToggle { get; private set; } = null!;
    public ICommand RailCorrToggle { get; private set; } = null!;
    public ICommand RailOptsToggle { get; private set; } = null!;
    public ICommand RailCopilotToggle { get; private set; } = null!;
    public ICommand RailAgentToggle { get; private set; } = null!;

    public ICommand RiskCheckCommand { get; private set; } = null!;
    public ICommand CorrAnalyzeCommand { get; private set; } = null!;
    public ICommand OptsSuggestCommand { get; private set; } = null!;
    public ICommand CopilotAskCommand { get; private set; } = null!;
    public ICommand CopilotToggleAutoCommand { get; private set; } = null!;
    public ICommand AgentToggleLiveCommand { get; private set; } = null!;
    public ICommand AgentArmCommand { get; private set; } = null!;

    public ObservableCollection<string> RiskReasons { get; } = new();
    public ObservableCollection<string> CorrBullets { get; } = new();
    public ObservableCollection<CopilotMsg> CopilotMsgs { get; } = new();
    public ObservableCollection<CopilotChip> CopilotChips { get; } = new();
    public ObservableCollection<AgentCap> AgentCaps { get; } = new();
    public ObservableCollection<LogRow> AgentLog { get; } = new();

    // ── shell view-models behind the rail ───────────────────────────────────
    private PreTradeRiskViewModel? RiskVm => _host?.RiskCheckVM;
    private CorrelationInsightViewModel? CorrVm => _host?.CorrelationInsightVM;
    private OptionsStrategyViewModel? OptsVm => _host?.OptionsStrategyVM;
    private CopilotViewModel? CopilotVm => _host?.CopilotVM;

    public string[] Sides => new[] { "BUY", "SELL" };
    public string[] OptAssets => OptsVm?.AvailableAssets.ToArray() ?? Array.Empty<string>();
    public string[] OptViews => OptsVm?.AvailableDirections.ToArray() ?? Array.Empty<string>();

    private void InitRail()
    {
        RailRiskToggle = new RelayCommand(() => ToggleRail("risk"));
        RailCorrToggle = new RelayCommand(() => ToggleRail("corr"));
        RailOptsToggle = new RelayCommand(() => ToggleRail("opts"));
        RailCopilotToggle = new RelayCommand(() => ToggleRail("copilot"));
        RailAgentToggle = new RelayCommand(() => ToggleRail("agent"));

        RiskCheckCommand = new RelayCommand(RunRiskCheck);
        CorrAnalyzeCommand = new RelayCommand(RunCorr);
        OptsSuggestCommand = new RelayCommand(RunOpts);
        CopilotAskCommand = new RelayCommand(CopilotAsk);
        CopilotToggleAutoCommand = new RelayCommand(CopilotToggleAuto);
        AgentToggleLiveCommand = new RelayCommand(AgentToggleLive);
        AgentArmCommand = new RelayCommand(AgentArm);
    }

    /// <summary>Pulls the rail collections back into sync with their source view-models.</summary>
    private void SyncRail()
    {
        if (_host is null) return;

        // copilot transcript
        if (CopilotVm is { } c && CopilotMsgs.Count != c.Messages.Count)
        {
            CopilotMsgs.Clear();
            foreach (var m in c.Messages)
                CopilotMsgs.Add(new CopilotMsg
                {
                    Role = m.RoleLabel.ToUpperInvariant(),
                    Text = m.Text,
                    Align = m.Align,
                    RoleColor = m.RoleBrush,
                    Bg = m.IsUser ? "#0a1520" : "#08161f"
                });
        }
        if (CopilotVm is { } cc && CopilotChips.Count != cc.Suggestions.Count)
        {
            CopilotChips.Clear();
            foreach (var s in cc.Suggestions)
            {
                var q = s;
                CopilotChips.Add(new CopilotChip
                {
                    Label = s,
                    Command = new RelayCommand(() => Run(cc.AskSuggestionCommand, q))
                });
            }
        }

        // risk reasons
        if (RiskVm is { } r && RiskReasons.Count != r.Reasons.Count)
        {
            RiskReasons.Clear();
            foreach (var x in r.Reasons) RiskReasons.Add(x);
        }

        // correlation bullets
        if (CorrVm is { } cr && CorrBullets.Count != cr.Bullets.Count)
        {
            CorrBullets.Clear();
            foreach (var x in cr.Bullets) CorrBullets.Add(x);
        }

        // autonomous agent log
        var log = AgentVm?.StatusLog ?? "";
        if (log.Length != _agentLogLen)
        {
            _agentLogLen = log.Length;
            AgentLog.Clear();
            foreach (var l in ParseLog(log, 60)) AgentLog.Add(l);
        }

        RaiseRailState();
    }

    private int _agentLogLen = -1;

    private void RaiseRailState()
    {
        foreach (var n in new[]
        {
            nameof(RiskHasVerdict), nameof(RiskVerdict), nameof(RiskVerdictShort), nameof(RiskVerdictColor),
            nameof(RiskVerdictBg), nameof(RiskVerdictBorder), nameof(RiskScore), nameof(RiskRationale),
            nameof(CorrHasResult), nameof(CorrBadge), nameof(CorrBadgeColor), nameof(CorrSummary),
            nameof(OptsHasResult), nameof(OptsBadge), nameof(OptsStrategy), nameof(OptsIv), nameof(OptsIvColor), nameof(OptsRationale),
            nameof(CopilotMode), nameof(CopilotModeColor), nameof(CopilotAutoLabel), nameof(CopilotAutoColor),
            nameof(CopilotAutoBorder), nameof(CopilotAutoBg), nameof(CopilotTrackBg), nameof(CopilotKnob), nameof(CopilotKnobLeft),
            nameof(AgentBadge), nameof(AgentBadgeColor), nameof(AgentLiveLabel), nameof(AgentLiveColor),
            nameof(AgentLiveBorder), nameof(AgentLiveBg), nameof(AgentArmLabel), nameof(AgentArmColor),
            nameof(AgentArmBorder), nameof(AgentArmBg)
        })
            this.RaisePropertyChanged(n);
    }

    private static void Run(ReactiveCommand<string, System.Reactive.Unit>? cmd, string arg)
    {
        if (cmd is null) return;
        try { cmd.Execute(arg).Subscribe(_ => { }, _ => { }); } catch { }
    }

    private void ToggleRail(string key)
    {
        _rail = _rail == key ? null : key;
        RaiseRail();
    }

    private void RaiseRail()
    {
        foreach (var n in new[]
        {
            nameof(RailRiskOpen), nameof(RailCorrOpen), nameof(RailOptsOpen), nameof(RailCopilotOpen), nameof(RailAgentOpen),
            nameof(RailRiskCaret), nameof(RailCorrCaret), nameof(RailOptsCaret), nameof(RailCopilotCaret), nameof(RailAgentCaret),
            nameof(RailRiskBorder), nameof(RailCorrBorder), nameof(RailOptsBorder), nameof(RailCopilotBorder), nameof(RailAgentBorder),
            nameof(RailRiskTitle), nameof(RailCorrTitle), nameof(RailOptsTitle), nameof(RailCopilotTitle), nameof(RailAgentTitle)
        })
            this.RaisePropertyChanged(n);
    }

    public bool RailRiskOpen => _rail == "risk";
    public bool RailCorrOpen => _rail == "corr";
    public bool RailOptsOpen => _rail == "opts";
    public bool RailCopilotOpen => _rail == "copilot";
    public bool RailAgentOpen => _rail == "agent";
    public string RailRiskCaret => RailRiskOpen ? "▾" : "▸";
    public string RailCorrCaret => RailCorrOpen ? "▾" : "▸";
    public string RailOptsCaret => RailOptsOpen ? "▾" : "▸";
    public string RailCopilotCaret => RailCopilotOpen ? "▾" : "▸";
    public string RailAgentCaret => RailAgentOpen ? "▾" : "▸";
    public string RailRiskBorder => RailRiskOpen ? SemanticColor.Stroke : "#0d1b27";
    public string RailCorrBorder => RailCorrOpen ? SemanticColor.Stroke : "#0d1b27";
    public string RailOptsBorder => RailOptsOpen ? SemanticColor.Stroke : "#0d1b27";
    public string RailCopilotBorder => RailCopilotOpen ? SemanticColor.Stroke : "#0d1b27";
    public string RailAgentBorder => RailAgentOpen ? SemanticColor.Stroke : "#0d1b27";
    public string RailRiskTitle => RailRiskOpen ? BotsDeskData.Accent : "#5a7a94";
    public string RailCorrTitle => RailCorrOpen ? BotsDeskData.Accent : "#5a7a94";
    public string RailOptsTitle => RailOptsOpen ? BotsDeskData.Accent : "#5a7a94";
    public string RailCopilotTitle => RailCopilotOpen ? BotsDeskData.Accent : "#5a7a94";
    public string RailAgentTitle => RailAgentOpen ? BotsDeskData.Accent : "#5a7a94";

    // ── pre-trade risk check (PreTradeRiskViewModel) ─────────────────────────
    public string RcSymbol
    {
        get => RiskVm?.Symbol ?? "";
        set { if (RiskVm is { } r) { r.Symbol = (value ?? "").ToUpperInvariant(); this.RaisePropertyChanged(); } }
    }

    public string RcSide
    {
        get => (RiskVm?.Side ?? "buy").ToUpperInvariant();
        set { if (RiskVm is { } r) { r.Side = (value ?? "BUY").ToLowerInvariant(); this.RaisePropertyChanged(); } }
    }

    public string RcUsd
    {
        get => RiskVm is { } r ? r.OrderUsd.ToString("0.##", Inv) : "";
        set { if (RiskVm is { } r) { r.OrderUsd = Dec(value, r.OrderUsd); this.RaisePropertyChanged(); } }
    }

    public string RcLev
    {
        get => RiskVm is { } r ? r.Leverage.ToString(Inv) : "";
        set { if (RiskVm is { } r) { r.Leverage = Int(value, r.Leverage); this.RaisePropertyChanged(); } }
    }

    public bool RiskHasVerdict => RiskVm?.HasVerdict ?? false;
    public string RiskVerdict => RiskVm is { HasVerdict: true } r ? r.Verdict.ToUpperInvariant() : BotsDeskData.Dash;
    public string RiskVerdictShort => RiskVm is null ? BotsDeskData.Dash
        : RiskVm.Running ? "checking…"
        : RiskVm.HasVerdict ? RiskVm.Verdict.ToUpperInvariant() : "idle";
    public string RiskVerdictColor => RiskVm is { HasVerdict: true } r ? r.VerdictBrush : BotsDeskData.Faint;
    // Подложка и рамка плашки берутся из общей палитры вердиктов.
    //
    // Здесь стоял свой список из ALLOW/CAUTION/REJECT, а сервис возвращает APPROVE/CAUTION/BLOCK
    // (PreTradeRiskService.Vocabulary). Совпадало только CAUTION — то есть «разрешено» и
    // «заблокировано», два исхода, ради которых проверку и запускают, выходили одинаково серыми,
    // и отличить их можно было только по цвету самого слова.
    public string RiskVerdictBg => RiskVm is { HasVerdict: true } r
        ? AiVerdictPalette.TintOf(r.Verdict)
        : "transparent";

    public string RiskVerdictBorder => RiskVm is { HasVerdict: true } r
        ? AiVerdictPalette.StrokeOf(r.Verdict)
        : SemanticColor.Stroke;
    public string RiskScore => RiskVm is { HasVerdict: true } r ? r.ScoreLabel : BotsDeskData.Dash;
    public string RiskRationale => RiskVm is { HasVerdict: true } r ? r.Rationale : "";

    private void RunRiskCheck()
    {
        if (RiskVm is null) { Toast("Pre-trade risk service is not available", "warn"); return; }
        Run(RiskVm.CheckCommand);
        RaiseRailState();
        Toast("Pre-trade check requested", "info");
    }

    // ── correlation insight (CorrelationInsightViewModel) ────────────────────
    public string CorrSymbols
    {
        get => CorrVm?.Symbols ?? "";
        set { if (CorrVm is { } c) { c.Symbols = value ?? ""; this.RaisePropertyChanged(); } }
    }
    public bool CorrHasResult => CorrVm?.HasSignal ?? false;
    public string CorrBadge => CorrVm is null ? BotsDeskData.Dash
        : CorrVm.Running ? "analysing…"
        : CorrVm.HasSignal ? CorrVm.SignalLabel : "idle";
    public string CorrBadgeColor => CorrVm is { HasSignal: true } c ? c.SignalBrush : BotsDeskData.Faint;
    public string CorrSummary => CorrVm is { HasSignal: true } c ? c.Summary : "";

    private void RunCorr()
    {
        if (CorrVm is null) { Toast("Correlation service is not available", "warn"); return; }
        Run(CorrVm.AnalyzeCommand);
        RaiseRailState();
        Toast("Correlation analysis requested", "ai");
    }

    // ── options advisor (OptionsStrategyViewModel) ───────────────────────────
    public string OptAsset
    {
        get => OptsVm?.Asset ?? "";
        set { if (OptsVm is { } o) { o.Asset = value ?? ""; this.RaisePropertyChanged(); } }
    }
    public string OptView
    {
        get => OptsVm?.Direction ?? "";
        set { if (OptsVm is { } o) { o.Direction = value ?? ""; this.RaisePropertyChanged(); } }
    }
    public bool OptsHasResult => OptsVm?.HasResult ?? false;
    public string OptsBadge => OptsVm is null ? BotsDeskData.Dash
        : OptsVm.Running ? "working…"
        : OptsVm.HasResult ? OptsVm.Asset + " · " + OptsVm.Direction : "idle";
    public string OptsStrategy => OptsVm is { HasResult: true } o ? o.Strategy : BotsDeskData.Dash;
    public string OptsIv => OptsVm is { HasResult: true } o ? o.IvRegime : "";
    public string OptsIvColor => OptsVm is { HasResult: true } o ? o.IvRegimeBrush : BotsDeskData.Faint;
    public string OptsRationale => OptsVm is { HasResult: true } o ? o.Rationale : "";

    private void RunOpts()
    {
        if (OptsVm is null) { Toast("Options advisor is not available", "warn"); return; }
        Run(OptsVm.SuggestCommand);
        RaiseRailState();
        Toast("Options strategy requested", "ai");
    }

    // ── copilot (CopilotViewModel) ───────────────────────────────────────────
    public string CopilotQ
    {
        get => CopilotVm?.Question ?? "";
        set { if (CopilotVm is { } c) { c.Question = value ?? ""; this.RaisePropertyChanged(); } }
    }
    public string CopilotMode => CopilotVm is null ? BotsDeskData.Dash
        : CopilotVm.IsAutoMode ? "AUTO ON" : CopilotVm.ModeLabel;
    public string CopilotModeColor => CopilotVm is { IsAutoMode: true } ? BotsDeskData.Amber : BotsDeskData.Faint;
    public string CopilotAutoLabel => CopilotVm is null ? "copilot unavailable"
        : CopilotVm.IsAutoMode ? "AUTO ON · the agent acts within its gates" : "AUTO off · approve each order";
    public string CopilotAutoColor => CopilotVm is { IsAutoMode: true } ? BotsDeskData.Amber : "#5a7a94";
    public string CopilotAutoBorder => CopilotVm is { IsAutoMode: true } ? "#3a2a12" : SemanticColor.Stroke;
    public string CopilotAutoBg => CopilotVm is { IsAutoMode: true } ? "#150f04" : "transparent";
    public string CopilotTrackBg => CopilotVm is { IsAutoMode: true } ? "#3a2a12" : SemanticColor.Stroke;
    public string CopilotKnob => CopilotVm is { IsAutoMode: true } ? BotsDeskData.Amber : "#3d5a72";
    public double CopilotKnobLeft => CopilotVm is { IsAutoMode: true } ? 15 : 2;

    private void CopilotAsk()
    {
        if (CopilotVm is null) { Toast("Copilot is not available", "warn"); return; }
        if (string.IsNullOrWhiteSpace(CopilotVm.Question)) { Toast("Type a question first", "warn"); return; }
        Run(CopilotVm.AskCommand);
        this.RaisePropertyChanged(nameof(CopilotQ));
    }

    private void CopilotToggleAuto()
    {
        if (CopilotVm is not { } c) { Toast("Copilot is not available", "warn"); return; }
        if (!c.CanAct) { Toast("Copilot has no action context — it stays read-only", "warn"); return; }

        if (!c.IsAutoMode)
        {
            AskConfirm(new ConfirmConfig
            {
                Kind = "copilotAuto", Accent = "#3a2a12", IconColor = BotsDeskData.Amber, Icon = "⚠",
                Title = "Enable AUTO mode",
                Body = c.AutoModeWarning,
                Checks = { new ConfirmCheck { Label = "I understand it can act without my approval." } },
                Cta = "ENABLE AUTO", BtnBg = "#150f04", BtnFg = BotsDeskData.Amber, BtnBorder = BotsDeskData.Amber
            });
        }
        else
        {
            c.IsAutoMode = false;
            RaiseRailState();
            Toast("AUTO off — every order needs approval", "info");
        }
    }

    internal void SetCopilotAuto(bool on)
    {
        if (CopilotVm is not { } c) return;
        c.IsAutoMode = on;
        if (on && c.ShowAutoConsent) Run(c.ConfirmAutoModeCommand);
        RaiseRailState();
    }

    // ── autonomous agent (AutonomousAgentViewModel) ──────────────────────────
    public string AgentObjective
    {
        get => AgentVm?.Objective ?? "";
        set { if (AgentVm is { } a) { a.Objective = value ?? ""; this.RaisePropertyChanged(); } }
    }

    private void RebuildAgentCaps()
    {
        AgentCaps.Clear();
        if (AgentVm is not { } a) return;
        AgentCaps.Add(new AgentCap { Label = "INTERVAL s", Value = a.IntervalSeconds.ToString(Inv), ValueChanged = v => a.IntervalSeconds = Int(v, a.IntervalSeconds) });
        AgentCaps.Add(new AgentCap { Label = "BUDGET $", Value = a.SessionBudgetUsd.ToString("0", Inv), ValueChanged = v => a.SessionBudgetUsd = Dec(v, a.SessionBudgetUsd) });
        AgentCaps.Add(new AgentCap { Label = "MAX TRADES", Value = a.MaxTrades.ToString(Inv), ValueChanged = v => a.MaxTrades = Int(v, a.MaxTrades) });
    }

    public string AgentBadge => AgentVm?.ModeBadge ?? BotsDeskData.Dash;
    public string AgentBadgeColor => AgentVm is null ? BotsDeskData.Faint
        : AgentVm.LiveEnabled ? BotsDeskData.Amber
        : AgentVm.IsRunning ? BotsDeskData.Green : BotsDeskData.Faint;
    public string AgentLiveLabel => AgentVm is { LiveEnabled: true } ? "LIVE · REAL FUNDS" : "PAPER";
    public string AgentLiveColor => AgentVm is { LiveEnabled: true } ? BotsDeskData.Amber : BotsDeskData.Accent;
    public string AgentLiveBorder => AgentVm is { LiveEnabled: true } ? "#3a2a12" : "#14302e";
    public string AgentLiveBg => AgentVm is { LiveEnabled: true } ? "#150f04" : "#061615";
    public string AgentArmLabel => AgentVm is { IsRunning: true } ? "STOP (KILL)" : "ARM AGENT";
    public string AgentArmColor => AgentVm is { IsRunning: true } ? BotsDeskData.Red : BotsDeskData.Accent;
    public string AgentArmBorder => AgentVm is { IsRunning: true } ? "#3a1620" : "#14302e";
    public string AgentArmBg => AgentVm is { IsRunning: true } ? "#14060a" : "#061615";

    private void AgentToggleLive()
    {
        if (AgentVm is not { } a) { Toast("Autonomous agent is not available", "warn"); return; }
        if (a.LiveEnabled)
        {
            a.LiveEnabled = false;
            RaiseRailState();
            AfterEngineAction(IdAgent);
            Toast("Agent moved back to PAPER", "info");
            return;
        }

        AskConfirm(new ConfirmConfig
        {
            Kind = "agentLive", Accent = "#3a2a12", IconColor = BotsDeskData.Amber, Icon = "⚠",
            Title = "Enable LIVE autonomous trading",
            Body = "The agent will place REAL orders up to $" + a.SessionBudgetUsd.ToString("0.##", Inv) +
                   " and " + a.MaxTrades + " trades per session. The wallet, risk and testnet gates still apply.",
            Checks = { new ConfirmCheck { Label = "I understand it trades without asking each time." } },
            HasType = true, TypeWord = "LIVE", Cta = "ENABLE LIVE", BtnBg = "#150f04", BtnFg = BotsDeskData.Amber, BtnBorder = BotsDeskData.Amber
        });
    }

    internal void SetAgentLive(bool on)
    {
        if (AgentVm is not { } a) return;
        a.LiveEnabled = on;
        RaiseRailState();
        AfterEngineAction(IdAgent);
    }

    private void AgentArm()
    {
        if (AgentVm is not { } a) { Toast("Autonomous agent is not available", "warn"); return; }
        if (a.IsRunning)
        {
            Run(a.StopCommand);
            AfterEngineAction(IdAgent);
            Toast("Agent stopped by kill-switch", "bad");
        }
        else
        {
            Run(a.ArmCommand);
            AfterEngineAction(IdAgent);

            // the agent asks for its own LIVE consent before the first live session
            if (a.ShowLiveConsent)
            {
                AskConfirm(new ConfirmConfig
                {
                    Kind = "agentArmLive", Accent = "#3a2a12", IconColor = BotsDeskData.Amber, Icon = "⚠",
                    Title = "Start a LIVE agent session",
                    Body = "The agent needs an explicit consent for each LIVE session before its first turn runs.",
                    HasType = true, TypeWord = "LIVE", Cta = "START LIVE", BtnBg = "#150f04", BtnFg = BotsDeskData.Amber, BtnBorder = BotsDeskData.Amber
                });
                return;
            }

            Toast(a.IsRunning
                    ? "Agent armed · " + (a.LiveEnabled ? "LIVE" : "PAPER")
                    : "Agent did not start — check the API key in the AI Bot panel",
                a.IsRunning ? (a.LiveEnabled ? "warn" : "ok") : "warn");
        }
        RaiseRailState();
    }
}
