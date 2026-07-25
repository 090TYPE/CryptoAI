using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.RulesDesk;

/// <summary>A rule card in the left list — a projection of a live
/// <see cref="RuleRowVM"/> owned by CompositeRuleViewModel.</summary>
public sealed class RuleViewModel : ReactiveObject
{
    public RulesDeskViewModel Desk { get; set; } = null!;
    public RuleRowVM Row { get; init; } = null!;

    public CompositeRule Model => Row.Model;
    public string Id => Row.Model.Id.ToString();

    public string Name => Row.Name;
    public string NameColor => Row.IsEnabled ? RulesDeskData.Text : "#7a90a6";
    public string Summary => Row.OneLineSummary;
    public string StateLabel => Row.IsEnabled ? "ENABLED" : "DISABLED";
    public string StateColor => Row.EnabledColor;
    public string StateBg => Row.IsEnabled ? "#061615" : "transparent";
    public string StateBorder => Row.IsEnabled ? "#14302e" : "#152233";

    public string LogicLabel =>
        $"{RulesDeskData.LogicShort(Model.Logic)} · {Model.Conditions.Count}c / {Model.Actions.Count}a";

    public string CooldownLabel =>
        "cooldown " + RulesDeskData.CooldownLabel(Model.Cooldown).ToLowerInvariant();

    public string LastFired => Model.LastTriggeredAt is null
        ? "never fired"
        : "last " + Model.LastTriggeredAt.Value.ToLocalTime().ToString("HH:mm:ss");

    public string TriggerLabel => Model.TriggerCount > 0
        ? Model.TriggerCount + (Model.TriggerCount == 1 ? " fire" : " fires")
        : "";

    public bool Selected => Desk.EditId == Id;
    public string RowBg => Selected ? "#08131d" : "transparent";
    public string SelBorder => Selected ? RulesDeskData.Accent : "transparent";

    public ObservableCollection<RuleActionButton> Actions { get; } = new();
    public ICommand EditCommand { get; }

    public RuleViewModel() { EditCommand = new RelayCommand(() => Desk.SelectRule(Id)); }

    public void RefreshAll() => this.RaisePropertyChanged(string.Empty);
}

public sealed class RuleActionButton
{
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
    public string Color { get; init; } = "#8fa3b8";
    public ICommand? Command { get; init; }
}

public sealed class FilterChip
{
    public string Label { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#3d5a72";
    public ICommand? Command { get; init; }
}

public sealed class PresetCard
{
    public string Name { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Desc { get; init; } = "";
    public ICommand? Command { get; init; }
}

public sealed class ExampleChip
{
    public string Label { get; init; } = "";
    public ICommand? Command { get; init; }
}

/// <summary>Numeric text helpers for the editor fields (keeps a leading minus,
/// unlike BotsDeskData.Decimalish — P&amp;L thresholds can be negative).</summary>
internal static class NumText
{
    public static string Clean(string? s)
    {
        s ??= "";
        var neg = s.StartsWith('-');
        var body = new string(s.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return neg ? "-" + body : body;
    }

    public static string Format(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    public static bool TryParse(string s, out decimal d) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
}

/// <summary>An editable condition row — a thin skin over the live
/// <see cref="RuleConditionEditorVM"/> held by CompositeRuleViewModel.</summary>
public sealed class CondEditVM : ReactiveObject
{
    public RulesDeskViewModel Desk { get; set; } = null!;
    public RuleConditionEditorVM Source { get; init; } = null!;
    public string[] Types => RulesDeskData.CondTypes;

    private string _tag = "IF";
    public string Tag { get => _tag; set => this.RaiseAndSetIfChanged(ref _tag, value); }

    private string? _p1;
    private string? _p2;

    public string Type
    {
        get => Source.SelectedType;
        set
        {
            if (value is null || Source.SelectedType == value) return;
            Source.SelectedType = value;
            RaiseTypeDeps();
            Desk?.OnDraftEdited();
        }
    }

    public string Symbol
    {
        get => Source.Symbol;
        set
        {
            Source.Symbol = (value ?? "").ToUpperInvariant();
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Summary));
            Desk?.OnDraftEdited();
        }
    }

