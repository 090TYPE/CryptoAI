using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

/// <summary>Pure (Avalonia-free) grid placement logic for the customizable dashboard.</summary>
public static class DashboardLayoutMath
{
    public const int Columns = 12;
    public const int MinColSpan = 2;
    public const int MinRowSpan = 1;

    /// <summary>The built-in default arrangement (mirrors the classic dashboard, top to bottom).</summary>
    public static IReadOnlyList<WidgetPlacement> DefaultLayout() => new[]
    {
        new WidgetPlacement("price-stats",    0, 0, 12, 1),
        new WidgetPlacement("tracked-coins",  0, 1,  4, 4),
        new WidgetPlacement("price-chart",    4, 1,  8, 3),
        new WidgetPlacement("liq-heatmap",    4, 4,  4, 2),
        new WidgetPlacement("order-book",     8, 4,  4, 2),
        new WidgetPlacement("sentiment",      0, 6, 12, 2),
    };

    /// <summary>True if any two placements share a cell.</summary>
    public static bool HasOverlap(IReadOnlyList<WidgetPlacement> layout)
    {
        for (int i = 0; i < layout.Count; i++)
            for (int j = i + 1; j < layout.Count; j++)
                if (Overlaps(layout[i], layout[j]))
                    return true;
        return false;
    }

    private static bool Overlaps(WidgetPlacement a, WidgetPlacement b)
        => a.Col < b.Col + b.ColSpan && b.Col < a.Col + a.ColSpan
        && a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;
}
