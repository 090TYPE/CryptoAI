using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.SettingsDesk;

/// <summary>A stable editable field (input or select) — keeps focus across refreshes.</summary>
public sealed class SetField : ReactiveObject
{
    public string Label { get; init; } = "";
    public string Placeholder { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool IsPassword { get; init; }
    public bool IsSelect { get; init; }
    public bool IsText { get; init; } = true;
    public List<string> Options { get; init; } = new();
    public Func<string, string>? Clean { get; init; }
    public Action<string>? ValueChanged { get; set; }

    private char _passChar => IsPassword ? '•' : '\0';
    public char PasswordChar => _passChar;

    private string _value = "";
    public string Value
    {
        get => _value;
        set { var v = Clean != null ? Clean(value ?? "") : (value ?? ""); this.RaiseAndSetIfChanged(ref _value, v); ValueChanged?.Invoke(v); }
    }

    /// <summary>Push a value in from the live source without echoing it back.
    /// No-ops when unchanged so a focused caret is never reset.</summary>
    public void SetSilent(string? v)
    {
        var s = v ?? "";
        if (_value == s) return;
        _value = s;
        this.RaisePropertyChanged(nameof(Value));
    }
}

public sealed class NavItem
{
    public string Label { get; init; } = "";
    public string Badge { get; init; } = "";
    public string BadgeColor { get; init; } = "#1e3048";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = SemanticColor.Muted;
    public string Mark { get; init; } = "transparent";
    public ICommand? Command { get; init; }
}

public sealed class NavGroup
{
    public string Label { get; init; } = "";
    public ObservableCollection<NavItem> Items { get; } = new();
}

public sealed class ProviderCard
{
    public string Name { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Border { get; init; } = SemanticColor.Stroke;
    public string Bg { get; init; } = "#050f14";
    public string Fg { get; init; } = SemanticColor.Muted;
    public string DotBorder { get; init; } = "#2a3f54";
    public string DotBg { get; init; } = "transparent";
    public string Status { get; init; } = "";
    public string StatusColor { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

public sealed class ProfileCard
{
    public string Name { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Border { get; init; } = SemanticColor.Stroke;
    public string Bg { get; init; } = "#050f14";
    public ICommand? LoadCommand { get; init; }
    public ICommand? ExportCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
}

public sealed class ExTabVM
{
    public string Label { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = SemanticColor.Muted;
    public string Dot { get; init; } = SemanticColor.Stroke;
    public string State { get; init; } = "";
    public string StateColor { get; init; } = "#1e3048";
    public ICommand? Command { get; init; }
}

/// <summary>One provider / integration credential row in the Data providers section.
/// Stable instance — the value survives a refresh so a focused caret is never reset.</summary>
public sealed class IntegrationRowVM : ReactiveObject
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string EnvVar { get; init; } = "";
    public string Powers { get; init; } = "";
    public string Applies { get; init; } = "";
    public string Placeholder { get; init; } = "";
    public bool Secret { get; init; }
    public bool HasConsole { get; init; }
    public ICommand? ConsoleCommand { get; init; }

    public Action<string>? ValueChanged { get; set; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set { this.RaiseAndSetIfChanged(ref _value, value ?? ""); ValueChanged?.Invoke(_value); }
    }

    /// <summary>Push a stored value in without echoing it back to the writer.</summary>
    public void SetSilent(string? v)
    {
        var s = v ?? "";
        if (_value == s) return;
        _value = s;
        this.RaisePropertyChanged(nameof(Value));
    }

    /// <summary>'\0' once the section is revealed; secrets are masked until then.</summary>
    private char _mask;
    public char Mask { get => _mask; set => this.RaiseAndSetIfChanged(ref _mask, value); }

    private string _srcLabel = "NOT SET";
    public string SrcLabel { get => _srcLabel; set => this.RaiseAndSetIfChanged(ref _srcLabel, value); }
    private string _srcColor = "#2d4a5e";
    public string SrcColor { get => _srcColor; set => this.RaiseAndSetIfChanged(ref _srcColor, value); }
    private string _srcBg = "transparent";
    public string SrcBg { get => _srcBg; set => this.RaiseAndSetIfChanged(ref _srcBg, value); }
    private string _srcBorder = SemanticColor.Stroke;
    public string SrcBorder { get => _srcBorder; set => this.RaiseAndSetIfChanged(ref _srcBorder, value); }

    /// <summary>True when an external environment variable overrides whatever is typed here.</summary>
    private bool _envWins;
    public bool EnvWins { get => _envWins; set => this.RaiseAndSetIfChanged(ref _envWins, value); }
}

/// <summary>A radio-style option in the DEX section — MEV mode or Jito tip preset.</summary>
public sealed class DexModeChip
{
    public string Label { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Border { get; init; } = SemanticColor.Stroke;
    public string Bg { get; init; } = "#050f14";
    public string Fg { get; init; } = SemanticColor.Muted;
    public string DotBorder { get; init; } = "#2a3f54";
    public string DotBg { get; init; } = "transparent";
    /// <summary>Set when the mode is selectable but cannot work on this build / network.</summary>
    public string Warn { get; init; } = "";
    public bool HasWarn => Warn.Length > 0;
    public ICommand? Command { get; init; }
}

public sealed class IntegrationGroupVM
{
    public string Label { get; init; } = "";
    public ObservableCollection<IntegrationRowVM> Items { get; } = new();
}

public sealed class PermToggle
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = SemanticColor.Stroke;
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

public sealed class NtChannelVM : ReactiveObject
{
    /// <summary>Stable channel id (telegram / discord / ntfy / email).</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Hint { get; init; } = "";
    public int Cols { get; init; } = 1;
    public ObservableCollection<SetField> Fields { get; } = new();
    public bool HasSsl { get; init; }

    private bool _open;
    public bool Open { get => _open; set { this.RaiseAndSetIfChanged(ref _open, value); this.RaisePropertyChanged(nameof(Caret)); } }
    public string Caret => _open ? "▾" : "▸";

    public string Border { get; set; } = "#0d1b27";
    public string Dot { get; set; } = SemanticColor.Stroke;
    public string Status { get; set; } = "";
    public string StatusColor { get; set; } = "#2d4a5e";
    public string Summary { get; set; } = "";
    public string Last { get; set; } = "";
    public string EnLabel { get; set; } = "ENABLE CHANNEL";
    public string EnColor { get; set; } = SemanticColor.Accent;
    public string EnBorder { get; set; } = "#14302e";
    public string EnBg { get; set; } = "#061615";
    public string SslMark { get; set; } = "";
    public string SslBorder { get; set; } = SemanticColor.Stroke;
    public string SslBg { get; set; } = "transparent";

    public ICommand? ToggleOpenCommand { get; set; }
    public ICommand? TestCommand { get; set; }
    public ICommand? EnableCommand { get; set; }
    public ICommand? SslCommand { get; set; }

    public void Refresh() => this.RaisePropertyChanged(string.Empty);
}

public sealed class PendingSignalVM
{
    public string Label { get; init; } = "";
    public string Desc { get; init; } = "";
    public string SideColor { get; init; } = SemanticColor.Muted;
    public ICommand? AcceptCommand { get; init; }
    public ICommand? SkipCommand { get; init; }
}

public sealed class ChannelChip
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = SemanticColor.Stroke;
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

public sealed class AlertRowVM
{
    public string Symbol { get; init; } = "";
    public string Rule { get; init; } = "";
    public string Dot { get; init; } = SemanticColor.Stroke;
    public string Distance { get; init; } = "";
    public string DistColor { get; init; } = "#2d4a5e";
    public List<string> Channels { get; init; } = new();
    public string Repeat { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string ToggleLabel { get; init; } = "";
    public string ToggleColor { get; init; } = SemanticColor.Muted;
    public ICommand? ToggleCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
}

public sealed class HistoryRow
{
    public string Time { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Color { get; init; } = SemanticColor.Muted;
    public string Msg { get; init; } = "";
}

public sealed class HotkeyRowVM : ReactiveObject
{
    public string Label { get; init; } = "";
    public string KeyColor { get; init; } = SemanticColor.Muted;
    public string Placeholder { get; init; } = "";
    public Action<string>? ValueChanged { get; set; }

    /// <summary>Supplies the formatted key label from the live source (e.g. "Esc", "1").</summary>
    public Func<string>? DisplayText { get; set; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            this.RaiseAndSetIfChanged(ref _value, value ?? "");
            ValueChanged?.Invoke(value ?? "");
            this.RaisePropertyChanged(nameof(Display));
        }
    }

    public string Display
    {
        get
        {
            var live = DisplayText?.Invoke();
            if (!string.IsNullOrEmpty(live)) return live;
            return string.IsNullOrEmpty(_value) ? "—" : _value;
        }
    }

    public void SetSilentValue(string? v)
    {
        var s = v ?? "";
        if (_value == s) { this.RaisePropertyChanged(nameof(Display)); return; }
        _value = s;
        this.RaisePropertyChanged(nameof(Value));
        this.RaisePropertyChanged(nameof(Display));
    }

    private string _bg = "transparent";
    public string Bg { get => _bg; set => this.RaiseAndSetIfChanged(ref _bg, value); }
    private string _captureLabel = "CAPTURE";
    public string CaptureLabel { get => _captureLabel; set => this.RaiseAndSetIfChanged(ref _captureLabel, value); }
    public ICommand? CaptureCommand { get; set; }
}

public sealed class ExampleChip
{
    public string Label { get; init; } = "";
    public ICommand? Command { get; init; }
}
