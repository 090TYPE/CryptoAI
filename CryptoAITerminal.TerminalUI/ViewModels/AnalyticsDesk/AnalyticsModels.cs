using System.Collections.ObjectModel;
using System.Windows.Input;

namespace CryptoAITerminal.TerminalUI.ViewModels.AnalyticsDesk;

public sealed class PeriodChip
{
    public string Label { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#3d5a72";
    public ICommand? Command { get; init; }
}

public sealed class AnKpi
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Color { get; init; } = "#e8f4ff";
    public ICommand? Command { get; init; }
}

public sealed class EqToggle
{
    public string Label { get; init; } = "";
    public string Swatch { get; init; } = "#152233";
    public string Fg { get; init; } = "#2d4a5e";
    public string Border { get; init; } = "#0d1b27";
    public string Bg { get; init; } = "transparent";
    public ICommand? Command { get; init; }
}

public sealed class EqMark
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = "#8fa3b8";
    public ICommand? Command { get; init; }
}

public sealed class BreakdownRow
{
    public string Label { get; init; } = "";
    public string Wt { get; init; } = "";
    public string Pnl { get; init; } = "";
    public string PnlColor { get; init; } = "#8fa3b8";
    public double BarRatio { get; init; }
    public string BarColor { get; init; } = "#21e6c1";
    public string Bg { get; init; } = "transparent";
    public ICommand? Command { get; init; }
}

public sealed class BreakdownPanel
{
    public string Title { get; init; } = "";
    public string Col1 { get; init; } = "";
    public string SortLabel { get; init; } = "";
    public ICommand? SortCommand { get; init; }
    public ObservableCollection<BreakdownRow> Rows { get; } = new();
}

public sealed class ColHeader
{
    public string Label { get; init; } = "";
    public double Width { get; init; }
    public bool Fill { get; init; }
    public string Align { get; init; } = "Left";       // TextAlignment
    public string Color { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

public sealed class AnTradeRow
{
    public string Date { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Source { get; init; } = "";
    public string SrcColor { get; init; } = "#58a6ff";
    public string Side { get; init; } = "";
    public string SideColor { get; init; } = "#8fa3b8";
    public string Pnl { get; init; } = "";
    public string PnlPct { get; init; } = "";
    public string PnlColor { get; init; } = "#8fa3b8";
    public string Duration { get; init; } = "";
    public string Exit { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public ICommand? Command { get; init; }
}

public sealed class FilterChip
{
    public string Label { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#3d5a72";
    public ICommand? Command { get; init; }
}

public sealed class Bullet
{
    public string Text { get; init; } = "";
    public string Dot { get; init; } = "#21e6c1";
}

public sealed class Ratio
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Color { get; init; } = "#e8f4ff";
}

public sealed class DistBar
{
    public double HeightRatio { get; init; }
    public string Color { get; init; } = "#21e6c1";
    public string Title { get; init; } = "";
    public ICommand? Command { get; init; }
}
