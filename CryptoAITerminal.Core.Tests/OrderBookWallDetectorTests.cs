using CryptoAITerminal.Core;
using CryptoAITerminal.Core.Models;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

public class OrderBookWallDetectorTests
{
    [Theory]
    [InlineData(100, 9, 1000, false)]   // 900  < 1000
    [InlineData(100, 10, 1000, true)]   // 1000 == 1000
    [InlineData(100, 11, 1000, true)]   // 1100 > 1000
    public void Usd_compares_notional_to_threshold(double price, double qty, double thr, bool expected)
    {
        var large = OrderBookWallDetector.IsLarge((decimal)price, (decimal)qty, WallHighlightMode.Usd, (decimal)thr, 0m);
        Assert.Equal(expected, large);
    }

    [Theory]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    public void Qty_compares_quantity_to_threshold(double qty, double thr, bool expected)
    {
        var large = OrderBookWallDetector.IsLarge(100m, (decimal)qty, WallHighlightMode.Qty, 0m, (decimal)thr);
        Assert.Equal(expected, large);
    }

    [Fact]
    public void Zero_threshold_never_highlights()
    {
        Assert.False(OrderBookWallDetector.IsLarge(100m, 9999m, WallHighlightMode.Usd, 0m, 0m));
        Assert.False(OrderBookWallDetector.IsLarge(100m, 9999m, WallHighlightMode.Qty, 0m, 0m));
    }

    [Fact]
    public void Intensity_scales_and_guards_zero_max()
    {
        Assert.Equal(0d, OrderBookWallDetector.Intensity(500m, 0m));
        Assert.Equal(0.5d, OrderBookWallDetector.Intensity(500m, 1000m), 3);
        Assert.Equal(1d, OrderBookWallDetector.Intensity(2000m, 1000m), 3); // clamped
    }
}
