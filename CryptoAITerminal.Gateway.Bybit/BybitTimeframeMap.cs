using System;
using Bybit.Net.Enums;
using CryptoAITerminal.Gateway.Base;

namespace CryptoAITerminal.Gateway.Bybit;

/// <summary>
/// Terminal timeframe token → Bybit <see cref="KlineInterval"/>. The token list itself lives in
/// <see cref="TimeframeParser"/>; only the SDK mapping is local, and a token Bybit has no interval
/// for throws instead of quietly becoming one-hour candles as the old <c>_ =&gt; OneHour</c> default did.
/// </summary>
internal static class BybitTimeframeMap
{
    /// <exception cref="ArgumentException">Unknown token, or one Bybit has no interval for (8H).</exception>
    public static KlineInterval Parse(string? timeframe)
    {
        // "ALL" is the chart's full-history button, not a timeframe: only the Binance gateway
        // paginates the whole series, so here it means "the coarsest interval one page covers".
        if (string.Equals(TimeframeParser.Normalize(timeframe), "ALL", StringComparison.Ordinal))
            return KlineInterval.OneDay;

        return (int)TimeframeParser.Parse(timeframe).TotalMinutes switch
        {
            1     => KlineInterval.OneMinute,
            3     => KlineInterval.ThreeMinutes,
            5     => KlineInterval.FiveMinutes,
            15    => KlineInterval.FifteenMinutes,
            30    => KlineInterval.ThirtyMinutes,
            60    => KlineInterval.OneHour,
            120   => KlineInterval.TwoHours,
            240   => KlineInterval.FourHours,
            360   => KlineInterval.SixHours,
            720   => KlineInterval.TwelveHours,
            1440  => KlineInterval.OneDay,
            10080 => KlineInterval.OneWeek,
            43200 => KlineInterval.OneMonth,
            _     => throw new ArgumentException(
                         $"Bybit has no kline interval for timeframe '{timeframe}'.", nameof(timeframe)),
        };
    }
}
