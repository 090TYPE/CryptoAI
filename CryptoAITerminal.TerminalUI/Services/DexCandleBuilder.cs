using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>A single observed price tick for a DEX pair.</summary>
public sealed record DexPriceSample(DateTime TimestampUtc, decimal PriceUsd);

/// <summary>
/// Builds OHLCV candles from raw price samples by time-bucketing. Empty buckets
/// repeat the previous close (flat candle). Pure and synthetic-free.
/// </summary>
public static class DexCandleBuilder
{
    public static IReadOnlyList<DexOhlcvPoint> Bucketize(
        IReadOnlyList<DexPriceSample> samples,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan bucketSize,
        int maxCandles)
    {
        if (samples.Count == 0)
        {
            return Array.Empty<DexOhlcvPoint>();
        }

        var ordered = samples
            .Where(sample => sample.PriceUsd > 0)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        if (ordered.Count == 0)
        {
            return Array.Empty<DexOhlcvPoint>();
        }

        var candles = new List<DexOhlcvPoint>();
        var bucketStart = AlignTime(fromUtc, bucketSize);
        var cursor = 0;
        var lastClose = ordered[0].PriceUsd;

        while (bucketStart <= toUtc && candles.Count < maxCandles)
        {
            var bucketEnd = bucketStart + bucketSize;
            var bucketSamples = new List<DexPriceSample>();

            while (cursor < ordered.Count && ordered[cursor].TimestampUtc < bucketEnd)
            {
                if (ordered[cursor].TimestampUtc >= bucketStart)
                {
                    bucketSamples.Add(ordered[cursor]);
                }

                cursor++;
            }

            if (bucketSamples.Count == 0)
            {
                candles.Add(new DexOhlcvPoint
                {
                    Timestamp = bucketStart.ToLocalTime(),
                    Open = lastClose,
                    High = lastClose,
                    Low = lastClose,
                    Close = lastClose,
                    Volume = 0
                });
            }
            else
            {
                var open = bucketSamples.First().PriceUsd;
                var close = bucketSamples.Last().PriceUsd;
                var high = bucketSamples.Max(sample => sample.PriceUsd);
                var low = bucketSamples.Min(sample => sample.PriceUsd);
                lastClose = close;

                candles.Add(new DexOhlcvPoint
                {
                    Timestamp = bucketStart.ToLocalTime(),
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = 0
                });
            }

            bucketStart = bucketEnd;
        }

        return candles.Count > maxCandles
            ? candles[^maxCandles..]
            : candles;
    }

    private static DateTime AlignTime(DateTime timestampUtc, TimeSpan bucketSize)
    {
        var ticks = bucketSize.Ticks == 0 ? 1 : bucketSize.Ticks;
        return new DateTime(timestampUtc.Ticks - (timestampUtc.Ticks % ticks), DateTimeKind.Utc);
    }
}
