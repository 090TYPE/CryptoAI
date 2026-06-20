using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class LiquidationProfileTests
{
    [Fact]
    public void BarWidth_is_zero_when_usd_is_zero()
        => Assert.Equal(0.0, LiquidationHeatmapViewModel.BarWidth(0, maxUsd: 100, usableWidth: 1000), 3);

    [Fact]
    public void BarWidth_is_full_usable_width_at_max()
        => Assert.Equal(1000.0, LiquidationHeatmapViewModel.BarWidth(100, maxUsd: 100, usableWidth: 1000), 3);

    [Fact]
    public void BarWidth_is_half_at_half_max()
        => Assert.Equal(500.0, LiquidationHeatmapViewModel.BarWidth(50, maxUsd: 100, usableWidth: 1000), 3);

    [Fact]
    public void BarWidth_is_zero_when_max_is_nonpositive()
        => Assert.Equal(0.0, LiquidationHeatmapViewModel.BarWidth(50, maxUsd: 0, usableWidth: 1000), 3);
}
