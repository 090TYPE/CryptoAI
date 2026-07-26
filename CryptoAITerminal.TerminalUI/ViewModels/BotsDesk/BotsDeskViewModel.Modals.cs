using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

/// <summary>Configuration the wizard applies to an existing engine before starting it.</summary>
public sealed class WizardResult
{
    public string? Name { get; init; }
    public string? Venue { get; init; }
    public string? Market { get; init; }
    public double? Capital { get; init; }
    public string? Mode { get; init; }
}

/// <summary>Config for the shared confirm dialog.</summary>
public sealed class ConfirmConfig
{
    public string Kind { get; init; } = "";
    public string? BotId { get; init; }
    public string Accent { get; init; } = SemanticColor.Stroke;
    public string Icon { get; init; } = "?";
    public string IconColor { get; init; } = SemanticColor.Accent;
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public List<ConfirmCheck> Checks { get; } = new();
    public bool HasType { get; init; }
    public string TypeWord { get; init; } = "";
    public string Cta { get; init; } = "CONFIRM";
    public string BtnBg { get; init; } = "#0e2a2a";
    public string BtnFg { get; init; } = SemanticColor.Accent;
    public string BtnBorder { get; init; } = SemanticColor.Accent;
    public WizardResult? Opts { get; init; }
    public string? TemplateId { get; init; }
}

/// <summary>One wizard slider bound straight to an engine property.</summary>
public sealed class WizParamViewModel : ReactiveObject
{
    public BotsDeskViewModel Desk { get; set; } = null!;
    public int Idx { get; init; }
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;

    /// <summary>Writes the value onto the engine view-model.</summary>
    public Action<double>? Apply { get; init; }

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            this.RaiseAndSetIfChanged(ref _value, value);
            this.RaisePropertyChanged(nameof(Display));
            try { Apply?.Invoke(value); } catch { /* engine setters clamp */ }
            Desk?.OnWizParam(Idx, value);
        }
    }
    public string Display => Trim(_value) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit);
    private static string Trim(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
}

public partial class BotsDeskViewModel
{
    private static readonly CultureInfo InvM = CultureInfo.InvariantCulture;

    // ── modal routing ────────────────────────────────────────────────────────
    private string? _modal;
    public bool ModalColumns => _modal == "columns";
    public bool ModalWizard => _modal == "wizard";
    public bool ModalConfirm => _modal == "confirm";
    public bool ModalPanel => _modal == "panel";
    public bool AnyModal => _modal != null;

    private void RaiseModalFlags()
    {
        foreach (var n in new[] { nameof(ModalColumns), nameof(ModalWizard), nameof(ModalConfirm), nameof(ModalPanel), nameof(AnyModal) })
            this.RaisePropertyChanged(n);
    }

    private void CloseModal()
    {
        _modal = null;
        CloseMenus();
        RaiseModalFlags();
    }

    // ── collections ──────────────────────────────────────────────────────────
    public ObservableCollection<ColumnToggle> ColumnToggles { get; } = new();
    public ObservableCollection<ActionToggle> ActionToggles { get; } = new();
    public ObservableCollection<WizardStep> WizardSteps { get; } = new();
    public ObservableCollection<WizParamViewModel> WizParams { get; } = new();
    public ObservableCollection<KvRow> WizProjection { get; } = new();
    public ObservableCollection<WizardMode> WizModes { get; } = new();
    public ObservableCollection<GuardToggle> WizGuards { get; } = new();
    public ObservableCollection<KvRow> WizReview { get; } = new();
    public ObservableCollection<ConfirmCheck> ConfirmChecks { get; } = new();
    public ObservableCollection<KvRow> PanelRows { get; } = new();

    private void InitModals()
    {
        ConfirmRunCommand = new RelayCommand(RunConfirm);
        WizBackCommand = new RelayCommand(WizBack);
        WizNextCommand = new RelayCommand(WizNext);
        WizBacktestCommand = new RelayCommand(() => Toast("No backtest runner is wired to this desk — use the Backtest tab", "warn"));
        WizAiTuneCommand = new RelayCommand(WizAiTune);
    }

    // ═══════════════════════ columns modal ═════════════════════════════════
    private void OpenColumns()
    {
        _modal = "columns";
        CloseMenus();
        RebuildColumnToggles();
        RaiseModalFlags();
    }

