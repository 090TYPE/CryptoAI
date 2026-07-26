using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

/// <summary>
/// One real bot engine in the Bots desk table. There is one row per engine
/// singleton the shell owns (grid / dca / rule / ai trader / autonomous agent /
/// trailing stop); the desk re-reads engine state into these fields on a timer.
/// Every metric is nullable: null renders as "—" because the app has no source
/// for it — the desk never substitutes a placeholder number.
/// </summary>
public sealed class BotRowViewModel : ReactiveObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly List<Point> NoSpark = new();

    public BotsDeskViewModel Desk { get; set; } = null!;

    public string Id { get; init; } = "";
    public string Type { get; init; } = "GRID";

    public string Name { get; set; } = "";
    public string Venue { get; set; } = "";
    public string Market { get; set; } = "";
    public string Mode { get; set; } = "paper";       // paper | live
    public string Status { get; set; } = "stopped";   // running | paused | stopped

    // ── live metrics (null ⇒ no source in the app ⇒ "—") ─────────────────────
    public double? Pnl24Usd { get; set; }
    public int? Fills24 { get; set; }
    public double? PnlTotalUsd { get; set; }
    public int? Fills { get; set; }
    public double? WinRatePct { get; set; }
    public double? BudgetUsd { get; set; }
    public double? BudgetUsedUsd { get; set; }

    /// <summary>Max drawdown of the row's realized equity curve over the desk's window (null ⇒ no closed trade).</summary>
    public double? MaxDdUsd { get; set; }
    public double? MaxDdPct { get; set; }

    /// <summary>Set when the desk observes the engine start; null while stopped.</summary>
    public DateTime? RunningSince { get; set; }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            this.RaiseAndSetIfChanged(ref _selected, value);
            RaiseSelectionVisuals();
        }
    }

    private bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            this.RaiseAndSetIfChanged(ref _expanded, value);
            this.RaisePropertyChanged(nameof(RowBg));
            this.RaisePropertyChanged(nameof(Caret));
        }
    }

    private bool _menuOpen;
    public bool MenuOpen { get => _menuOpen; set => this.RaiseAndSetIfChanged(ref _menuOpen, value); }

    /// <summary>The checkbox visuals and the row tint are all computed from <see cref="Selected"/>.</summary>
    private void RaiseSelectionVisuals()
    {
        this.RaisePropertyChanged(nameof(BoxBorder));
        this.RaisePropertyChanged(nameof(BoxBg));
        this.RaisePropertyChanged(nameof(BoxMark));
        this.RaisePropertyChanged(nameof(RowBg));
    }

    // ── derived colour / meta helpers ────────────────────────────────────────
    private (string c, string name) TM => BotsDeskData.TypeMeta(Type);
    private bool EffLive => Mode == "live" && !Desk.PaperOnly;

    // ── column display values ────────────────────────────────────────────────
    public string TypeLabel => Type;
    public string TypeFg => TM.c;
    public string TypeBg => Desk.Alpha(TM.c, "14");
    public string TypeBorder => Desk.Alpha(TM.c, "33");
    public string Subtitle => string.IsNullOrEmpty(Market) ? TM.name : TM.name + " · " + Market;

    public string ModeLabel => EffLive ? "LIVE" : "PAPER";
    public string ModeFg => EffLive ? BotsDeskData.Amber : "#3d5a72";
    public string ModeBg => EffLive ? "#150f04" : "transparent";
    public string ModeBorder => EffLive ? "#3a2a12" : SemanticColor.Stroke;

    public string StatusLabel => BotsDeskData.StatusMeta(Status).label;
    public string StatusColor => BotsDeskData.StatusMeta(Status).color;
    public bool StatusPulse => Status == "running";

    // PnL 24h — big line is the realized amount, small line the fill count.
    public string Pnl24Pct => Pnl24Usd is { } v ? Desk.Money(v, true) : BotsDeskData.Dash;
    public string Pnl24Color => Pnl24Usd is { } v ? Desk.Sgn(v) : BotsDeskData.Text3;
    public string Pnl24Abs => Fills24 is { } n && n > 0 ? n + (n == 1 ? " fill" : " fills") : "";

    public string PnlTotalAbs => PnlTotalUsd is { } v ? Desk.Money(v, true) : BotsDeskData.Dash;
    public string PnlTotalColor => PnlTotalUsd is { } v ? Desk.Sgn(v) : BotsDeskData.Text3;
    public string PnlTotalPct => PnlTotalUsd is null ? "" : "realized";

    /// <summary>
    /// Equity sparkline: the row's cumulative realized P&amp;L over the desk's window, mapped
    /// onto the 70×22 canvas the row template draws. Empty for an engine that closed nothing —
    /// the row shows no line rather than a zero baseline.
    /// </summary>
    private List<Point>? _spark;
    public List<Point> Spark => _spark ?? NoSpark;

    private const double SparkW = 70, SparkH = 22, SparkPad = 1.5;

    /// <summary>Re-maps the sparkline from a cumulative-P&amp;L series; null/short clears it.</summary>
    public void SetSpark(IReadOnlyList<decimal>? series)
    {
        if (series is null || series.Count < 2) { _spark = null; return; }

        double min = (double)series.Min(), max = (double)series.Max();
        bool flat = max - min < 1e-9;
        double range = flat ? 1 : max - min;

        var pts = new List<Point>(series.Count);
        for (int i = 0; i < series.Count; i++)
        {
            double x = i * SparkW / (series.Count - 1);
            double y = flat
                ? SparkH / 2
                : (SparkH - SparkPad) - ((double)series[i] - min) / range * (SparkH - SparkPad * 2);
            pts.Add(new Point(x, y));
        }
        _spark = pts;
    }

    public string Trades => Fills is { } n ? n.ToString("N0", Inv) : BotsDeskData.Dash;
    public string Winrate => WinRatePct is { } w ? Math.Round(w).ToString("0", Inv) + "%" : BotsDeskData.Dash;

    public string AllocStr => BudgetUsd is { } b ? Desk.Money(b) : BotsDeskData.Dash;
    public string AllocUsedPct => BudgetUsd is { } b && b > 0 && BudgetUsedUsd is { } u
        ? Math.Round(u / b * 100) + "%"
        : "";
    public double AllocRatio => BudgetUsd is { } b && b > 0 && BudgetUsedUsd is { } u
        ? Math.Clamp(u / b, 0, 1)
        : 0;
    public string AllocBarColor => AllocRatio > 0.75 ? BotsDeskData.Amber : BotsDeskData.Accent;

    // Max drawdown from the rolling peak of the same realized curve the sparkline draws.
    // The column is one narrow line, so it carries the percentage whenever the curve had a
    // real peak to fall from, and the USD amount otherwise. Both are shown in the detail panel.
    public string Dd_ => MaxDdUsd is not { } d ? BotsDeskData.Dash
        : MaxDdPct is { } p && p > 0 ? "−" + p.ToString("0.#", Inv) + "%"
        : Desk.Money(-d);

    public string DdColor => MaxDdUsd is not { } d || d <= 0 ? BotsDeskData.Dim
        : (MaxDdPct ?? 0) >= 20 ? BotsDeskData.Red
        : BotsDeskData.Amber;

    /// <summary>Both figures for the expanded overview, where there is room for them.</summary>
    public string MaxDdLabel => MaxDdUsd is not { } d ? BotsDeskData.Dash
        : MaxDdPct is { } p && p > 0
            ? Desk.Money(-d) + " · " + p.ToString("0.#", Inv) + "%"
            : Desk.Money(-d);

    public string UptimeStr => RunningSince is { } t
        ? BotsDeskData.Elapsed(DateTime.Now - t)
        : BotsDeskData.Dash;

    // expanded → selected → live &amp; running → plain
    public string RowBg => Expanded ? "#08131d"
        : Selected ? Desk.Alpha(BotsDeskData.Accent, "14")
        : EffLive && Status == "running" ? "rgba(244,184,96,.02)" : "transparent";
    public double Caret => Expanded ? 90 : 0;

    public string BoxBorder => Selected ? BotsDeskData.Accent : SemanticColor.Stroke;
    public string BoxBg => Selected ? BotsDeskData.Accent : "transparent";
    public string BoxMark => Selected ? "✓" : "";

    public ObservableCollection<RowAction> Actions { get; } = new();
    public ObservableCollection<RowMenuItem> MenuItems { get; } = new();

    public ICommand ExpandCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand MenuCommand { get; }

    public BotRowViewModel()
    {
        ExpandCommand = new RelayCommand(() => Desk.ToggleExpand(this));
        SelectCommand = new RelayCommand(() => Desk.ToggleSelect(this));
        MenuCommand = new RelayCommand(() => Desk.ToggleMenu(this));
    }

    /// <summary>Re-emits every computed display value (engine state changed).</summary>
    public void RefreshAll() => this.RaisePropertyChanged(string.Empty);
}
