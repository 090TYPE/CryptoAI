using System.Collections.Generic;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class LiquidationProfileTests
{
    [Fact]
    public void Intensity_is_floor_when_zero()
        => Assert.Equal(0.06, LiquidationHeatmapViewModel.Intensity(0, 100), 3);

    [Fact]
    public void Intensity_is_one_at_max()
        => Assert.Equal(1.0, LiquidationHeatmapViewModel.Intensity(100, 100), 3);

    [Fact]
    public void Intensity_is_zero_when_max_nonpositive()
        => Assert.Equal(0.0, LiquidationHeatmapViewModel.Intensity(50, 0), 3);

    [Fact]
    public void Intensity_is_between_floor_and_one_midrange()
    {
        var v = LiquidationHeatmapViewModel.Intensity(50, 100);
        Assert.True(v > 0.06 && v < 1.0, $"expected mid value, got {v}");
    }

    [Fact]
    public void BuildBandRects_tiles_contiguously_and_covers_height()
    {
        var ys = new List<double> { 50, 200, 400 };
        var rects = LiquidationHeatmapViewModel.BuildBandRects(ys, 460);
        Assert.Equal(3, rects.Count);
        Assert.Equal(200, rects[0].Y + rects[0].Height, 3);
        Assert.Equal(400, rects[1].Y + rects[1].Height, 3);
        Assert.Equal(460, rects[2].Y + rects[2].Height, 3);
        foreach (var r in rects) Assert.True(r.Height >= 0);
    }
}
