using System;
using Avalonia;
using Avalonia.Controls;

namespace CryptoAITerminal.TerminalUI.Controls;

/// <summary>Arranges children on a 12-column snap grid by attached Col/Row/ColSpan/RowSpan.
/// Cell width = panel width / 12; cell height is a fixed constant.</summary>
public class WidgetGridPanel : Panel
{
    public const int Columns = 12;
    public const double CellHeight = 84;
    public const double Gap = 10;

    public static readonly AttachedProperty<int> ColProperty =
        AvaloniaProperty.RegisterAttached<WidgetGridPanel, Control, int>("Col");
    public static readonly AttachedProperty<int> RowProperty =
        AvaloniaProperty.RegisterAttached<WidgetGridPanel, Control, int>("Row");
    public static readonly AttachedProperty<int> ColSpanProperty =
        AvaloniaProperty.RegisterAttached<WidgetGridPanel, Control, int>("ColSpan", 4);
    public static readonly AttachedProperty<int> RowSpanProperty =
        AvaloniaProperty.RegisterAttached<WidgetGridPanel, Control, int>("RowSpan", 1);

    public static void SetCol(Control c, int v) => c.SetValue(ColProperty, v);
    public static int GetCol(Control c) => c.GetValue(ColProperty);
    public static void SetRow(Control c, int v) => c.SetValue(RowProperty, v);
    public static int GetRow(Control c) => c.GetValue(RowProperty);
    public static void SetColSpan(Control c, int v) => c.SetValue(ColSpanProperty, v);
    public static int GetColSpan(Control c) => c.GetValue(ColSpanProperty);
    public static void SetRowSpan(Control c, int v) => c.SetValue(RowSpanProperty, v);
    public static int GetRowSpan(Control c) => c.GetValue(RowSpanProperty);

    static WidgetGridPanel()
    {
        AffectsParentArrange<WidgetGridPanel>(ColProperty, RowProperty, ColSpanProperty, RowSpanProperty);
        AffectsParentMeasure<WidgetGridPanel>(ColProperty, RowProperty, ColSpanProperty, RowSpanProperty);
    }

    private double CellWidth(double panelWidth) => panelWidth / Columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
        double cellW = CellWidth(width);
        int maxRowBottom = 0;

        foreach (var child in Children)
        {
            int colSpan = Math.Max(1, GetColSpan(child));
            int rowSpan = Math.Max(1, GetRowSpan(child));
            child.Measure(new Size(Math.Max(0, cellW * colSpan - Gap), Math.Max(0, CellHeight * rowSpan - Gap)));
            maxRowBottom = Math.Max(maxRowBottom, GetRow(child) + rowSpan);
        }

        return new Size(width, maxRowBottom * CellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double cellW = CellWidth(finalSize.Width);

        foreach (var child in Children)
        {
            int col = Math.Max(0, GetCol(child));
            int row = Math.Max(0, GetRow(child));
            int colSpan = Math.Max(1, GetColSpan(child));
            int rowSpan = Math.Max(1, GetRowSpan(child));

            double x = col * cellW;
            double y = row * CellHeight;
            double w = Math.Max(0, cellW * colSpan - Gap);
            double h = Math.Max(0, CellHeight * rowSpan - Gap);
            child.Arrange(new Rect(x, y, w, h));
        }

        return finalSize;
    }
}
