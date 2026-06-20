using CryptoAITerminal.TerminalUI.ViewModels.Dashboard;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class DashboardLayoutMathTests
{
    [Fact]
    public void DefaultLayout_fits_in_12_columns_and_has_no_overlap()
    {
        var layout = DashboardLayoutMath.DefaultLayout();

        Assert.NotEmpty(layout);
        foreach (var w in layout)
        {
            Assert.InRange(w.Col, 0, DashboardLayoutMath.Columns - 1);
            Assert.True(w.Col + w.ColSpan <= DashboardLayoutMath.Columns, $"{w.Key} exceeds 12 cols");
            Assert.True(w.ColSpan >= DashboardLayoutMath.MinColSpan);
            Assert.True(w.RowSpan >= DashboardLayoutMath.MinRowSpan);
        }
        Assert.False(DashboardLayoutMath.HasOverlap(layout));
    }

    [Theory]
    [InlineData(0, 1, 0, 2, 1)]
    [InlineData(0, 99, 99, 12, 99)]
    [InlineData(10, 5, 3, 2, 3)]
    public void ClampSize_keeps_widget_within_grid_and_minimums(
        int col, int reqCol, int reqRow, int expCol, int expRow)
    {
        var (cs, rs) = DashboardLayoutMath.ClampSize(col, reqCol, reqRow);
        Assert.Equal(expCol, cs);
        Assert.Equal(expRow, rs);
    }

    [Fact]
    public void ClampPosition_keeps_widget_inside_grid()
    {
        Assert.Equal(0, DashboardLayoutMath.ClampCol(-3, colSpan: 4));
        Assert.Equal(8, DashboardLayoutMath.ClampCol(20, colSpan: 4));
        Assert.Equal(0, DashboardLayoutMath.ClampRow(-5));
    }

    [Fact]
    public void ResolveDrop_pushes_row_down_when_target_is_occupied()
    {
        var others = new[] { new WidgetPlacement("a", 0, 0, 6, 2) };
        var moved  = new WidgetPlacement("b", 0, 0, 6, 2);
        var result = DashboardLayoutMath.ResolveDrop(moved, others);
        Assert.Equal(0, result.Col);
        Assert.Equal(2, result.Row);
        Assert.False(DashboardLayoutMath.HasOverlap(new[] { others[0], result }));
    }

    [Fact]
    public void ResolveDrop_leaves_free_target_untouched()
    {
        var others = new[] { new WidgetPlacement("a", 0, 0, 6, 2) };
        var moved  = new WidgetPlacement("b", 6, 0, 6, 2);
        var result = DashboardLayoutMath.ResolveDrop(moved, others);
        Assert.Equal(6, result.Col);
        Assert.Equal(0, result.Row);
    }
}
