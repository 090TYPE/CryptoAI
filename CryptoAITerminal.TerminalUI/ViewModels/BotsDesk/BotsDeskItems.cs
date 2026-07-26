using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

// ─────────────────────────────────────────────────────────────────────────────
// Small display item view-models for the Bots desk. Colours are Avalonia-format
// strings (#AARRGGBB / #RRGGBB / rgba(...)) bound through the StringToBrush
// converter, matching the design mock which computes every colour per element.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One scrolling header ticker entry (recent fill / event).</summary>
public sealed class TickerItem
{
    public string Tag { get; init; } = "";
    public string Text { get; init; } = "";
    public string Val { get; init; } = "";
    public string Color { get; init; } = "#8fa3b8";
}

/// <summary>A type filter chip (ALL / GRID / DCA / …) above the table.</summary>
public sealed class TypeChip
{
    public string Label { get; init; } = "";
    public string Count { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#3d5a72";
    public string CountColor { get; init; } = "#1e3048";
    public ICommand? Command { get; init; }
}

/// <summary>Bulk action button shown when rows are selected.</summary>
public sealed class BulkAction
{
    public string Label { get; init; } = "";
    public string Color { get; init; } = "#8fa3b8";
    public ICommand? Command { get; init; }
}

/// <summary>An inline row action icon-button (start/pause/kill/logs/…).</summary>
public sealed class RowAction
{
    public string Icon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Color { get; init; } = "#8fa3b8";
    public ICommand? Command { get; init; }
}

/// <summary>An entry in a row's "⋯" context menu.</summary>
public sealed class RowMenuItem
{
    public string Label { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Color { get; init; } = "#e8f4ff";
    public ICommand? Command { get; init; }
}

/// <summary>A performance stat cell in the expanded Overview tab.</summary>
public sealed class StatCell
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = "#e8f4ff";
}

/// <summary>A grid level / ladder bar in the Overview tab.</summary>
public sealed class GridLevel
{
    public string Price { get; init; } = "";
    public string Width { get; init; } = "0%";        // used as a 0..1 ratio too
    public double Ratio { get; init; }                 // 0..1 fill fraction
    public string Color { get; init; } = "#152233";
    public double Opacity { get; init; } = 1;
    public string Tag { get; init; } = "";
    public string TagColor { get; init; } = "#2d4a5e";
}

/// <summary>A key/value row (open position, review, side-panel).</summary>
public sealed class KvRow
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = "#e8f4ff";
}

/// <summary>A fill in the expanded Fills tab.</summary>
public sealed class TradeRow
{
    public string Time { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Side { get; init; } = "";
    public string SideColor { get; init; } = "#8fa3b8";
    public string Price { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Total { get; init; } = "";
    public string Pnl { get; init; } = "";
    public string PnlColor { get; init; } = "#8fa3b8";
    public string Route { get; init; } = "";
}

/// <summary>A log line in the expanded Logs tab / agent log.</summary>
public sealed class LogRow
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = "";
    public string Color { get; init; } = "#4a6a82";
    public string Msg { get; init; } = "";
}

/// <summary>A log-level filter chip in the Logs tab.</summary>
public sealed class LogFilter
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = "#0d1b27";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

/// <summary>A toggle switch guard (kill-on-cap, cooldown, alerts, allowlist…).</summary>
public sealed class GuardToggle
{
    public string Label { get; init; } = "";
    public string Hint { get; init; } = "";
    public string TrackBg { get; init; } = "#152233";
    public string Knob { get; init; } = "#3d5a72";
    public double KnobLeft { get; init; } = 2;
    public ICommand? Command { get; init; }
}

/// <summary>A risk limit slider row in the Risk tab.</summary>
public sealed class RiskLimit
{
    public string Label { get; init; } = "";
    public string Display { get; init; } = "";
    public string Color { get; init; } = "#21e6c1";
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;
    public double Value { get; init; }
    public ICommand? Command { get; init; }   // parameter = new value (double)
}

/// <summary>A strategy template card (empty state + wizard step 1).</summary>
public sealed class TemplateCard
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Desc { get; init; } = "";
    public string Risk { get; init; } = "";
    public string Backtest { get; init; } = "";
    public string Apr { get; init; } = "";
    public string Accent { get; init; } = "#21e6c1";
    public string BadgeBg { get; init; } = "transparent";
    public string BadgeBorder { get; init; } = "#152233";
    public string PickBorder { get; init; } = "#0d1b27";
    public string PickBg { get; init; } = "#060d14";
    public ICommand? QuickCreate { get; init; }
    public ICommand? Pick { get; init; }
}

/// <summary>A visible-columns toggle in the Columns modal.</summary>
public sealed class ColumnToggle
{
    public string Label { get; init; } = "";
    public string Mark { get; init; } = "";
    public string BoxBorder { get; init; } = "#152233";
    public string BoxBg { get; init; } = "transparent";
    public string Border { get; init; } = "#0d1b27";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

/// <summary>An inline-action toggle in the Columns modal.</summary>
public sealed class ActionToggle
{
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "";
    public string IcColor { get; init; } = "#1e3048";
    public string Border { get; init; } = "#0d1b27";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

/// <summary>Header tab in a bot's expanded detail (Overview / Params / …).</summary>
public sealed class DetailTab
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = "transparent";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

/// <summary>A wizard step chip in the header.</summary>
public sealed class WizardStep
{
    public string N { get; init; } = "";
    public string Label { get; init; } = "";
    public string Border { get; init; } = "#0d1b27";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

/// <summary>Execution-mode card (PAPER / LIVE) in the wizard.</summary>
public sealed class WizardMode
{
    public string Label { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Border { get; init; } = "#152233";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

/// <summary>A checkbox line in a confirm dialog.</summary>
public sealed class ConfirmCheck : ReactiveObject
{
    private bool _on;
    public bool On { get => _on; set => this.RaiseAndSetIfChanged(ref _on, value); }
    public string Label { get; init; } = "";
    public string Mark => _on ? "✓" : "";
    public string BoxBorder => _on ? "#21e6c1" : "#152233";
    public string BoxBg => _on ? "#21e6c1" : "transparent";
    public ICommand? Command { get; set; }

    public void Refresh()
    {
        this.RaisePropertyChanged(nameof(Mark));
        this.RaisePropertyChanged(nameof(BoxBorder));
        this.RaisePropertyChanged(nameof(BoxBg));
    }
}

/// <summary>A copilot chat bubble.</summary>
public sealed class CopilotMsg
{
    public string Role { get; init; } = "";
    public string Text { get; init; } = "";
    public string Align { get; init; } = "Left";     // HorizontalAlignment name
    public string RoleColor { get; init; } = "#21e6c1";
    public string Bg { get; init; } = "#08161f";
}

/// <summary>Copilot suggestion chip.</summary>
public sealed class CopilotChip
{
    public string Label { get; init; } = "";
    public ICommand? Command { get; init; }
}

/// <summary>A capacity input in the autonomous agent panel.</summary>
public sealed class AgentCap : ReactiveObject
{
    private string _value = "";
    public string Label { get; init; } = "";
    public string Value
    {
        get => _value;
        set { this.RaiseAndSetIfChanged(ref _value, value); ValueChanged?.Invoke(value); }
    }
    public System.Action<string>? ValueChanged { get; init; }
}
