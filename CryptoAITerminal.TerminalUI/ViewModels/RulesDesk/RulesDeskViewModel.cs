using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.RulesDesk;

/// <summary>
/// Rules desk — a live view over <see cref="CompositeRuleViewModel"/>: its rule
/// list, its editor, its engine and its trigger log. The desk owns no rule data
/// of its own; before <see cref="Attach"/> it is simply empty.
///
/// Rules are not persisted anywhere: whatever the engine holds in memory is all
/// there is, and it is gone when the app closes.
/// </summary>
public sealed class RulesDeskViewModel : ReactiveObject
{
    /// <summary>Mirrors CompositeRuleEngine's fixed evaluation interval.</summary>
    private const int EvalIntervalSec = 10;

    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly HashSet<RuleRowVM> _hooked = new();
    private readonly List<LogRow> _allLog = new();

    private MainWindowViewModel? _host;
    private CompositeRuleViewModel? _vm;
    private DateTime? _engineStartedAt;

    public ObservableCollection<RuleViewModel> VisibleRules { get; } = new();
    public ObservableCollection<TickerItem> Ticker { get; } = new();
    public ObservableCollection<FilterChip> Filters { get; } = new();
    public ObservableCollection<PresetCard> Presets { get; } = new();
    public ObservableCollection<ExampleChip> Examples { get; } = new();
    public ObservableCollection<LogRow> LogRows { get; } = new();
    public ObservableCollection<LogFilter> LogFilters { get; } = new();
    public ObservableCollection<CondEditVM> Conditions { get; } = new();
    public ObservableCollection<ActEditVM> Actions { get; } = new();
    public ObservableCollection<KvRow> PanelRows { get; } = new();

    public string[] Cooldowns => RulesDeskData.Cooldowns;

    /// <summary>Values match CompositeRuleViewModel.SelectedLogic ("AND" / "OR").</summary>
    public SelectOption[] Logics { get; } =
    {
        new() { Value = "AND", Label = "ALL conditions (AND)" },
        new() { Value = "OR", Label = "ANY condition (OR)" },
    };

    public RulesDeskViewModel()
    {
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => { if (EngineRunning) RaiseEvalCountdown(); };
        _tickTimer.Start();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HasToast = false; };

        ToggleEngineCommand = new RelayCommand(ToggleEngine);
        TestNowCommand = new RelayCommand(TestNow);
        NewRuleCommand = new RelayCommand(NewRule);
        OpenEvalPanelCommand = new RelayCommand(OpenEvalPanel);
        GenerateCommand = new RelayCommand(GenerateFromPrompt);
        ClearLogCommand = new RelayCommand(ClearLog);
        AddConditionCommand = new RelayCommand(() => Fire(_vm?.AddConditionCommand));
        AddActionCommand = new RelayCommand(() => Fire(_vm?.AddActionCommand));
        TestDraftCommand = new RelayCommand(TestDraft);
        SaveCommand = new RelayCommand(SaveDraft);
        CancelCommand = new RelayCommand(CancelDraft);
        DeleteCommand = new RelayCommand(DeleteEdited);
        CloseModalCommand = new RelayCommand(CloseModal);
        ConfirmRunCommand = new RelayCommand(RunConfirm);

