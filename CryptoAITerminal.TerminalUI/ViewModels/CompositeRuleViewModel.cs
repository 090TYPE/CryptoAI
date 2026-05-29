using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// Condition-row editor VM
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RuleConditionEditorVM : ReactiveObject
{
    // ── Available types ───────────────────────────────────────────────────────

    public static readonly IReadOnlyList<string> AllTypes =
    [
        "RSI Below", "RSI Above",
        "Price Above MA", "Price Below MA",
        "Volume > SMA×",
        "Position P&L >", "Position P&L <",
        "Funding Rate >"
    ];

    // ── State ─────────────────────────────────────────────────────────────────

    private string  _type   = "RSI Below";
    private string  _symbol = "BTCUSDT";
    private decimal _param1 = 14m;
    private decimal _param2 = 30m;

    public string SelectedType
    {
        get => _type;
        set
        {
            this.RaiseAndSetIfChanged(ref _type, value);
            this.RaisePropertyChanged(nameof(Param1Label));
            this.RaisePropertyChanged(nameof(Param2Label));
            this.RaisePropertyChanged(nameof(IsSymbolVisible));
            this.RaisePropertyChanged(nameof(IsParam2Visible));
            this.RaisePropertyChanged(nameof(Summary));
        }
    }

    public string Symbol
    {
        get => _symbol;
        set { this.RaiseAndSetIfChanged(ref _symbol, value); this.RaisePropertyChanged(nameof(Summary)); }
    }

    public decimal Param1
    {
        get => _param1;
        set { this.RaiseAndSetIfChanged(ref _param1, value); this.RaisePropertyChanged(nameof(Summary)); }
    }

    public decimal Param2
    {
        get => _param2;
        set { this.RaiseAndSetIfChanged(ref _param2, value); this.RaisePropertyChanged(nameof(Summary)); }
    }

    // ── Dynamic labels & visibility ───────────────────────────────────────────

    public string Param1Label => _type switch
    {
        "RSI Below" or "RSI Above"           => "Period",
        "Price Above MA" or "Price Below MA" => "MA Period",
        "Volume > SMA×"                      => "SMA Period",
        "Position P&L >" or "Position P&L <" => "P&L %",
        "Funding Rate >"                      => "Rate %",
        _                                     => "Param 1"
    };

    public string Param2Label => _type switch
    {
        "RSI Below"      => "< Threshold",
        "RSI Above"      => "> Threshold",
        "Price Above MA" => "× Mult",
        "Price Below MA" => "× Mult",
        "Volume > SMA×"  => "× Mult",
        _                => ""
    };

    public bool IsSymbolVisible =>
        _type is not ("Position P&L >" or "Position P&L <");

    public bool IsParam2Visible =>
        _type is "RSI Below" or "RSI Above" or "Price Above MA" or "Price Below MA" or "Volume > SMA×";

    // ── Human-readable summary ────────────────────────────────────────────────

    public string Summary => _type switch
    {
        "RSI Below"      => $"RSI({_symbol},{_param1:0}) < {_param2:0}",
        "RSI Above"      => $"RSI({_symbol},{_param1:0}) > {_param2:0}",
        "Price Above MA" => $"Price({_symbol}) > MA({_param1:0}) × {_param2:0.##}",
        "Price Below MA" => $"Price({_symbol}) < MA({_param1:0}) × {_param2:0.##}",
        "Volume > SMA×"  => $"Volume({_symbol}) > VolSMA({_param1:0}) × {_param2:0.##}",
        "Position P&L >" => $"Position PnL > {_param1:0.##}%",
        "Position P&L <" => $"Position PnL < {_param1:0.##}%",
        "Funding Rate >"  => $"Funding({_symbol}) > {_param1:0.###}%",
        _                 => _type
    };

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    // ── Ctor ──────────────────────────────────────────────────────────────────

    public RuleConditionEditorVM(Action<RuleConditionEditorVM> onRemove)
    {
        RemoveCommand = ReactiveCommand.Create(() => onRemove(this));
    }

    // ── Conversion to / from domain model ────────────────────────────────────

    public RuleCondition ToModel() => new()
    {
        Type   = LabelToType(_type),
        Symbol = IsSymbolVisible ? _symbol : "ANY",
        Param1 = _param1,
        Param2 = IsParam2Visible ? _param2 : 0m
    };

    public static RuleConditionEditorVM FromModel(RuleCondition c, Action<RuleConditionEditorVM> onRemove)
    {
        var vm = new RuleConditionEditorVM(onRemove)
        {
            _symbol = c.Symbol,
            _param1 = c.Param1,
            _param2 = c.Param2,
            _type   = TypeToLabel(c.Type)
        };
        return vm;
    }

    private static ConditionType LabelToType(string label) => label switch
    {
        "RSI Below"       => ConditionType.RsiBelow,
        "RSI Above"       => ConditionType.RsiAbove,
        "Price Above MA"  => ConditionType.PriceAboveMa,
        "Price Below MA"  => ConditionType.PriceBelowMa,
        "Volume > SMA×"   => ConditionType.VolumeAboveSma,
        "Position P&L >"  => ConditionType.OpenPositionPnlAbove,
        "Position P&L <"  => ConditionType.OpenPositionPnlBelow,
        "Funding Rate >"  => ConditionType.FundingRateAbove,
        _                 => ConditionType.RsiBelow
    };

    private static string TypeToLabel(ConditionType type) => type switch
    {
        ConditionType.RsiBelow             => "RSI Below",
        ConditionType.RsiAbove             => "RSI Above",
        ConditionType.PriceAboveMa         => "Price Above MA",
        ConditionType.PriceBelowMa         => "Price Below MA",
        ConditionType.VolumeAboveSma       => "Volume > SMA×",
        ConditionType.OpenPositionPnlAbove => "Position P&L >",
        ConditionType.OpenPositionPnlBelow => "Position P&L <",
        ConditionType.FundingRateAbove     => "Funding Rate >",
        _                                  => "RSI Below"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Action-row editor VM
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RuleActionEditorVM : ReactiveObject
{
    public static readonly IReadOnlyList<string> AllTypes =
    [
        "DCA Buy",
        "Move Stop to Breakeven",
        "Start Funding Arb",
        "Pause Grid Bot",
        "Resume Grid Bot",
        "Close All Positions",
        "Send Notification"
    ];

    private string  _type    = "DCA Buy";
    private string  _symbol  = "BTCUSDT";
    private decimal _amount  = 50m;
    private string  _message = string.Empty;

    public string SelectedType
    {
        get => _type;
        set
        {
            this.RaiseAndSetIfChanged(ref _type, value);
            this.RaisePropertyChanged(nameof(IsSymbolVisible));
            this.RaisePropertyChanged(nameof(IsAmountVisible));
            this.RaisePropertyChanged(nameof(IsMessageVisible));
            this.RaisePropertyChanged(nameof(Summary));
        }
    }

    public string Symbol
    {
        get => _symbol;
        set { this.RaiseAndSetIfChanged(ref _symbol, value); this.RaisePropertyChanged(nameof(Summary)); }
    }

    public decimal Amount
    {
        get => _amount;
        set { this.RaiseAndSetIfChanged(ref _amount, value); this.RaisePropertyChanged(nameof(Summary)); }
    }

    public string Message
    {
        get => _message;
        set { this.RaiseAndSetIfChanged(ref _message, value); this.RaisePropertyChanged(nameof(Summary)); }
    }

    public bool IsSymbolVisible  => _type is not "Send Notification";
    public bool IsAmountVisible  => _type == "DCA Buy";
    public bool IsMessageVisible => _type == "Send Notification";

    public string Summary => _type switch
    {
        "DCA Buy"                 => $"DCA Buy {_symbol} ${_amount:0.##}",
        "Move Stop to Breakeven"  => $"Move Stop → BE ({_symbol})",
        "Start Funding Arb"       => $"Funding Arb ({_symbol})",
        "Pause Grid Bot"          => $"Pause Grid ({_symbol})",
        "Resume Grid Bot"         => $"Resume Grid ({_symbol})",
        "Close All Positions"     => $"Close All ({_symbol})",
        "Send Notification"       => $"Notify: {(string.IsNullOrWhiteSpace(_message) ? "(empty)" : _message)}",
        _                         => _type
    };

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public RuleActionEditorVM(Action<RuleActionEditorVM> onRemove)
    {
        RemoveCommand = ReactiveCommand.Create(() => onRemove(this));
    }

    public RuleAction ToModel() => new()
    {
        Type    = LabelToType(_type),
        Symbol  = IsSymbolVisible ? _symbol : string.Empty,
        Amount  = _amount,
        Message = _message
    };

    public static RuleActionEditorVM FromModel(RuleAction a, Action<RuleActionEditorVM> onRemove)
    {
        var vm = new RuleActionEditorVM(onRemove)
        {
            _symbol  = a.Symbol,
            _amount  = a.Amount,
            _message = a.Message,
            _type    = TypeToLabel(a.Type)
        };
        return vm;
    }

    private static ActionType LabelToType(string label) => label switch
    {
        "DCA Buy"                => ActionType.StartDcaBuy,
        "Move Stop to Breakeven" => ActionType.MoveStopToBreakeven,
        "Start Funding Arb"      => ActionType.StartFundingArb,
        "Pause Grid Bot"         => ActionType.PauseGridBot,
        "Resume Grid Bot"        => ActionType.ResumeGridBot,
        "Close All Positions"    => ActionType.CloseAllPositions,
        "Send Notification"      => ActionType.Notify,
        _                        => ActionType.Notify
    };

    private static string TypeToLabel(ActionType type) => type switch
    {
        ActionType.StartDcaBuy          => "DCA Buy",
        ActionType.MoveStopToBreakeven  => "Move Stop to Breakeven",
        ActionType.StartFundingArb      => "Start Funding Arb",
        ActionType.PauseGridBot         => "Pause Grid Bot",
        ActionType.ResumeGridBot        => "Resume Grid Bot",
        ActionType.CloseAllPositions    => "Close All Positions",
        ActionType.Notify               => "Send Notification",
        _                               => "Send Notification"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Rule list-row VM
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RuleRowVM : ReactiveObject
{
    public CompositeRule Model { get; }

    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEnabled, value);
            Model.IsEnabled = value;
            this.RaisePropertyChanged(nameof(EnabledLabel));
            this.RaisePropertyChanged(nameof(EnabledColor));
            this.RaisePropertyChanged(nameof(EnabledBackground));
        }
    }

    public string Name             => Model.Name;
    public string ConditionSummary => BuildConditionSummary();
    public string ActionSummary    => BuildActionSummary();
    public string OneLineSummary   => $"{ConditionSummary}  →  {ActionSummary}";
    public string CooldownLabel    => CooldownToLabel(Model.Cooldown);

    public string LastTriggerLabel => Model.LastTriggeredAt is null
        ? "Never fired"
        : (DateTime.UtcNow - Model.LastTriggeredAt.Value) switch
        {
            { TotalSeconds: < 60  } ts => $"{(int)ts.TotalSeconds}s ago",
            { TotalMinutes: < 60  } ts => $"{(int)ts.TotalMinutes}m ago",
            { TotalHours:   < 24  } ts => $"{(int)ts.TotalHours}h ago",
            TimeSpan ts                => $"{(int)ts.TotalDays}d ago"
        };

    public string TriggerCountLabel =>
        Model.TriggerCount > 0 ? $"✓ {Model.TriggerCount}×" : "—";

    public string EnabledLabel      => _isEnabled ? "ON"       : "OFF";
    public string EnabledColor      => _isEnabled ? "#21E6C1"  : "#8FA3B8";
    public string EnabledBackground => _isEnabled ? "#152625"  : "#111821";

    public ReactiveCommand<Unit, Unit> EditCommand   { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    public RuleRowVM(CompositeRule model,
                     Action<RuleRowVM> onEdit,
                     Action<RuleRowVM> onDelete)
    {
        Model     = model;
        _isEnabled = model.IsEnabled;

        EditCommand   = ReactiveCommand.Create(() => onEdit(this));
        DeleteCommand = ReactiveCommand.Create(() => onDelete(this));
        ToggleCommand = ReactiveCommand.Create(() => { IsEnabled = !IsEnabled; });
    }

    public void Refresh()
    {
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(ConditionSummary));
        this.RaisePropertyChanged(nameof(ActionSummary));
        this.RaisePropertyChanged(nameof(OneLineSummary));
        this.RaisePropertyChanged(nameof(LastTriggerLabel));
        this.RaisePropertyChanged(nameof(TriggerCountLabel));
        this.RaisePropertyChanged(nameof(CooldownLabel));
    }

    private string BuildConditionSummary()
    {
        if (Model.Conditions.Count == 0) return "(no conditions)";
        var sep   = Model.Logic == ConditionLogic.And ? " AND " : " OR ";
        return string.Join(sep, Model.Conditions.Select(FormatCondition));
    }

    private string BuildActionSummary()
    {
        if (Model.Actions.Count == 0) return "(no actions)";
        return string.Join(", ", Model.Actions.Select(FormatAction));
    }

    private static string FormatCondition(RuleCondition c) => c.Type switch
    {
        ConditionType.RsiBelow             => $"RSI({c.Symbol},{c.Param1:0})<{c.Param2:0}",
        ConditionType.RsiAbove             => $"RSI({c.Symbol},{c.Param1:0})>{c.Param2:0}",
        ConditionType.PriceAboveMa         => $"{c.Symbol}>MA({c.Param1:0})",
        ConditionType.PriceBelowMa         => $"{c.Symbol}<MA({c.Param1:0})",
        ConditionType.VolumeAboveSma       => $"Vol({c.Symbol})>SMA×{c.Param2:0.##}",
        ConditionType.OpenPositionPnlAbove => $"PnL>{c.Param1:0.##}%",
        ConditionType.OpenPositionPnlBelow => $"PnL<{c.Param1:0.##}%",
        ConditionType.FundingRateAbove     => $"Fund({c.Symbol})>{c.Param1:0.###}%",
        _                                  => c.Type.ToString()
    };

    private static string FormatAction(RuleAction a) => a.Type switch
    {
        ActionType.StartDcaBuy          => $"DCA ${a.Amount:0.##}",
        ActionType.MoveStopToBreakeven  => "→BE",
        ActionType.StartFundingArb      => $"FundArb",
        ActionType.PauseGridBot         => "Pause Grid",
        ActionType.ResumeGridBot        => "Resume Grid",
        ActionType.CloseAllPositions    => "Close All",
        ActionType.Notify               => "Notify",
        _                               => a.Type.ToString()
    };

    private static string CooldownToLabel(RuleCooldown cd) => cd switch
    {
        RuleCooldown.Once      => "One-time",
        RuleCooldown.Seconds30 => "30s cooldown",
        RuleCooldown.Minutes1  => "1m cooldown",
        RuleCooldown.Minutes5  => "5m cooldown",
        RuleCooldown.Minutes15 => "15m cooldown",
        RuleCooldown.Hours1    => "1h cooldown",
        RuleCooldown.Hours4    => "4h cooldown",
        RuleCooldown.Unlimited => "Always re-fires",
        _                      => "5m cooldown"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Master Composite-Rule VM
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CompositeRuleViewModel : ReactiveObject
{
    // ─── shared domain rules (given to engine by reference) ──────────────────
    private readonly List<CompositeRule>   _domainRules = [];
    private readonly CompositeRuleEngine   _engine;
    private          RuleRowVM?            _editTarget;

    // ── Rule list ─────────────────────────────────────────────────────────────
    public ObservableCollection<RuleRowVM> Rules { get; } = [];

    // ── Editor state ──────────────────────────────────────────────────────────

    private bool    _isEditing;
    private string  _editingName     = "New Rule";
    private string  _selectedLogic   = "AND";
    private string  _selectedCooldown = "5 minutes";

    public bool IsEditing
    {
        get => _isEditing;
        set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    public string EditingName
    {
        get => _editingName;
        set => this.RaiseAndSetIfChanged(ref _editingName, value);
    }

    public string SelectedLogic
    {
        get => _selectedLogic;
        set => this.RaiseAndSetIfChanged(ref _selectedLogic, value);
    }

    public string SelectedCooldown
    {
        get => _selectedCooldown;
        set => this.RaiseAndSetIfChanged(ref _selectedCooldown, value);
    }

    public IReadOnlyList<string> LogicOptions    { get; } = ["AND", "OR"];
    public IReadOnlyList<string> CooldownOptions { get; } =
    [
        "One-time", "30 seconds", "1 minute", "5 minutes",
        "15 minutes", "1 hour", "4 hours", "Always"
    ];

    public ObservableCollection<RuleConditionEditorVM> EditingConditions { get; } = [];
    public ObservableCollection<RuleActionEditorVM>    EditingActions    { get; } = [];

    // ── Engine status ─────────────────────────────────────────────────────────

    private bool   _isEngineRunning;
    private string _engineStatus = "Engine stopped — press ▶ to activate";

    public bool IsEngineRunning
    {
        get => _isEngineRunning;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEngineRunning, value);
            this.RaisePropertyChanged(nameof(EngineButtonLabel));
            this.RaisePropertyChanged(nameof(EngineButtonColor));
            this.RaisePropertyChanged(nameof(EngineStatusColor));
        }
    }

    public string EngineStatus
    {
        get => _engineStatus;
        set => this.RaiseAndSetIfChanged(ref _engineStatus, value);
    }

    public string EngineButtonLabel => _isEngineRunning ? "◼ Stop Engine" : "▶ Start Engine";
    public string EngineButtonColor => _isEngineRunning ? "#FF5D73"       : "#21E6C1";
    public string EngineStatusColor => _isEngineRunning ? "#3DDC84"       : "#8FA3B8";

    public ObservableCollection<string> TriggerLog { get; } = [];

    // ── Empty-state helpers for XAML visibility ───────────────────────────────
    public bool HasNoRules            => Rules.Count == 0;
    public bool HasNoEditingConditions => EditingConditions.Count == 0;
    public bool HasNoEditingActions    => EditingActions.Count == 0;
    public bool HasNoTriggerLog        => TriggerLog.Count == 0;

    // ── Callback for external toast notifications ─────────────────────────────
    public Action<string>? ToastRequested { get; set; }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddRuleCommand      { get; }
    public ReactiveCommand<Unit, Unit> SaveRuleCommand     { get; }
    public ReactiveCommand<Unit, Unit> CancelEditCommand   { get; }
    public ReactiveCommand<Unit, Unit> AddConditionCommand { get; }
    public ReactiveCommand<Unit, Unit> AddActionCommand    { get; }
    public ReactiveCommand<Unit, Unit> ToggleEngineCommand { get; }
    public ReactiveCommand<Unit, Unit> TestNowCommand      { get; }
    public ReactiveCommand<Unit, Unit> ClearLogCommand     { get; }

    // ── Engine reference (for wiring callbacks from MainWindowViewModel) ──────
    public CompositeRuleEngine Engine => _engine;

    // ─── ctor ─────────────────────────────────────────────────────────────────

    public CompositeRuleViewModel()
    {
        _engine = new CompositeRuleEngine(_domainRules);

        _engine.OnRuleTriggered += (name, summary) =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                TriggerLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {summary}");
                if (TriggerLog.Count > 60) TriggerLog.RemoveAt(TriggerLog.Count - 1);
                foreach (var row in Rules) row.Refresh();   // update last-trigger labels
            });

        _engine.OnStatusChanged += status =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                EngineStatus = $"Engine {status} — evaluation every {10}s";
            });

        _engine.OnNotify += msg =>
            Dispatcher.UIThread.InvokeAsync(() => ToastRequested?.Invoke(msg));

        AddRuleCommand      = ReactiveCommand.Create(StartAddNewRule,    outputScheduler: App.UiScheduler);
        SaveRuleCommand     = ReactiveCommand.Create(SaveCurrentRule,    outputScheduler: App.UiScheduler);
        CancelEditCommand   = ReactiveCommand.Create(() => { IsEditing = false; }, outputScheduler: App.UiScheduler);
        AddConditionCommand = ReactiveCommand.Create(AddCondition,       outputScheduler: App.UiScheduler);
        AddActionCommand    = ReactiveCommand.Create(AddAction,          outputScheduler: App.UiScheduler);
        ToggleEngineCommand = ReactiveCommand.Create(ToggleEngine,       outputScheduler: App.UiScheduler);
        TestNowCommand      = ReactiveCommand.Create(TestNow,            outputScheduler: App.UiScheduler);
        ClearLogCommand     = ReactiveCommand.Create(() => TriggerLog.Clear(), outputScheduler: App.UiScheduler);

        SeedExampleRules();
    }

    // ─── rule list management ─────────────────────────────────────────────────

    private void SeedExampleRules()
    {
        AddRuleModel(new CompositeRule
        {
            Name    = "RSI Oversold + Volume Spike → DCA Buy",
            Logic   = ConditionLogic.And,
            Cooldown = RuleCooldown.Minutes15,
            Conditions =
            [
                new() { Type = ConditionType.RsiBelow,       Symbol = "BTCUSDT", Param1 = 14, Param2 = 30 },
                new() { Type = ConditionType.VolumeAboveSma, Symbol = "BTCUSDT", Param1 = 20, Param2 = 1.5m }
            ],
            Actions = [new() { Type = ActionType.StartDcaBuy, Symbol = "BTCUSDT", Amount = 50 }]
        });

        AddRuleModel(new CompositeRule
        {
            Name     = "Position > 2% profit → Move Stop to BE",
            Logic    = ConditionLogic.And,
            Cooldown = RuleCooldown.Minutes5,
            Conditions =
            [
                new() { Type = ConditionType.OpenPositionPnlAbove, Symbol = "ANY", Param1 = 2m }
            ],
            Actions = [new() { Type = ActionType.MoveStopToBreakeven, Symbol = "ANY" }]
        });

        AddRuleModel(new CompositeRule
        {
            Name      = "Funding > 0.08% → Open Funding Arb",
            IsEnabled = false,
            Logic     = ConditionLogic.And,
            Cooldown  = RuleCooldown.Hours1,
            Conditions =
            [
                new() { Type = ConditionType.FundingRateAbove, Symbol = "BTCUSDT", Param1 = 0.08m }
            ],
            Actions = [new() { Type = ActionType.StartFundingArb, Symbol = "BTCUSDT" }]
        });

        AddRuleModel(new CompositeRule
        {
            Name      = "RSI Overbought → Notify",
            IsEnabled = false,
            Logic     = ConditionLogic.And,
            Cooldown  = RuleCooldown.Hours1,
            Conditions =
            [
                new() { Type = ConditionType.RsiAbove, Symbol = "BTCUSDT", Param1 = 14, Param2 = 70 }
            ],
            Actions = [new() { Type = ActionType.Notify, Message = "BTC RSI overbought (>70) — consider reducing position." }]
        });
    }

    private void AddRuleModel(CompositeRule rule)
    {
        _domainRules.Add(rule);
        Rules.Add(new RuleRowVM(rule, StartEditRule, DeleteRule));
    }

    // ─── editor helpers ───────────────────────────────────────────────────────

    private void StartAddNewRule()
    {
        _editTarget = null;
        EditingName = "New Rule";
        SelectedLogic = "AND";
        SelectedCooldown = "5 minutes";
        EditingConditions.Clear();
        EditingActions.Clear();
        AddCondition();
        AddAction();
        IsEditing = true;
    }

    private void StartEditRule(RuleRowVM row)
    {
        _editTarget = row;
        EditingName      = row.Model.Name;
        SelectedLogic    = row.Model.Logic == ConditionLogic.And ? "AND" : "OR";
        SelectedCooldown = CooldownToLabel(row.Model.Cooldown);

        EditingConditions.Clear();
        foreach (var c in row.Model.Conditions)
            EditingConditions.Add(RuleConditionEditorVM.FromModel(c, RemoveCondition));

        EditingActions.Clear();
        foreach (var a in row.Model.Actions)
            EditingActions.Add(RuleActionEditorVM.FromModel(a, RemoveAction));

        IsEditing = true;
    }

    private void SaveCurrentRule()
    {
        if (EditingConditions.Count == 0)
        {
            ToastRequested?.Invoke("Add at least one condition before saving.");
            return;
        }
        if (EditingActions.Count == 0)
        {
            ToastRequested?.Invoke("Add at least one action before saving.");
            return;
        }

        var model = _editTarget?.Model ?? new CompositeRule();
        model.Name       = string.IsNullOrWhiteSpace(EditingName) ? "Untitled Rule" : EditingName;
        model.Logic      = SelectedLogic == "AND" ? ConditionLogic.And : ConditionLogic.Or;
        model.Cooldown   = LabelToCooldown(SelectedCooldown);
        model.Conditions = EditingConditions.Select(c => c.ToModel()).ToList();
        model.Actions    = EditingActions.Select(a => a.ToModel()).ToList();

        if (_editTarget is null)
        {
            AddRuleModel(model);
        }
        else
        {
            _editTarget.Refresh();
        }

        IsEditing = false;
    }

    private void DeleteRule(RuleRowVM row)
    {
        _domainRules.Remove(row.Model);
        Rules.Remove(row);
    }

    private void AddCondition() =>
        EditingConditions.Add(new RuleConditionEditorVM(RemoveCondition));

    private void AddAction() =>
        EditingActions.Add(new RuleActionEditorVM(RemoveAction));

    private void RemoveCondition(RuleConditionEditorVM vm) => EditingConditions.Remove(vm);
    private void RemoveAction(RuleActionEditorVM vm)        => EditingActions.Remove(vm);

    // ─── engine control ───────────────────────────────────────────────────────

    private void ToggleEngine()
    {
        if (_isEngineRunning) { _engine.Stop(); IsEngineRunning = false; }
        else                  { _engine.Start(); IsEngineRunning = true; }
    }

    private void TestNow()
    {
        _engine.EvaluateAll();
        TriggerLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Manual evaluation triggered ({_domainRules.Count(r => r.IsEnabled)} active rules)");
    }

    // ─── static helpers ───────────────────────────────────────────────────────

    private static string CooldownToLabel(RuleCooldown cd) => cd switch
    {
        RuleCooldown.Once      => "One-time",
        RuleCooldown.Seconds30 => "30 seconds",
        RuleCooldown.Minutes1  => "1 minute",
        RuleCooldown.Minutes5  => "5 minutes",
        RuleCooldown.Minutes15 => "15 minutes",
        RuleCooldown.Hours1    => "1 hour",
        RuleCooldown.Hours4    => "4 hours",
        RuleCooldown.Unlimited => "Always",
        _                      => "5 minutes"
    };

    private static RuleCooldown LabelToCooldown(string label) => label switch
    {
        "One-time"   => RuleCooldown.Once,
        "30 seconds" => RuleCooldown.Seconds30,
        "1 minute"   => RuleCooldown.Minutes1,
        "5 minutes"  => RuleCooldown.Minutes5,
        "15 minutes" => RuleCooldown.Minutes15,
        "1 hour"     => RuleCooldown.Hours1,
        "4 hours"    => RuleCooldown.Hours4,
        "Always"     => RuleCooldown.Unlimited,
        _            => RuleCooldown.Minutes5
    };
}