    private void RebuildColumnToggles()
    {
        ColumnToggles.Clear();
        foreach (var (key, label) in BotsDeskData.ColumnDefs)
        {
            var on = _cols[key]; var k = key;
            ColumnToggles.Add(new ColumnToggle
            {
                Label = label, Mark = on ? "✓" : "",
                BoxBorder = on ? BotsDeskData.Accent : SemanticColor.Stroke, BoxBg = on ? BotsDeskData.Accent : "transparent",
                Border = on ? SemanticColor.Stroke : "#0d1b27", Bg = on ? "#08131d" : "transparent", Fg = on ? BotsDeskData.Text : BotsDeskData.Faint,
                Command = new RelayCommand(() => { _cols[k] = !_cols[k]; RaiseColFlags(); RebuildColumnToggles(); })
            });
        }
        ActionToggles.Clear();
        foreach (var (key, label, icon, color) in BotsDeskData.ActionDefs)
        {
            var on = _quick.Contains(key); var k = key;
            ActionToggles.Add(new ActionToggle
            {
                Label = label, Icon = icon, IcColor = on ? color : "#1e3048",
                Border = on ? SemanticColor.Stroke : "#0d1b27", Bg = on ? "#08131d" : "transparent", Fg = on ? BotsDeskData.Text : BotsDeskData.Faint,
                Command = new RelayCommand(() => ToggleQuickAction(k))
            });
        }
    }

    private void ToggleQuickAction(string key)
    {
        var on = _quick.Contains(key);
        if (on) _quick.Remove(key);
        else
        {
            _quick.Add(key);
            if (_quick.Count > 4) _quick = _quick.Skip(_quick.Count - 4).ToList();
        }
        foreach (var b in Bots) BuildRowActionsAndMenu(b);
        RebuildColumnToggles();
        if (!on && _quick.Count >= 4) Toast("Row shows 4 actions — the oldest moved to the ⋯ menu", "info");
    }

    private void RaiseColFlags()
    {
        foreach (var n in new[] { nameof(ColVenue), nameof(ColMode), nameof(ColStatus), nameof(ColPnl24), nameof(ColPnlTotal),
            nameof(ColSpark), nameof(ColTrades), nameof(ColAlloc), nameof(ColDd), nameof(ColUptime) })
            this.RaisePropertyChanged(n);
    }

    private void ResetLayout()
    {
        _cols["venue"] = true; _cols["mode"] = true; _cols["status"] = true; _cols["pnl24"] = true; _cols["pnlTotal"] = true;
        _cols["spark"] = true; _cols["trades"] = true; _cols["alloc"] = true; _cols["dd"] = true; _cols["uptime"] = true;
        _quick = new List<string> { "start", "pause", "stop", "logs" };
        RaiseColFlags();
        foreach (var b in Bots) BuildRowActionsAndMenu(b);
        RebuildColumnToggles();
        Toast("Table layout reset", "info");
    }

    // ═══════════════════════ wizard ════════════════════════════════════════
    // The engines are singletons: the wizard configures and starts an existing
    // engine, it never creates a new bot instance.
    private int _wizStep = 1;
    private string? _wizTemplate;
    private string _wizName = "";
    private string _wizVenue = "";
    private string _wizMarket = "";
    private string _wizCapital = "";
    private string _wizMode = "paper";

    public ICommand WizBackCommand { get; private set; } = null!;
    public ICommand WizNextCommand { get; private set; } = null!;
    public ICommand WizBacktestCommand { get; private set; } = null!;
    public ICommand WizAiTuneCommand { get; private set; } = null!;
    public ICommand ConfirmRunCommand { get; private set; } = null!;

    /// <summary>Venues the selected engine actually supports.</summary>
    public string[] WizVenues => CurrentTpl().id switch
    {
        IdGrid => Grid?.AvailableMarketModes.Select(m => "Binance " + m).ToArray() ?? Array.Empty<string>(),
        IdDca => Dca?.AvailableExchanges.Select(e => e + " Spot").ToArray() ?? Array.Empty<string>(),
        IdRule => Rule?.AvailableExchanges.ToArray() ?? Array.Empty<string>(),
        IdAi => Trader?.AvailableExchanges.ToArray() ?? Array.Empty<string>(),
        _ => Array.Empty<string>()
    };

