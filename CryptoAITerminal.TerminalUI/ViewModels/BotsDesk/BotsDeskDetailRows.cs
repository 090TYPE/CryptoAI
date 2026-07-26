using System;
using System.Collections.Generic;
using System.Windows.Input;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

/// <summary>
/// A coin in the live DCA basket. Wraps the engine's own
/// <see cref="DcaCoinRowViewModel"/> and writes edits straight back to it.
/// </summary>
public sealed class CoinRowViewModel : ReactiveObject
{
    public BotsDeskViewModel Desk { get; set; } = null!;

    /// <summary>The engine row this mirrors (null only in the detached desk).</summary>
    public DcaCoinRowViewModel? Source { get; init; }

    public string Id { get; init; } = "";
    public string Symbol { get; init; } = "";

    private string _stats = "";
    public string Stats { get => _stats; set => this.RaiseAndSetIfChanged(ref _stats, value); }

    private string _weight = "0";
    public string Weight
    {
        get => _weight;
        set
        {
            var clean = BotsDeskData.Digits(value);
            this.RaiseAndSetIfChanged(ref _weight, clean);
            if (Source is not null && int.TryParse(clean, out var w) && w > 0)
                Source.WeightPercent = w;
            Desk?.OnCoinWeightChanged();
        }
    }

    private bool _ma;
    public bool Ma
    {
        get => _ma;
        set
        {
            this.RaiseAndSetIfChanged(ref _ma, value);
            if (Source is not null) Source.ConditionalBuy = value;
            RaiseMa();
        }
    }

    public string MaLabel => _ma ? "MA " + (Source?.MaPeriod ?? 200) + " ON" : "MA OFF";
    public string MaColor => _ma ? BotsDeskData.Accent : BotsDeskData.Faint;
    public string MaBorder => _ma ? "#14302e" : SemanticColor.Stroke;
    public string MaBg => _ma ? "#061615" : "transparent";

    public ICommand ToggleMaCommand { get; }
    public ICommand RemoveCommand { get; }

    public CoinRowViewModel()
    {
        ToggleMaCommand = new RelayCommand(() => Ma = !Ma);
        RemoveCommand = new RelayCommand(() => Desk.RemoveCoin(this));
    }

    private void RaiseMa()
    {
        this.RaisePropertyChanged(nameof(MaLabel));
        this.RaisePropertyChanged(nameof(MaColor));
        this.RaisePropertyChanged(nameof(MaBorder));
        this.RaisePropertyChanged(nameof(MaBg));
    }
}

/// <summary>
/// A TP/SL toggle+value row. Mirrors one real property pair on the rule bot
/// (<see cref="AIBotViewModel"/>) — the only engine with a TP/SL config.
/// </summary>
public sealed class TpSlRowViewModel : ReactiveObject
{
    public BotsDeskViewModel Desk { get; set; } = null!;
    public string Key { get; init; } = "";      // tp | partial | sl | trail
    public string ValueKey { get; init; } = ""; // tpVal | tp1 | tp2 | slVal | trailVal
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "%";
    public string Hint { get; init; } = "";

    /// <summary>Pushes the toggle state onto the engine.</summary>
    public Action<bool>? ApplyOn { get; init; }
    /// <summary>Pushes the numeric value onto the engine.</summary>
    public Action<string>? ApplyValue { get; init; }

    private bool _suppress;

    private bool _on;
    public bool On
    {
        get => _on;
        set
        {
            this.RaiseAndSetIfChanged(ref _on, value);
            if (!_suppress) ApplyOn?.Invoke(value);
            RaiseOn();
        }
    }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            var clean = BotsDeskData.Decimalish(value);
            this.RaiseAndSetIfChanged(ref _value, clean);
            if (!_suppress) ApplyValue?.Invoke(clean);
            Desk?.OnTpSlChanged(this);
        }
    }

    /// <summary>Refresh from the engine without echoing the write back.</summary>
    public void Sync(bool on, string value)
    {
        _suppress = true;
        On = on;
        Value = value;
        _suppress = false;
    }

    public string Mark => _on ? "✓" : "";
    public string BoxBorder => _on ? BotsDeskData.Accent : SemanticColor.Stroke;
    public string BoxBg => _on ? BotsDeskData.Accent : "transparent";
    public string LabelColor => _on ? BotsDeskData.Text : BotsDeskData.Faint;
    public double InputOpacity => _on ? 1 : 0.45;
    public string RowBg => _on ? "#08131d" : "transparent";

    public ICommand ToggleCommand { get; }

    public TpSlRowViewModel()
    {
        ToggleCommand = new RelayCommand(() => { On = !On; Desk?.OnTpSlChanged(this); });
    }

    private void RaiseOn()
    {
        this.RaisePropertyChanged(nameof(Mark));
        this.RaisePropertyChanged(nameof(BoxBorder));
        this.RaisePropertyChanged(nameof(BoxBg));
        this.RaisePropertyChanged(nameof(LabelColor));
        this.RaisePropertyChanged(nameof(InputOpacity));
        this.RaisePropertyChanged(nameof(RowBg));
    }
}

/// <summary>
/// A single editable engine parameter (Params tab). <see cref="Apply"/> writes the
/// edit straight through to the owning bot view-model.
/// </summary>
public sealed class ParamRowViewModel : ReactiveObject
{
    public BotsDeskViewModel Desk { get; set; } = null!;
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool IsSelect { get; init; }
    public bool IsText { get; init; }
    public List<string> Options { get; init; } = new();

    /// <summary>Write-back to the engine. Null ⇒ read-only row.</summary>
    public Action<string>? Apply { get; init; }

    private bool _suppress;

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            // a select whose engine value is not in the option list pushes null back
            // through the binding — never let that clear a real engine setting
            if (IsSelect && string.IsNullOrEmpty(value)) return;
            this.RaiseAndSetIfChanged(ref _value, value ?? "");
            if (_suppress) return;
            try { Apply?.Invoke(_value); }
            catch { /* engine setters clamp / reject — keep the desk responsive */ }
            Desk?.MarkDirty();
        }
    }

    /// <summary>Seed the initial engine value without flagging the row dirty.</summary>
    public void Seed(string value)
    {
        _suppress = true;
        Value = value;
        _suppress = false;
    }
}