        RebuildFilters();
        RebuildLogFilters();
        RebuildPresets();
        RebuildExamples();
    }

    // ── wiring ───────────────────────────────────────────────────────────────

    /// <summary>Binds the desk to the running rule engine. Everything shown from
    /// here on is the engine's own state.</summary>
    public void Attach(MainWindowViewModel host)
    {
        if (_host is not null) return;
        _host = host;
        _vm = host.CompositeRuleVM;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Rules.CollectionChanged += OnRulesChanged;
        _vm.TriggerLog.CollectionChanged += OnTriggerLogChanged;
        _vm.EditingConditions.CollectionChanged += OnEditorRowsChanged;
        _vm.EditingActions.CollectionChanged += OnEditorRowsChanged;

        SyncHooks();
        if (_vm.IsEngineRunning) _engineStartedAt = DateTime.UtcNow;

        RebuildAllLog();
        RebuildEditorRows();
        RebuildRules();
        RaiseHeader();
        RaiseFooter();
        RaiseNl();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CompositeRuleViewModel.IsEngineRunning):
                _engineStartedAt = _vm?.IsEngineRunning == true ? DateTime.UtcNow : null;
                RaiseHeader();
                RaiseFooter();
                break;
            case nameof(CompositeRuleViewModel.EngineStatus):
                RaiseFooter();
                break;
            case nameof(CompositeRuleViewModel.IsEditing):
                if (_vm?.IsEditing != true) EditId = "";
                RebuildEditorRows();
                RefreshRows();
                break;
            case nameof(CompositeRuleViewModel.EditingName):
            case nameof(CompositeRuleViewModel.SelectedCooldown):
                RaiseEditor();
                break;
            case nameof(CompositeRuleViewModel.SelectedLogic):
                UpdateTags();
                RaiseEditor();
                break;
            case nameof(CompositeRuleViewModel.NlPrompt):
                this.RaisePropertyChanged(nameof(NlPrompt));
                break;
            case nameof(CompositeRuleViewModel.AiRuleStatus):
            case nameof(CompositeRuleViewModel.AiRuleRunning):
                RaiseNl();
                break;
        }
    }

    private void OnRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncHooks();
        RebuildRules();
        RaiseHeader();
    }

    private void OnTriggerLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildAllLog();
        RebuildTicker();
        RefreshRows();
        RaiseHeader();
        RaiseFooter();
    }

    private void OnEditorRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildEditorRows();

    private void SyncHooks()
    {
        foreach (var row in _hooked.Where(r => _vm is null || !_vm.Rules.Contains(r)).ToList())
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            _hooked.Remove(row);
        }
        if (_vm is null) return;
        foreach (var row in _vm.Rules)
            if (_hooked.Add(row)) row.PropertyChanged += OnRowPropertyChanged;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RuleRowVM.IsEnabled)) RebuildRules();
        else RefreshRows();
        RaiseHeader();
    }

    private static void Fire(ICommand? cmd)
    {
        if (cmd is not null && cmd.CanExecute(null)) cmd.Execute(null);
    }

    private IReadOnlyList<RuleRowVM> Rows => _vm?.Rules ?? (IReadOnlyList<RuleRowVM>)Array.Empty<RuleRowVM>();

    private static string Now() => DateTime.Now.ToString("HH:mm:ss");

    // ── engine / header ──────────────────────────────────────────────────────

    public ICommand ToggleEngineCommand { get; }
    public ICommand TestNowCommand { get; }
    public ICommand NewRuleCommand { get; }
    public ICommand OpenEvalPanelCommand { get; }

    private bool EngineRunning => _vm?.IsEngineRunning == true;
    private int ActiveCount => Rows.Count(r => r.IsEnabled);
    private int TotalFires => Rows.Sum(r => r.Model.TriggerCount);

    public string EngLabel => EngineRunning ? "RUNNING" : "STOPPED";
    public string EngColor => EngineRunning ? RulesDeskData.Green : RulesDeskData.Dimmer;
    public string EngBorder => EngineRunning ? "#14302e" : SemanticColor.Stroke;
    public string EngBg => EngineRunning ? "#061615" : "#060d14";
    public string EngBtnLabel => EngineRunning ? "■ STOP ENGINE" : "▶ START ENGINE";
    public string EngBtnColor => EngineRunning ? RulesDeskData.Red : RulesDeskData.Accent;
    public string EngBtnBorder => EngineRunning ? "#3a1620" : "#14302e";
    public string EngBtnBg => EngineRunning ? "#14060a" : "#061615";

    public string HdrActive => ActiveCount + "/" + Rows.Count;
    public string HdrActiveSub => (Rows.Count - ActiveCount) + " disabled";
    public string HdrTriggers => TotalFires.ToString();
    public string HdrTriggersSub => Rows.Count == 0
        ? "no rules loaded"
        : "across " + Rows.Count(r => r.Model.TriggerCount > 0) + " rules · this session";

    // The engine ticks every EvalIntervalSec; the countdown is derived from the
    // moment it was started. Before that moment is known we only state the rate.
    private int SecondsToNextEval()
    {
        if (_engineStartedAt is null) return 0;
        var elapsed = (DateTime.UtcNow - _engineStartedAt.Value).TotalSeconds;
        return EvalIntervalSec - (int)(elapsed % EvalIntervalSec);
    }

    public string HdrEvalLabel => !EngineRunning
        ? "paused"
        : _engineStartedAt is null ? "every " + EvalIntervalSec + "s" : "in " + SecondsToNextEval() + "s";

    public double HdrEvalRatio => EngineRunning && _engineStartedAt is not null
        ? 1 - SecondsToNextEval() / (double)EvalIntervalSec
        : 0;

    public string HdrEvalColor => EngineRunning ? RulesDeskData.Accent : RulesDeskData.Faint;

    private void RaiseHeader()
    {
        foreach (var n in new[] { nameof(EngLabel), nameof(EngColor), nameof(EngBorder), nameof(EngBg), nameof(EngBtnLabel),
            nameof(EngBtnColor), nameof(EngBtnBorder), nameof(EngBtnBg), nameof(HdrActive), nameof(HdrActiveSub),
            nameof(HdrTriggers), nameof(HdrTriggersSub) })
            this.RaisePropertyChanged(n);
        RaiseEvalCountdown();
    }

    private void RaiseEvalCountdown()
    {
        this.RaisePropertyChanged(nameof(HdrEvalLabel));
        this.RaisePropertyChanged(nameof(HdrEvalRatio));
        this.RaisePropertyChanged(nameof(HdrEvalColor));
    }

    private void ToggleEngine()
    {
        if (_vm is null) { Toast("Rule engine is not connected", "warn"); return; }
        Fire(_vm.ToggleEngineCommand);
        Toast(EngineRunning
            ? "Rule engine started · evaluating every " + EvalIntervalSec + "s"
            : "Rule engine stopped", EngineRunning ? "ok" : "warn");
    }

    private void TestNow()
    {
        if (_vm is null) { Toast("Rule engine is not connected", "warn"); return; }
        Fire(_vm.TestNowCommand);
        Toast("Evaluated " + ActiveCount + " enabled rules", "info");
    }

    private void OpenEvalPanel()
        => OpenPanel("Evaluation loop", "CompositeRuleEngine · " + EvalIntervalSec + "s tick", new[]
        {
            new KvRow { Label = "Engine state", Value = EngineRunning ? "running" : "stopped", Color = EngineRunning ? RulesDeskData.Green : RulesDeskData.Dimmer },
            new KvRow { Label = "Tick interval", Value = EvalIntervalSec + " s", Color = RulesDeskData.Text },
            new KvRow { Label = "Rules loaded", Value = Rows.Count.ToString(), Color = RulesDeskData.Text },
            new KvRow { Label = "Enabled rules", Value = ActiveCount.ToString(), Color = RulesDeskData.Accent },
            new KvRow { Label = "Fires this session", Value = TotalFires.ToString(), Color = RulesDeskData.Green },
            new KvRow { Label = "Engine status", Value = _vm?.EngineStatus ?? "not connected", Color = RulesDeskData.Text3 },
        }, "Every tick the engine reads the indicator values it has been fed, evaluates each enabled rule and runs the actions of those whose conditions match and whose cooldown has expired. Rules live in memory only — they are not written to disk.");

    private void RebuildTicker()
    {
        Ticker.Clear();
        foreach (var l in _allLog.Take(3))
            Ticker.Add(new TickerItem { Tag = l.Level, Text = l.Msg, Val = l.Time, Color = l.Color });
    }

    // ── NL → rule ────────────────────────────────────────────────────────────

    public ICommand GenerateCommand { get; }

    public string NlPrompt
    {
        get => _vm?.NlPrompt ?? "";
        set { if (_vm is not null) _vm.NlPrompt = value ?? ""; this.RaisePropertyChanged(); }
    }

    public string NlStatus => _vm is null
        ? "rule engine not connected"
        : _vm.AiRuleRunning ? "parsing intent…"
        : string.IsNullOrWhiteSpace(_vm.AiRuleStatus) ? "idle" : _vm.AiRuleStatus;

    public string NlBtnLabel => _vm?.AiRuleRunning == true ? "GENERATING…" : "GENERATE";

    private void RaiseNl()
    {
        this.RaisePropertyChanged(nameof(NlStatus));
        this.RaisePropertyChanged(nameof(NlBtnLabel));
        this.RaisePropertyChanged(nameof(NlPrompt));
    }

    private void RebuildExamples()
    {
        Examples.Clear();
        foreach (var t in RulesDeskData.NlExamples)
        {
            var s = t;
            Examples.Add(new ExampleChip { Label = t, Command = new RelayCommand(() => NlPrompt = s) });
        }
    }

    private void GenerateFromPrompt()
    {
        if (_vm is null) { Toast("Rule engine is not connected", "warn"); return; }
        if (string.IsNullOrWhiteSpace(_vm.NlPrompt)) { Toast("Describe the rule first", "warn"); return; }
        EditId = "";                       // the generated rule lands in the editor as a new draft
        Fire(_vm.GenerateRuleFromTextCommand);
        RaiseNl();
    }

    // ── list / filter / presets ──────────────────────────────────────────────

    private string _search = "";
    private string _filter = "all";

    public string Search { get => _search; set { this.RaiseAndSetIfChanged(ref _search, value ?? ""); RebuildRules(); } }
    public string ListCount => VisibleRules.Count + " of " + Rows.Count;
    public bool ListEmpty => VisibleRules.Count == 0;

    public string EmptyTitle => _vm is null
        ? "Rule engine not connected"
        : Rows.Count == 0 ? "No rules yet — start from a preset" : "Nothing matches this filter";

    public string EmptySub => _vm is null
        ? "This desk shows the live CompositeRule engine once the shell hands it over."
        : Rows.Count == 0
            ? "A preset fills the editor — nothing reaches the engine until you save."
            : "Clear the search or switch the filter above.";

    private void RebuildFilters()
    {
        Filters.Clear();
        foreach (var (id, label) in new[] { ("all", "ALL"), ("on", "ENABLED"), ("off", "DISABLED") })
        {
            var key = id; var active = _filter == id;
            Filters.Add(new FilterChip
            {
                Label = label,
                Bg = active ? "#0e2a2a" : "transparent",
                Fg = active ? RulesDeskData.Accent : RulesDeskData.Dimmer,
                Command = new RelayCommand(() => { _filter = key; RebuildFilters(); RebuildRules(); })
            });
        }
    }

    private void RebuildPresets()
    {
        Presets.Clear();
        foreach (var p in RulesDeskData.Presets())
        {
            var pp = p;
            Presets.Add(new PresetCard { Name = p.Name, Meta = p.Meta, Desc = p.Desc, Command = new RelayCommand(() => AddPreset(pp)) });
        }
    }

    private void AddPreset(RulePreset p)
    {
        if (_vm is null) { Toast("Rule engine is not connected", "warn"); return; }
        StartNewDraft(p.Name, p.Logic, p.Cooldown, p.Conditions, p.Actions);
        _search = ""; _filter = "all";
        this.RaisePropertyChanged(nameof(Search));
        RebuildFilters();
        RebuildRules();
        Toast("“" + p.Name + "” loaded into the editor — review, then save", "info");
    }

    private void NewRule()
    {
        if (_vm is null) { Toast("Rule engine is not connected", "warn"); return; }
        EditId = "";
        Fire(_vm.AddRuleCommand);
        RefreshRows();
        RaiseEditor();
        Toast("Draft rule created — configure and save", "info");
    }

    private void DuplicateRule(RuleRowVM row)
    {
        if (_vm is null) return;
        StartNewDraft(row.Model.Name + " (copy)", row.Model.Logic, row.Model.Cooldown,
                      row.Model.Conditions, row.Model.Actions);
        Toast("Copy loaded into the editor — press SAVE to add it", "info");
    }

    /// <summary>Opens the live editor on a brand-new rule and fills it from the
    /// given clauses. Nothing is added to the engine until SAVE.</summary>
    private void StartNewDraft(string name, ConditionLogic logic, RuleCooldown cooldown,
                               IEnumerable<RuleCondition> conditions, IEnumerable<RuleAction> actions)
    {
        if (_vm is null) return;
        EditId = "";
        Fire(_vm.AddRuleCommand);                       // clears the edit target
        _vm.EditingName = name;
        _vm.SelectedLogic = logic == ConditionLogic.And ? "AND" : "OR";
        _vm.SelectedCooldown = RulesDeskData.CooldownLabel(cooldown);

        _vm.EditingConditions.Clear();
        foreach (var c in conditions)
            _vm.EditingConditions.Add(RuleConditionEditorVM.FromModel(c, RemoveEditorCondition));

        _vm.EditingActions.Clear();
        foreach (var a in actions)
            _vm.EditingActions.Add(RuleActionEditorVM.FromModel(a, RemoveEditorAction));

        RefreshRows();
        RaiseEditor();
    }

    private void RemoveEditorCondition(RuleConditionEditorVM vm) => _vm?.EditingConditions.Remove(vm);
    private void RemoveEditorAction(RuleActionEditorVM vm) => _vm?.EditingActions.Remove(vm);

    private IEnumerable<RuleRowVM> FilteredRules()
    {
        var q = _search.Trim().ToLowerInvariant();
        return Rows.Where(r =>
        {
            if (_filter == "on" && !r.IsEnabled) return false;
            if (_filter == "off" && r.IsEnabled) return false;
            if (q.Length > 0 && !(r.Name + " " + r.OneLineSummary).ToLowerInvariant().Contains(q)) return false;
            return true;
        });
    }

    private void RebuildRules()
    {
        VisibleRules.Clear();
        foreach (var r in FilteredRules())
        {
            var vm = new RuleViewModel { Desk = this, Row = r };
            BuildRuleActions(vm);
            VisibleRules.Add(vm);
        }
        foreach (var n in new[] { nameof(ListCount), nameof(ListEmpty), nameof(EmptyTitle), nameof(EmptySub) })
            this.RaisePropertyChanged(n);
    }

    private void RefreshRows()
    {
        foreach (var vm in VisibleRules) vm.RefreshAll();
    }

    private void BuildRuleActions(RuleViewModel vm)
    {
        var row = vm.Row;
        vm.Actions.Clear();
        vm.Actions.Add(new RuleActionButton
        {
            Label = row.IsEnabled ? "DISABLE" : "ENABLE",
            Title = "Toggle rule",
            Color = row.IsEnabled ? RulesDeskData.Amber : RulesDeskData.Green,
            Command = new RelayCommand(() => ToggleRule(row))
        });
        vm.Actions.Add(new RuleActionButton
        {
            Label = "⧉",
            Title = "Duplicate into the editor",
            Color = RulesDeskData.Text3,
            Command = new RelayCommand(() => DuplicateRule(row))
        });
        vm.Actions.Add(new RuleActionButton
        {
            Label = "✕",
            Title = "Delete",
            Color = RulesDeskData.Red,
            Command = new RelayCommand(() => AskDelete(row))
        });
    }

    private void ToggleRule(RuleRowVM row)
    {
        Fire(row.ToggleCommand);
        Toast(row.Name + (row.IsEnabled ? " enabled" : " disabled"), row.IsEnabled ? "ok" : "warn");
    }

    // ── editor ───────────────────────────────────────────────────────────────

    public string EditId { get; private set; } = "";

    public ICommand AddConditionCommand { get; }
    public ICommand AddActionCommand { get; }
    public ICommand TestDraftCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }

    public bool Editing => _vm?.IsEditing == true;
    public bool Idle => !Editing;
    public int Interval => EvalIntervalSec;

    private RuleRowVM? EditedRow => EditId.Length == 0 ? null : FindRow(EditId);

    public string EdMode => !Editing
        ? ""
        : EditedRow is { } row ? "editing · " + row.Name : "new rule · not saved yet";

    public string EdName
    {
        get => _vm?.EditingName ?? "";
        set { if (_vm is not null) _vm.EditingName = value ?? ""; OnDraftEdited(); }
    }

    public SelectOption? EdLogic
    {
        get => Logics.FirstOrDefault(l => l.Value == (_vm?.SelectedLogic ?? "AND"));
        set
        {
            if (_vm is null || value is null) return;
            _vm.SelectedLogic = value.Value;
            UpdateTags();
            OnDraftEdited();
        }
    }

    public string EdCooldown
    {
        get => _vm?.SelectedCooldown ?? "";
        set { if (_vm is not null && value is not null) _vm.SelectedCooldown = value; OnDraftEdited(); }
    }

    public string CondMeta => !Editing
        ? ""
        : Conditions.Count + " · " + (_vm?.SelectedLogic == "OR" ? "any may match" : "all must match");

    public string ActMeta => !Editing ? "" : Actions.Count + " · run in order";
    public bool NoConditions => Editing && Conditions.Count == 0;
    public bool NoActions => Editing && Actions.Count == 0;

    public string Preview
    {
        get
        {
            if (!Editing || _vm is null) return "";
            var conds = Conditions.Count == 0
                ? "(no conditions yet)"
                : string.Join(_vm.SelectedLogic == "OR" ? " OR " : " AND ", Conditions.Select(c => c.Summary));
            var acts = Actions.Count == 0 ? "(no actions yet)" : string.Join(", ", Actions.Select(a => a.Summary));
            return "When " + conds + " → " + acts + "." + RulesDeskData.CooldownTail(_vm.SelectedCooldown);
        }
    }

    /// <summary>The live editor writes into the rule only on save.</summary>
    public string DirtyLabel => Editing ? "applies on save" : "";
    public string DirtyColor => RulesDeskData.Amber;

    public void SelectRule(string id)
    {
        var row = FindRow(id);
        if (row is null || _vm is null) { EditId = ""; RefreshRows(); RaiseEditor(); return; }
        EditId = id;
        Fire(row.EditCommand);            // loads the rule into the live editor
        RefreshRows();
        RaiseEditor();
    }

    private RuleRowVM? FindRow(string id) => Rows.FirstOrDefault(r => r.Model.Id.ToString() == id);

    private void RebuildEditorRows()
    {
        Conditions.Clear();
        Actions.Clear();
        if (_vm is not null)
        {
            foreach (var c in _vm.EditingConditions) Conditions.Add(new CondEditVM { Desk = this, Source = c });
            foreach (var a in _vm.EditingActions) Actions.Add(new ActEditVM { Desk = this, Source = a });
        }
        UpdateTags();
        RaiseEditor();
    }

    private void UpdateTags()
    {
        var or = _vm?.SelectedLogic == "OR";
        for (int i = 0; i < Conditions.Count; i++) Conditions[i].Tag = i == 0 ? "IF" : (or ? "OR" : "AND");
        for (int i = 0; i < Actions.Count; i++) Actions[i].Tag = i == 0 ? "THEN" : "AND";
    }

    public void OnDraftEdited()
    {
        foreach (var n in new[] { nameof(EdMode), nameof(Preview), nameof(DirtyLabel), nameof(DirtyColor),
            nameof(CondMeta), nameof(ActMeta), nameof(NoConditions), nameof(NoActions) })
            this.RaisePropertyChanged(n);
    }

    private void RaiseEditor()
    {
        foreach (var n in new[] { nameof(Editing), nameof(Idle), nameof(EdMode), nameof(EdName), nameof(EdLogic), nameof(EdCooldown),
            nameof(CondMeta), nameof(ActMeta), nameof(NoConditions), nameof(NoActions), nameof(Preview), nameof(DirtyLabel), nameof(DirtyColor) })
            this.RaisePropertyChanged(n);
    }

    public void RemoveCond(CondEditVM vm)
    {
        Fire(vm.Source.RemoveCommand);
        OnDraftEdited();
    }

    public void RemoveAct(ActEditVM vm)
    {
        Fire(vm.Source.RemoveCommand);
        OnDraftEdited();
    }

    private void TestDraft()
        => Toast("The engine only evaluates saved rules — save first, then TEST NOW", "info");

    private void SaveDraft()
    {
        if (_vm is null) { Toast("Rule engine is not connected", "warn"); return; }
        var wasNew = EditId.Length == 0;
        Fire(_vm.SaveRuleCommand);           // validates and writes into the engine's rule list
        if (_vm.IsEditing) return;           // save was rejected — the VM raised its own toast
        RebuildRules();
        RaiseHeader();
        Toast(wasNew ? "Rule added — the engine picks it up on the next tick" : "Rule updated", "ok");
    }

    private void CancelDraft()
    {
        if (_vm is null) return;
        Fire(_vm.CancelEditCommand);
        EditId = "";
        RefreshRows();
        RaiseEditor();
    }

    private void DeleteEdited()
    {
        if (EditedRow is { } row) AskDelete(row);
        else Toast("This draft is not saved — press CANCEL to discard it", "info");
    }

    // ── log ──────────────────────────────────────────────────────────────────

    private string _logLevel = "all";
    public string LogMeta => _allLog.Count + " entries";
    public bool LogEmpty => LogRows.Count == 0;
    public ICommand ClearLogCommand { get; }

    /// <summary>TriggerLog entries look like "[HH:mm:ss] text"; rule fires carry
    /// the engine's "fired #N" marker, everything else is an evaluation note.</summary>
    private static LogRow ToLogRow(string entry)
    {
        var time = "";
        var msg = entry ?? "";
        if (msg.StartsWith('['))
        {
            var close = msg.IndexOf(']');
            if (close > 0)
            {
                time = msg[1..close];
                msg = msg[(close + 1)..].Trim();
            }
        }
        var fire = msg.Contains("fired #", StringComparison.Ordinal);
        return new LogRow
        {
            Time = time,
            Level = fire ? "FIRE" : "EVAL",
            Color = fire ? RulesDeskData.Green : RulesDeskData.Faint,
            Msg = msg
        };
    }

    private void RebuildAllLog()
    {
        _allLog.Clear();
        if (_vm is not null)
            foreach (var e in _vm.TriggerLog) _allLog.Add(ToLogRow(e));
        RebuildLog();
    }

    private void RebuildLog()
    {
        LogRows.Clear();
        foreach (var l in _logLevel == "all" ? _allLog : _allLog.Where(l => l.Level == _logLevel)) LogRows.Add(l);
        this.RaisePropertyChanged(nameof(LogMeta));
        this.RaisePropertyChanged(nameof(LogEmpty));
    }

    private void RebuildLogFilters()
    {
        LogFilters.Clear();
        foreach (var lv in new[] { "all", "FIRE", "EVAL" })
        {
            var key = lv; var active = _logLevel == lv;
            LogFilters.Add(new LogFilter
            {
                Label = lv == "all" ? "ALL" : lv,
                Border = active ? SemanticColor.Stroke : "#0d1b27",
                Bg = active ? "#08131d" : "transparent",
                Fg = active ? RulesDeskData.Accent : RulesDeskData.Faint,
                Command = new RelayCommand(() => { _logLevel = key; RebuildLogFilters(); RebuildLog(); })
            });
        }
    }

    private void ClearLog()
    {
        if (_vm is null) return;
        Fire(_vm.ClearLogCommand);
        Toast("Trigger log cleared", "info");
    }

    // ── footer ───────────────────────────────────────────────────────────────

    public string FooterState => EngineRunning ? "running" : "stopped";
    public string FooterColor => EngineRunning ? RulesDeskData.Green : RulesDeskData.Dimmer;
    public string FooterEvalCount => TotalFires + (TotalFires == 1 ? " fire this session" : " fires this session");
    public string FooterInterval => EvalIntervalSec + "s";
    public string FooterMode => _vm?.EngineStatus ?? "rule engine not connected";
    public string FooterSync => _allLog.Count > 0 && _allLog[0].Time.Length > 0
        ? "last entry " + _allLog[0].Time
        : "no trigger log entries";

    private void RaiseFooter()
    {
        foreach (var n in new[] { nameof(FooterState), nameof(FooterColor), nameof(FooterEvalCount),
            nameof(FooterInterval), nameof(FooterMode), nameof(FooterSync) })
            this.RaisePropertyChanged(n);
    }

    // ── modals ───────────────────────────────────────────────────────────────

    private string? _modal;
    public bool ModalPanel => _modal == "panel";
    public bool ModalConfirm => _modal == "confirm";
    public ICommand CloseModalCommand { get; }
    public ICommand ConfirmRunCommand { get; }

    public string PanelTitle { get; private set; } = "";
    public string PanelSub { get; private set; } = "";
    public string PanelNote { get; private set; } = "";

    private void OpenPanel(string title, string sub, IEnumerable<KvRow> rows, string note)
    {
        PanelTitle = title; PanelSub = sub; PanelNote = note;
        PanelRows.Clear();
        foreach (var r in rows) PanelRows.Add(r);
        _modal = "panel";
        foreach (var n in new[] { nameof(PanelTitle), nameof(PanelSub), nameof(PanelNote), nameof(ModalPanel), nameof(ModalConfirm) })
            this.RaisePropertyChanged(n);
    }

    private void CloseModal()
    {
        _modal = null;
        this.RaisePropertyChanged(nameof(ModalPanel));
        this.RaisePropertyChanged(nameof(ModalConfirm));
    }

    private RuleRowVM? _confirmRow;
    public string ConfirmTitle { get; private set; } = "";
    public string ConfirmBody { get; private set; } = "";
    public string ConfirmCta { get; private set; } = "DELETE RULE";

    private void AskDelete(RuleRowVM row)
    {
        _confirmRow = row;
        ConfirmTitle = "Delete “" + row.Name + "”";
        ConfirmBody = "The rule is removed from the engine together with its trigger count. Actions it already ran are not reverted.";
        ConfirmCta = "DELETE RULE";
        _modal = "confirm";
        foreach (var n in new[] { nameof(ConfirmTitle), nameof(ConfirmBody), nameof(ConfirmCta), nameof(ModalPanel), nameof(ModalConfirm) })
            this.RaisePropertyChanged(n);
    }

    private void RunConfirm()
    {
        if (_confirmRow is { } row)
        {
            var wasEdited = EditId == row.Model.Id.ToString();
            Fire(row.DeleteCommand);
            if (wasEdited)
            {
                Fire(_vm?.CancelEditCommand);
                EditId = "";
                RaiseEditor();
            }
            _confirmRow = null;
            RebuildRules();
            RaiseHeader();
            Toast("Rule deleted", "info");
        }
        CloseModal();
    }

    // ── toast ────────────────────────────────────────────────────────────────

    private bool _hasToast;
    public bool HasToast { get => _hasToast; private set => this.RaiseAndSetIfChanged(ref _hasToast, value); }
    public string ToastMsg { get; private set; } = "";
    public string ToastColor { get; private set; } = RulesDeskData.Accent;
    public string ToastIcon { get; private set; } = "";
    public string ToastBorder { get; private set; } = "#0d1b27";
    public string ToastMeta { get; private set; } = "";

    private void Toast(string msg, string kind = "ok")
    {
        (string color, string icon) = kind switch
        {
            "ok" => (RulesDeskData.Green, "✓"),
            "warn" => (RulesDeskData.Amber, "!"),
            "bad" => (RulesDeskData.Red, "✕"),
            "ai" => (RulesDeskData.Accent, "✦"),
            _ => (RulesDeskData.Accent, "›"),
        };
        ToastMsg = msg; ToastColor = color; ToastIcon = icon; ToastBorder = RulesDeskData.Alpha(color, "55"); ToastMeta = Now();
        foreach (var n in new[] { nameof(ToastMsg), nameof(ToastColor), nameof(ToastIcon), nameof(ToastBorder), nameof(ToastMeta) })
            this.RaisePropertyChanged(n);
        HasToast = true;
        _toastTimer.Stop(); _toastTimer.Start();
    }
}