    /// <summary>Symbols already configured on the engines — the desk invents none.</summary>
    public string[] WizMarkets
    {
        get
        {
            var list = new List<string>();
            if (Grid is { } g && !string.IsNullOrWhiteSpace(g.Symbol)) list.Add(g.Symbol);
            if (Rule is { } r && !string.IsNullOrWhiteSpace(r.Symbol)) list.Add(r.Symbol);
            if (Dca is { } d) list.AddRange(d.Coins.Select(c => c.Symbol));
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public bool WizStep1 => _wizStep == 1;
    public bool WizStep2 => _wizStep == 2;
    public bool WizStep3 => _wizStep == 3;

    public string WizName { get => _wizName; set { this.RaiseAndSetIfChanged(ref _wizName, value ?? ""); } }

    // the combos push null back when the seeded value is not in their list — ignore that
    public string WizVenue
    {
        get => _wizVenue;
        set { if (string.IsNullOrEmpty(value)) return; this.RaiseAndSetIfChanged(ref _wizVenue, value); RebuildWizardReview(); }
    }
    public string WizMarket
    {
        get => _wizMarket;
        set { if (string.IsNullOrEmpty(value)) return; this.RaiseAndSetIfChanged(ref _wizMarket, value); RebuildWizardReview(); }
    }
    public string WizCapital { get => _wizCapital; set { this.RaiseAndSetIfChanged(ref _wizCapital, BotsDeskData.Decimalish(value)); RebuildWizardReview(); } }

    public string WizParamsTitle => CurrentTpl().type + " PARAMETERS";

    /// <summary>Only shows the engine's own AI note — the desk has no config critic.</summary>
    public string WizAiNote => CurrentTpl().id switch
    {
        IdGrid => Grid?.AiParamsRationale ?? "",
        IdRule => Rule?.TpSlSuggestNote ?? "",
        _ => ""
    };

    public bool WizLiveWarning => _wizMode == "live";
    public string WizBackLabel => _wizStep == 1 ? "CANCEL" : "← BACK";
    public string WizNextLabel => _wizStep == 3
        ? (_wizMode == "live" ? "REVIEW & START LIVE" : "CONFIGURE & START")
        : "CONTINUE →";
    public string WizHint => _wizStep == 1 ? "pick an engine"
        : _wizStep == 2 ? CurrentTpl().type + (string.IsNullOrEmpty(_wizMarket) ? "" : " · " + _wizMarket)
        : _wizMode == "live" ? "live start needs typed consent" : "the engine starts in its current mode";

    private (string id, string type, string name, string desc) CurrentTpl()
    {
        var t = BotsDeskData.Templates.FirstOrDefault(x => x.id == _wizTemplate);
        return t.id is null ? BotsDeskData.Templates[0] : t;
    }

    private void OpenWizard()
    {
        if (!IsAttached) { Toast("Desk is not attached to the trading shell", "warn"); return; }
        _modal = "wizard"; _wizStep = 1; _wizTemplate = null;
        CloseMenus();
        RebuildTemplates();
        RebuildWizard();
        RaiseModalFlags();
    }

    /// <summary>Opens the wizard straight on an engine (the engine cards in the empty state).</summary>
    private void OpenWizardFor(string engineId)
    {
        if (!IsAttached) { Toast("Desk is not attached to the trading shell", "warn"); return; }
        _modal = "wizard";
        _wizTemplate = engineId;
        _wizStep = 2;
        _wizName = BotsDeskData.Templates.FirstOrDefault(t => t.id == engineId).name ?? "";
        SeedWizardFromEngine(engineId);
        CloseMenus();
        RebuildTemplates();
        RebuildWizard();
        RaiseModalFlags();
    }

    /// <summary>Fills the wizard fields from the engine's current configuration.</summary>
    private void SeedWizardFromEngine(string engineId)
    {
        switch (engineId)
        {
            case IdGrid when Grid is { } g:
                _wizVenue = "Binance " + g.SelectedMarketMode;
                _wizMarket = g.Symbol;
                _wizCapital = "";
                _wizMode = "live";
                break;
            case IdDca when Dca is { } d:
                _wizVenue = d.SelectedExchange + " Spot";
                _wizMarket = d.Coins.FirstOrDefault()?.Symbol ?? "";
                _wizCapital = d.TotalBudget.ToString("0", InvM);
                _wizMode = "live";
                break;
            case IdRule when Rule is { } r:
                _wizVenue = r.SelectedExchange;
                _wizMarket = r.Symbol;
                _wizCapital = r.MaxRiskPerTrade.ToString("0", InvM);
                _wizMode = "live";
                break;
            case IdAi when Trader is { } a:
                _wizVenue = a.SelectedExchange;
                _wizMarket = "";
                _wizCapital = a.MaxTotalExposureUsd.ToString("0", InvM);
                _wizMode = a.LiveEnabled ? "live" : "paper";
                break;
            case IdAgent when AgentVm is { } ag:
                _wizVenue = "";
                _wizMarket = "";
                _wizCapital = ag.SessionBudgetUsd.ToString("0", InvM);
                _wizMode = ag.LiveEnabled ? "live" : "paper";
                break;
            case IdTrail when Trail is { } t:
                _wizVenue = "";
                _wizMarket = "";
                _wizCapital = "";
                _wizMode = "live";
                break;
        }
    }

    public void OnWizParam(int idx, double value)
    {
        this.RaisePropertyChanged(nameof(WizAiNote));
        RebuildWizardReview();
    }

    private void WizAiTune()
    {
        switch (CurrentTpl().id)
        {
            case IdGrid when Grid is { } g:
                Run(g.SuggestParamsCommand);
                RebuildWizardParams();
                Toast("BotParameterAi is sizing the grid from live book data", "ai");
                break;
            case IdRule when Rule is { } r:
                Run(r.SuggestTpSlCommand);
                Toast("DynamicTpSlAi requested", "ai");
                break;
            default:
                Toast("No AI parameter service exists for this engine", "warn");
                break;
        }
    }

    private void RebuildWizard()
    {
        RebuildWizardSteps();
        RebuildWizardParams();
        RebuildProjection();
        RebuildWizardModes();
        RebuildWizardGuards();
        RebuildWizardReview();
        foreach (var n in new[] { nameof(WizStep1), nameof(WizStep2), nameof(WizStep3), nameof(WizParamsTitle), nameof(WizAiNote),
            nameof(WizLiveWarning), nameof(WizBackLabel), nameof(WizNextLabel), nameof(WizHint),
            nameof(WizName), nameof(WizVenue), nameof(WizMarket), nameof(WizCapital), nameof(WizVenues), nameof(WizMarkets) })
            this.RaisePropertyChanged(n);
    }

    private void RebuildWizardSteps()
    {
        WizardSteps.Clear();
        var labels = new[] { ("1", "Engine"), ("2", "Configure"), ("3", "Risk & start") };
        for (int i = 0; i < labels.Length; i++)
        {
            var step = i + 1; var active = _wizStep == step;
            WizardSteps.Add(new WizardStep
            {
                N = labels[i].Item1, Label = labels[i].Item2,
                Border = active ? "#14302e" : "#0d1b27", Bg = active ? "#061615" : "transparent",
                Fg = active ? BotsDeskData.Accent : _wizStep > step ? BotsDeskData.Text3 : BotsDeskData.Faint,
                Command = new RelayCommand(() => { if (step == 1 || _wizTemplate != null) { _wizStep = step; RebuildWizard(); } })
            });
        }
    }

    /// <summary>Sliders bound to the selected engine's own numeric settings.</summary>
    private void RebuildWizardParams()
    {
        WizParams.Clear();
        int i = 0;
        void S(string label, string unit, double min, double max, double step, double value, Action<double> apply)
            => WizParams.Add(new WizParamViewModel
            {
                Desk = this, Idx = i++, Label = label, Unit = unit, Min = min, Max = max, Step = step,
                Value = Math.Clamp(value, min, max), Apply = apply
            });

        switch (CurrentTpl().id)
        {
            case IdGrid when Grid is { } g:
                S("Grid levels", "", 2, 100, 1, g.GridLevels, v => g.GridLevels = (int)v);
                S("Leverage", "x", 1, 25, 1, g.Leverage, v => g.Leverage = (int)v);
                break;
            case IdDca when Dca is { } d:
                S("Budget per cycle", "USDT", 10, 10000, 10, (double)d.TotalBudget, v => d.TotalBudget = (decimal)v);
                S("Interval", "", 1, 30, 1, d.IntervalValue, v => d.IntervalValue = (int)v);
                break;
            case IdRule when Rule is { } r:
                S("Max risk per trade", "$", 10, 5000, 10, (double)r.MaxRiskPerTrade, v => r.MaxRiskPerTrade = (decimal)v);
                S("Leverage", "x", 1, 25, 1, r.FuturesLeverage, v => r.FuturesLeverage = (int)v);
                break;
            case IdAi when Trader is { } a:
                S("Max order", "$", 1, 5000, 1, (double)a.MaxOrderUsd, v => a.MaxOrderUsd = (decimal)v);
                S("Max exposure", "$", 1, 50000, 10, (double)a.MaxTotalExposureUsd, v => a.MaxTotalExposureUsd = (decimal)v);
                S("Max positions", "", 1, 20, 1, a.MaxOpenPositions, v => a.MaxOpenPositions = (int)v);
                S("Max daily loss", "$", 1, 5000, 10, (double)a.MaxDailyLossUsd, v => a.MaxDailyLossUsd = (decimal)v);
                break;
            case IdAgent when AgentVm is { } ag:
                S("Session budget", "$", 0, 50000, 50, (double)ag.SessionBudgetUsd, v => ag.SessionBudgetUsd = (decimal)v);
                S("Max trades", "", 0, 100, 1, ag.MaxTrades, v => ag.MaxTrades = (int)v);
                S("Turn interval", "s", 5, 3600, 5, ag.IntervalSeconds, v => ag.IntervalSeconds = (int)v);
                break;
            case IdTrail when Trail is { } t:
                S("Trail distance", "%", 0.1, 20, 0.1, (double)t.PctDistance, v => t.PctDistance = (decimal)v);
                S("ATR multiplier", "x", 0.5, 10, 0.1, (double)t.AtrMultiplier, v => t.AtrMultiplier = (decimal)v);
                S("ATR period", "", 2, 60, 1, t.AtrPeriod, v => t.AtrPeriod = (int)v);
                break;
        }
    }

    /// <summary>No walk-forward archive exists per engine — the projection stays empty.</summary>
    private void RebuildProjection() => WizProjection.Clear();

    private void RebuildWizardModes()
    {
        WizModes.Clear();
        var supportsPaper = CurrentTpl().id is IdAi or IdAgent;
        WizModes.Add(new WizardMode
        {
            Label = "PAPER",
            Hint = supportsPaper
                ? "Simulated fills on live market data. The engine's own paper mode."
                : "This engine has no paper mode — the wallet guard is the only paper gate.",
            Border = _wizMode == "paper" ? "#14302e" : SemanticColor.Stroke, Bg = _wizMode == "paper" ? "#061615" : "transparent",
            Fg = _wizMode == "paper" ? BotsDeskData.Accent : BotsDeskData.Faint,
            Command = new RelayCommand(() => { _wizMode = "paper"; RebuildWizard(); })
        });
        WizModes.Add(new WizardMode
        {
            Label = "LIVE",
            Hint = "Real orders through the exchange gateway, subject to the wallet risk caps.",
            Border = _wizMode == "live" ? "#3a2a12" : SemanticColor.Stroke, Bg = _wizMode == "live" ? "#150f04" : "transparent",
            Fg = _wizMode == "live" ? BotsDeskData.Amber : BotsDeskData.Faint,
            Command = new RelayCommand(() => { _wizMode = "live"; RebuildWizard(); })
        });
    }

    private void RebuildWizardGuards()
    {
        WizGuards.Clear();
        foreach (var g in GuardsRisk) WizGuards.Add(g);
    }

    private void RebuildWizardReview()
    {
        WizReview.Clear();
        var tpl = CurrentTpl();
        void R(string l, string v, string col) => WizReview.Add(new KvRow { Label = l, Value = string.IsNullOrEmpty(v) ? BotsDeskData.Dash : v, Color = col });
        R("Engine", tpl.type + " · " + tpl.name, BotsDeskData.Text);
        R("Venue", _wizVenue, BotsDeskData.Text);
        R("Market", _wizMarket, BotsDeskData.Text);
        R("Budget", string.IsNullOrEmpty(_wizCapital) ? "" : "$" + _wizCapital, BotsDeskData.Text);
        R("Mode", _wizMode.ToUpperInvariant(), _wizMode == "live" ? BotsDeskData.Amber : BotsDeskData.Accent);
        R("Wallet guard", PaperOnly ? "paper-only ON" : "live allowed", PaperOnly ? BotsDeskData.Accent : BotsDeskData.Amber);
        R("Max order", Trader is { } tr ? "$" + tr.MaxOrderUsd.ToString("#,##0", InvM) : "", BotsDeskData.Text3);
        R("Daily loss stop", Wallet is { } wl ? "−$" + wl.GlobalMaxDailyLossUsdt.ToString("#,##0", InvM) : "", BotsDeskData.Red);
    }

    private void WizBack()
    {
        if (_wizStep == 1) CloseModal();
        else { _wizStep--; RebuildWizard(); }
    }

    private void WizNext()
    {
        if (_wizStep == 1)
        {
            _wizTemplate ??= BotsDeskData.Templates[0].id;
            SeedWizardFromEngine(_wizTemplate!);
            _wizStep = 2;
            RebuildTemplates();
            RebuildWizard();
            return;
        }
        if (_wizStep == 2) { _wizStep = 3; RebuildWizard(); return; }

        var tpl = CurrentTpl();
        var opts = new WizardResult
        {
            Name = string.IsNullOrEmpty(_wizName) ? tpl.name : _wizName,
            Venue = _wizVenue,
            Market = _wizMarket,
            Capital = double.TryParse(_wizCapital, NumberStyles.Any, InvM, out var c) ? c : null,
            Mode = _wizMode
        };

        if (_wizMode == "live")
            AskConfirm(new ConfirmConfig
            {
                Kind = "createLive", TemplateId = tpl.id, Opts = opts, Accent = "#3a2a12", IconColor = BotsDeskData.Amber, Icon = "⚠",
                Title = "Start " + tpl.name + " live",
                Body = "The engine will trade real funds"
                       + (string.IsNullOrEmpty(_wizVenue) ? "" : " on " + _wizVenue)
                       + (opts.Capital is { } cap ? " with $" + cap.ToString("#,##0", InvM) : "") + ".",
                HasType = true, TypeWord = "LIVE", Cta = "START LIVE", BtnBg = "#150f04", BtnFg = BotsDeskData.Amber, BtnBorder = BotsDeskData.Amber
            });
        else CreateFromTemplate(tpl.id, opts);
    }

    /// <summary>Writes the wizard fields onto the selected engine's own properties.</summary>
    private void ApplyWizardConfig(string engineId, WizardResult? o)
    {
        if (o is null) return;
        var venue = o.Venue ?? "";
        var market = (o.Market ?? "").Trim().ToUpperInvariant();

        switch (engineId)
        {
            case IdGrid when Grid is { } g:
                if (market.Length > 0) g.Symbol = market;
                if (venue.EndsWith("Futures", StringComparison.OrdinalIgnoreCase)) g.SelectedMarketMode = "Futures";
                else if (venue.EndsWith("Spot", StringComparison.OrdinalIgnoreCase)) g.SelectedMarketMode = "Spot";
                break;

            case IdDca when Dca is { } d:
                var ex = venue.Replace(" Spot", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (ex.Length > 0) d.SelectedExchange = ex;
                if (o.Capital is { } cap) d.TotalBudget = (decimal)cap;
                break;

            case IdRule when Rule is { } r:
                if (market.Length > 0) r.Symbol = market;
                if (venue.Length > 0) r.SelectedExchange = venue;
                if (o.Capital is { } risk) r.MaxRiskPerTrade = (decimal)risk;
                break;

            case IdAi when Trader is { } a:
                if (venue.Length > 0) a.SelectedExchange = venue;
                if (o.Capital is { } exp) a.MaxTotalExposureUsd = (decimal)exp;
                a.LiveEnabled = o.Mode == "live";
                break;

            case IdAgent when AgentVm is { } ag:
                if (o.Capital is { } budget) ag.SessionBudgetUsd = (decimal)budget;
                ag.LiveEnabled = o.Mode == "live";
                break;

            case IdTrail:
                // the trailing engine takes its entry price from the shell when armed
                break;
        }
    }

    // ═══════════════════════ confirm dialog ════════════════════════════════
    private ConfirmConfig _confirm = new();
    private string _confirmTyped = "";

    public string ConfirmAccent => _confirm.Accent;
    public string ConfirmIcon => _confirm.Icon;
    public string ConfirmIconColor => _confirm.IconColor;
    public string ConfirmTitle => _confirm.Title;
    public string ConfirmBody => _confirm.Body;
    public bool ConfirmHasChecks => ConfirmChecks.Count > 0;
    public bool ConfirmHasType => _confirm.HasType;
    public string ConfirmTypeWord => _confirm.TypeWord;
    public string ConfirmCta => _confirm.Cta;
    public string ConfirmTyped { get => _confirmTyped; set { this.RaiseAndSetIfChanged(ref _confirmTyped, value ?? ""); RaiseConfirmBtn(); } }
    private bool ConfirmReady => !_confirm.HasType || string.Equals((_confirmTyped ?? "").Trim(), _confirm.TypeWord, StringComparison.OrdinalIgnoreCase);
    public string ConfirmBtnBg => ConfirmReady ? _confirm.BtnBg : "#0a1520";
    public string ConfirmBtnFg => ConfirmReady ? _confirm.BtnFg : "#3d5a72";
    public string ConfirmBtnBorder => ConfirmReady ? _confirm.BtnBorder : SemanticColor.Stroke;
    public double ConfirmBtnOpacity => ConfirmReady ? 1 : .7;

    private void RaiseConfirmBtn()
    {
        foreach (var n in new[] { nameof(ConfirmBtnBg), nameof(ConfirmBtnFg), nameof(ConfirmBtnBorder), nameof(ConfirmBtnOpacity) })
            this.RaisePropertyChanged(n);
    }

    public void AskConfirm(ConfirmConfig cfg)
    {
        _confirm = cfg;
        _confirmTyped = "";
        ConfirmChecks.Clear();
        foreach (var c in cfg.Checks)
        {
            c.Command = new RelayCommand(() => { c.On = !c.On; c.Refresh(); });
            ConfirmChecks.Add(c);
        }
        _modal = "confirm";
        CloseMenus();
        foreach (var n in new[] { nameof(ConfirmAccent), nameof(ConfirmIcon), nameof(ConfirmIconColor), nameof(ConfirmTitle),
            nameof(ConfirmBody), nameof(ConfirmHasChecks), nameof(ConfirmHasType), nameof(ConfirmTypeWord), nameof(ConfirmCta), nameof(ConfirmTyped) })
            this.RaisePropertyChanged(n);
        RaiseConfirmBtn();
        RaiseModalFlags();
    }

    private void RunConfirm()
    {
        if (!ConfirmReady) { Toast("Type " + _confirm.TypeWord + " to confirm", "warn"); return; }
        switch (_confirm.Kind)
        {
            case "killOne":
                if (_confirm.BotId is { } killId && KillEngine(killId))
                { AfterEngineAction(killId); Toast("Kill-switch fired — engine stopped", "bad"); }
                else Toast("Engine was already stopped", "info");
                break;

            case "killAll":
            {
                var n = Bots.Count(b => CanStop(b.Id) && StopEngine(b.Id));
                AfterEngineAction();
                Toast(n == 0 ? "No engine was running" : n + " engines stopped", n == 0 ? "info" : "bad");
                break;
            }

            case "killSel":
            {
                var n = Bots.Where(b => Selected.Contains(b.Id)).Count(b => CanStop(b.Id) && StopEngine(b.Id));
                Selected.Clear();
                AfterSelectionChanged();
                AfterEngineAction();
                Toast(n == 0 ? "No selected engine was running" : n + " engines stopped", n == 0 ? "info" : "bad");
                break;
            }

            case "goLive":
                if (_confirm.BotId is { } liveId)
                {
                    SetEngineLive(liveId, true);
                    AfterEngineAction(liveId);
                    Toast("Engine switched to LIVE — wallet caps still enforced", "warn");
                }
                break;

            case "arm":
                PaperOnly = false;
                RebuildGuards();
                RefreshAllRows();
                Recompute();
                Toast(PaperOnly ? "Live could not be armed — check the licence gate" : "Live trading armed", PaperOnly ? "bad" : "warn");
                break;

            case "closePos":
                if (_detailPosition is { } pos && _host?.AllPositionsVM is { } ap)
                {
                    try { ap.ClosePositionCommand.Execute(pos).Subscribe(_ => { }, _ => { }); } catch { }
                    Toast("Close order sent for " + pos.Symbol, "info");
                }
                else Toast("Position is no longer open", "info");
                break;

            case "copilotAuto": SetCopilotAuto(true); Toast("AUTO ON — gates still apply", "warn"); break;

            case "agentLive": SetAgentLive(true); Toast("Autonomous agent set to LIVE", "warn"); break;

            case "agentArmLive":
                if (AgentVm is { } av) { Run(av.ConfirmLiveCommand); AfterEngineAction(IdAgent); Toast("Agent running LIVE", "warn"); }
                break;

            case "createLive":
                CreateFromTemplate(_confirm.TemplateId!, _confirm.Opts);
                return;
        }
        CloseModal();
    }

    // ═══════════════════════ side panel / kill-all / daily cap ═════════════
    public string PanelTitle { get; private set; } = "";
    public string PanelSub { get; private set; } = "";
    public string PanelNote { get; private set; } = "";

    private void OpenPanel(string title, string sub, IEnumerable<KvRow> rows, string note)
    {
        PanelTitle = title; PanelSub = sub; PanelNote = note;
        PanelRows.Clear();
        foreach (var r in rows) PanelRows.Add(r);
        _modal = "panel";
        CloseMenus();
        foreach (var n in new[] { nameof(PanelTitle), nameof(PanelSub), nameof(PanelNote) }) this.RaisePropertyChanged(n);
        RaiseModalFlags();
    }

    private void OpenKillAll()
    {
        var running = Running.Count;
        if (running == 0) { Toast("No engine is running", "info"); return; }
        AskConfirm(new ConfirmConfig
        {
            Kind = "killAll", Accent = "#3a1620", IconColor = BotsDeskData.Red, Icon = "⛔",
            Title = "Kill-switch — all engines",
            Body = "Stops " + running + " running engine" + (running == 1 ? "" : "s") + " immediately. Each engine cancels its own open orders on stop.",
            HasType = true, TypeWord = "KILL", Cta = "KILL ALL", BtnBg = "#14060a", BtnFg = BotsDeskData.Red, BtnBorder = BotsDeskData.Red
        });
    }

    private void OpenDailyCap()
    {
        if (!IsAttached) { Toast("Desk is not attached to the trading shell", "warn"); return; }
        var rows = new List<KvRow>
        {
            new() { Label = "Daily loss cap (wallet)", Value = Wallet is { } w1 ? "−$" + w1.GlobalMaxDailyLossUsdt.ToString("#,##0", InvM) : BotsDeskData.Dash, Color = BotsDeskData.Red },
            new() { Label = "Realized loss today", Value = LossToday > 0 ? "−" + Money(LossToday) : Money(0), Color = BotsDeskData.Amber },
            new() { Label = "Used", Value = Wallet is null ? BotsDeskData.Dash : CapUsedPct + "%", Color = BotsDeskData.Amber },
            new() { Label = "Max open exposure (wallet)", Value = Wallet is { } w2 ? "$" + w2.GlobalMaxOpenExposureUsdt.ToString("#,##0", InvM) : BotsDeskData.Dash, Color = BotsDeskData.Text3 },
            new() { Label = "Max order (AI trader)", Value = Trader is { } t1 ? "$" + t1.MaxOrderUsd.ToString("#,##0", InvM) : BotsDeskData.Dash, Color = BotsDeskData.Text3 },
            new() { Label = "Budget on running engines", Value = HdrAlloc, Color = BotsDeskData.Text },
            new() { Label = "Engines running", Value = Running.Count + "/" + Bots.Count, Color = BotsDeskData.Text },
            new() { Label = "Execution guard", Value = PaperOnly ? "paper-only (no broadcast)" : "live allowed", Color = PaperOnly ? BotsDeskData.Accent : BotsDeskData.Amber },
        };
        OpenPanel("Daily cap & exposure", "wallet risk guard", rows,
            "These are the wallet-wide caps every route is checked against. Realized loss is summed from the closed trades in the P&L store.");
    }
}
