using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

/// <summary>One placed widget on the dashboard grid. Col/Row/spans are reactive so the
/// WidgetGridPanel re-arranges live during drag/resize.</summary>
public sealed class DashboardWidget : ReactiveObject
{
    public string Key { get; }
    public string Title { get; }

    private int _col, _row, _colSpan, _rowSpan;

    public DashboardWidget(WidgetPlacement p, string title)
    {
        Key = p.Key;
        Title = title;
        _col = p.Col; _row = p.Row; _colSpan = p.ColSpan; _rowSpan = p.RowSpan;
    }

    public int Col     { get => _col;     set => this.RaiseAndSetIfChanged(ref _col, value); }
    public int Row     { get => _row;     set => this.RaiseAndSetIfChanged(ref _row, value); }
    public int ColSpan { get => _colSpan; set => this.RaiseAndSetIfChanged(ref _colSpan, value); }
    public int RowSpan { get => _rowSpan; set => this.RaiseAndSetIfChanged(ref _rowSpan, value); }

    public WidgetPlacement ToPlacement() => new(Key, Col, Row, ColSpan, RowSpan);
}
