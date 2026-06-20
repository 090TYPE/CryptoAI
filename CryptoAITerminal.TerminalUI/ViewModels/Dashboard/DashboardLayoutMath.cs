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

    /// <summary>Clamp a requested span to minimums and to the 12-column right edge for the given column.</summary>
    public static (int ColSpan, int RowSpan) ClampSize(int col, int reqColSpan, int reqRowSpan)
    {
        int maxColSpan = Columns - col;
        int colSpan = System.Math.Clamp(reqColSpan, MinColSpan, maxColSpan < MinColSpan ? MinColSpan : maxColSpan);
        int rowSpan = System.Math.Max(reqRowSpan, MinRowSpan);
        return (colSpan, rowSpan);
    }

    public static int ClampCol(int col, int colSpan)
        => System.Math.Clamp(col, 0, System.Math.Max(0, Columns - colSpan));

    public static int ClampRow(int row) => System.Math.Max(0, row);

    /// <summary>Snap a dropped widget into the grid, then push its Row down until it no longer
    /// overlaps any of <paramref name="others"/>.</summary>
    public static WidgetPlacement ResolveDrop(WidgetPlacement moved, IReadOnlyList<WidgetPlacement> others)
    {
        int col = ClampCol(moved.Col, moved.ColSpan);
        int row = ClampRow(moved.Row);
        var candidate = moved with { Col = col, Row = row };

        while (others.Any(o => Overlaps(candidate, o)))
            candidate = candidate with { Row = candidate.Row + 1 };

        return candidate;
    }

    /// <summary>Place a new widget at column 0, first row where it doesn't overlap anything.</summary>
    public static WidgetPlacement PlaceNew(string key, int colSpan, int rowSpan, IReadOnlyList<WidgetPlacement> existing)
    {
        var (cs, rs) = ClampSize(0, colSpan, rowSpan);
        var candidate = new WidgetPlacement(key, 0, 0, cs, rs);
        while (existing.Any(o => Overlaps(candidate, o)))
            candidate = candidate with { Row = candidate.Row + 1 };
        return candidate;
    }

    /// <summary>Clean a loaded layout: drop unknown keys, clamp spans/positions into the grid.</summary>
    public static IReadOnlyList<WidgetPlacement> Sanitize(
        IReadOnlyList<WidgetPlacement> loaded, ISet<string> knownKeys)
    {
        var result = new List<WidgetPlacement>();
        foreach (var w in loaded)
        {
            if (!knownKeys.Contains(w.Key)) continue;
            var (cs, rs) = ClampSize(System.Math.Clamp(w.Col, 0, Columns - 1), w.ColSpan, w.RowSpan);
            int col = ClampCol(w.Col, cs);
            int row = ClampRow(w.Row);
            result.Add(new WidgetPlacement(w.Key, col, row, cs, rs));
        }
        return result;
    }
}