    public string P1
    {
        get => _p1 ??= NumText.Format(Source.Param1);
        set
        {
            _p1 = NumText.Clean(value);
            if (NumText.TryParse(_p1, out var d)) Source.Param1 = d;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Summary));
            Desk?.OnDraftEdited();
        }
    }

    public string P2
    {
        get => _p2 ??= NumText.Format(Source.Param2);
        set
        {
            _p2 = NumText.Clean(value);
            if (NumText.TryParse(_p2, out var d)) Source.Param2 = d;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Summary));
            Desk?.OnDraftEdited();
        }
    }

    public string P1LabelText => Source.Param1Label;
    public bool HasP2 => Source.IsParam2Visible;
    public string P2LabelText => Source.Param2Label;
    public string Summary => Source.Summary;

    /// <summary>The engine keeps its indicator buffers private, so there is no
    /// live value to show next to a condition.</summary>
    public string LiveText => "";

    public ICommand RemoveCommand { get; }
    public CondEditVM() { RemoveCommand = new RelayCommand(() => Desk.RemoveCond(this)); }

    private void RaiseTypeDeps()
    {
        foreach (var n in new[] { nameof(Type), nameof(P1LabelText), nameof(HasP2), nameof(P2LabelText), nameof(Summary) })
            this.RaisePropertyChanged(n);
    }
}

/// <summary>An editable action row — a thin skin over the live
/// <see cref="RuleActionEditorVM"/> held by CompositeRuleViewModel.</summary>
public sealed class ActEditVM : ReactiveObject
{
    public RulesDeskViewModel Desk { get; set; } = null!;
    public RuleActionEditorVM Source { get; init; } = null!;
    public string[] Types => RulesDeskData.ActTypes;

    /// <summary>RuleAction has no per-action delivery channels, so this stays
    /// empty and the channel row stays hidden.</summary>
    public ObservableCollection<object> Channels { get; } = new();

    private string _tag = "THEN";
    public string Tag { get => _tag; set => this.RaiseAndSetIfChanged(ref _tag, value); }

    private string? _amount;

    public string Type
    {
        get => Source.SelectedType;
        set
        {
            if (value is null || Source.SelectedType == value) return;
            Source.SelectedType = value;
            RaiseTypeDeps();
            Desk?.OnDraftEdited();
        }
    }

    public string Symbol
    {
        get => Source.Symbol;
        set
        {
            Source.Symbol = (value ?? "").ToUpperInvariant();
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Summary));
            Desk?.OnDraftEdited();
        }
    }

    public string Amount
    {
        get => _amount ??= NumText.Format(Source.Amount);
        set
        {
            _amount = NumText.Clean(value);
            if (NumText.TryParse(_amount, out var d)) Source.Amount = d;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Summary));
            Desk?.OnDraftEdited();
        }
    }

    public string Message
    {
        get => Source.Message;
        set
        {
            Source.Message = value ?? "";
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Summary));
            Desk?.OnDraftEdited();
        }
    }

    public bool HasSymbol => Source.IsSymbolVisible;
    public bool HasAmount => Source.IsAmountVisible;
    public bool HasMessage => Source.IsMessageVisible;
    public bool HasChannels => false;
    public bool Risky => RulesDeskData.Risky(Source.SelectedType);
    public string RiskNote => RulesDeskData.RiskNote(Source.SelectedType);
    public string Summary => Source.Summary;

    public ICommand RemoveCommand { get; }
    public ActEditVM() { RemoveCommand = new RelayCommand(() => Desk.RemoveAct(this)); }

    private void RaiseTypeDeps()
    {
        foreach (var n in new[] { nameof(Type), nameof(HasSymbol), nameof(HasAmount), nameof(HasMessage),
            nameof(HasChannels), nameof(Risky), nameof(RiskNote), nameof(Summary) })
            this.RaisePropertyChanged(n);
    }
}
