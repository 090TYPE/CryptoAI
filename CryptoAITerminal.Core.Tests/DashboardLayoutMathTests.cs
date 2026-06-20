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
}
