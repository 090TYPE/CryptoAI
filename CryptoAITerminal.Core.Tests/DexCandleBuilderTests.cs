using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DexCandleBuilderTests
{
    private static readonly DateTime T0 = new(2026, 6, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmptySamples_ReturnsEmpty()
    {
        var r = DexCandleBuilder.Bucketize(new List<DexPriceSample>(), T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5), 100);
        Assert.Empty(r);
    }

    [Fact]
    public void BucketsOhlcCorrectly_AndFillsEmptyBucketsFlat()
    {
        var samples = new List<DexPriceSample>
        {
            new(T0.AddMinutes(0), 10m),
            new(T0.AddMinutes(1), 12m),
            new(T0.AddMinutes(2), 8m),   // bucket 0 [0,5): O10 C8 H12 L8
            new(T0.AddMinutes(6), 9m),   // bucket 1 [5,10): O9 C9
        };

        var r = DexCandleBuilder.Bucketize(samples, T0, T0.AddMinutes(10), TimeSpan.FromMinutes(5), 100);

        Assert.Equal(3, r.Count); // buckets at 0,5,10
        Assert.Equal(10m, r[0].Open);
        Assert.Equal(8m,  r[0].Close);
        Assert.Equal(12m, r[0].High);
        Assert.Equal(8m,  r[0].Low);
        Assert.Equal(9m,  r[1].Open);
        Assert.Equal(9m,  r[1].Close);
        // bucket 2 has no samples → flat at last close (9)
        Assert.Equal(9m,  r[2].Open);
        Assert.Equal(9m,  r[2].Close);
    }

    [Fact]
    public void MaxCandles_LimitsCount()
    {
        var samples = new List<DexPriceSample>
        {
            new(T0.AddMinutes(0), 10m),
            new(T0.AddMinutes(6), 11m),
            new(T0.AddMinutes(12), 12m),
        };

        var r = DexCandleBuilder.Bucketize(samples, T0, T0.AddMinutes(20), TimeSpan.FromMinutes(5), 2);

        Assert.Equal(2, r.Count);
    }
}
