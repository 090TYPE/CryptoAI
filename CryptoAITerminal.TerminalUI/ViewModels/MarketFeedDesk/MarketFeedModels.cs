using System.Windows.Input;

namespace CryptoAITerminal.TerminalUI.ViewModels.MarketFeedDesk;

public sealed class ViewTab
{
    public string Label { get; init; } = "";
    public string Count { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#3d5a72";
    public string CountColor { get; init; } = "#1e3048";
    public ICommand? Command { get; init; }
}

public sealed class MfChip
{
    public string Label { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#3d5a72";
    public ICommand? Command { get; init; }
}

public sealed class SortChip
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = "#0d1b27";
    public string Bg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public ICommand? Command { get; init; }
}

public sealed class Bullet
{
    public string Text { get; init; } = "";
    public string Dot { get; init; } = SemanticColor.Accent;
}

public sealed class NewsRow
{
    public string Title { get; init; } = "";
    public string TitleColor { get; init; } = SemanticColor.Primary;
    public string Sentiment { get; init; } = "";
    public string SentColor { get; init; } = SemanticColor.Muted;
    public string SentBg { get; init; } = "#050f14";
    public string SentBorder { get; init; } = SemanticColor.Stroke;
    public string Tags { get; init; } = "";
    public bool Important { get; init; }
    public string Source { get; init; } = "";
    public string Age { get; init; } = "";
    public string Votes { get; init; } = "";
    public string VotesColor { get; init; } = SemanticColor.Muted;
    public string Impact { get; init; } = "";
    public string ImpactColor { get; init; } = "#2d4a5e";
    public string Bg { get; init; } = "transparent";
    public string LeftBorder { get; init; } = "transparent";
    public ICommand? Command { get; init; }
}

public sealed class TapeRow
{
    public string Venue { get; init; } = "";
    public string VenueColor { get; init; } = "#58a6ff";
    public string Time { get; init; } = "";
    public string Side { get; init; } = "";
    public string SideColor { get; init; } = SemanticColor.Muted;
    public string Weight { get; init; } = "Normal";   // FontWeight
    public string Price { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Usd { get; init; } = "";
    public string Wallet { get; init; } = "";
    public string Flag { get; init; } = "";
    public string FlagColor { get; init; } = "transparent";
    public string Bg { get; init; } = "transparent";
    public double UsdNum { get; init; }
    public ICommand? Command { get; init; }
}

/// <summary>A liquidation heat band (rendered as a proportional horizontal bar).</summary>
public sealed class LiqBand
{
    public double WidthRatio { get; init; }   // 0..1 of the plot width
    public string Fill { get; init; } = SemanticColor.Stroke;
    public string Title { get; init; } = "";
    public ICommand? Command { get; init; }
}

public sealed class LiqAxisLabel
{
    public string Text { get; init; } = "";
    public string Color { get; init; } = "#26405a";
}

public sealed class KpiCard
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Color { get; init; } = SemanticColor.Primary;
    public ICommand? Command { get; init; }
}

public sealed class SymBtn
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = SemanticColor.Stroke;
    public string Bg { get; init; } = "#050f14";
    public string Fg { get; init; } = SemanticColor.Muted;
    public ICommand? Command { get; init; }
}

public sealed class SideToggle
{
    public string Label { get; init; } = "";
    public string Mark { get; init; } = "";
    public string BoxBorder { get; init; } = SemanticColor.Stroke;
    public string BoxBg { get; init; } = "transparent";
    public string Fg { get; init; } = "#2d4a5e";
    public string Border { get; init; } = "#0d1b27";
    public string Bg { get; init; } = SemanticColor.Surface;
    public ICommand? Command { get; init; }
}

public sealed class SideRow
{
    public string Label { get; init; } = "";
    public double BarRatio { get; init; }
    public string BarColor { get; init; } = "#2a3f54";
    public string Value { get; init; } = "";
    public string ValueColor { get; init; } = SemanticColor.Muted;
    public ICommand? Command { get; init; }
}
