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
    public Action<string>? Changed { get; set; }

    private char _passChar => IsPassword ? '•' : '\0';
    public char PasswordChar => _passChar;

    private string _value = "";
    public string Value
    {
        get => _value;
        set { var v = Clean != null ? Clean(value ?? "") : (value ?? ""); this.RaiseAndSetIfChanged(ref _value, v); Changed?.Invoke(v); }
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
    public string Fg { get; init; } = "#8fa3b8";
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
    public string Border { get; init; } = "#152233";
    public string Bg { get; init; } = "#050f14";
    public string Fg { get; init; } = "#8fa3b8";
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
    public string Border { get; init; } = "#152233";
    public string Bg { get; init; } = "#050f14";
    public ICommand? LoadCommand { get; init; }
    public ICommand? ExportCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
}

public sealed class ExTabVM
{
    public string Label { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#8fa3b8";
    public string Dot { get; init; } = "#152233";
    public string State { get; init; } = "";
    public string StateColor { get; init; } = "#1e3048";
    public ICommand? Command { get; init; }
}

public sealed class PermToggle
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = "#152233";
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
    public string Dot { get; set; } = "#152233";
    public string Status { get; set; } = "";
    public string StatusColor { get; set; } = "#2d4a5e";
    public string Summary { get; set; } = "";
    public string Last { get; set; } = "";
    public string EnLabel { get; set; } = "ENABLE CHANNEL";
    public string EnColor { get; set; } = "#21e6c1";
    public string EnBorder { get; set; } = "#14302e";
    public string EnBg { get; set; } = "#061615";
    public string SslMark { get; set; } = "";
    public string SslBorder { get; set; } = "#152233";
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
    public string SideColor { get; init; } = "#8fa3b8";
    public ICommand? AcceptCommand { get; init; }
    public ICommand? SkipCommand { get; init; }
}

public sealed class ChannelChip
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = "#152233";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

public sealed class AlertRowVM
{
    public string Symbol { get; init; } = "";
    public string Rule { get; init; } = "";
    public string Dot { get; init; } = "#152233";
    public string Distance { get; init; } = "";
    public string DistColor { get; init; } = "#2d4a5e";
    public List<string> Channels { get; init; } = new();
    public string Repeat { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string ToggleLabel { get; init; } = "";
    public string ToggleColor { get; init; } = "#8fa3b8";
    public ICommand? ToggleCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
}

public sealed class HistoryRow
{
    public string Time { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Color { get; init; } = "#8fa3b8";
    public string Msg { get; init; } = "";
}

public sealed class HotkeyRowVM : ReactiveObject
{
    public string Label { get; init; } = "";
    public string KeyColor { get; init; } = "#8fa3b8";
    public string Placeholder { get; init; } = "";
    public Action<string>? Changed { get; set; }

    /// <summary>Supplies the formatted key label from the live source (e.g. "Esc", "1").</summary>
    public Func<string>? DisplayText { get; set; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            this.RaiseAndSetIfChanged(ref _value, value ?? "");
            Changed?.Invoke(value ?? "");
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
