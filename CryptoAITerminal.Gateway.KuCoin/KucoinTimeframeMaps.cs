using System;
using Kucoin.Net.Enums;
using CryptoAITerminal.Gateway.Base;

namespace CryptoAITerminal.Gateway.KuCoin;

/// <summary>
/// Terminal timeframe token → KuCoin spot <see cref="KlineInterval"/>. The token list itself lives
/// in <see cref="TimeframeParser"/>; only the SDK mapping is local, and anything the venue cannot
/// serve throws instead of quietly becoming one-hour candles as the old <c>_ =&gt; OneHour</c> default did.
/// </summary>
internal static class KucoinSpotTimeframeMap
{
    /// <exception cref="ArgumentException">Unknown token.</exception>
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
            480   => KlineInterval.EightHours,
            720   => KlineInterval.TwelveHours,
            1440  => KlineInterval.OneDay,
            10080 => KlineInterval.OneWeek,
            43200 => KlineInterval.OneMonth,
            _     => throw new ArgumentException(
                         $"KuCoin spot has no kline interval for timeframe '{timeframe}'.", nameof(timeframe)),
        };
    }
}

/// <summary>
/// Terminal timeframe token → KuCoin futures <see cref="FuturesKlineInterval"/>. The futures API
/// is narrower than the spot one — no 3M, 6H or monthly candles — so those tokens throw rather
/// than being served as something else.
/// </summary>
internal static class KucoinFuturesTimeframeMap
{
    /// <exception cref="ArgumentException">Unknown token, or one KuCoin futures has no interval for.</exception>
    public static FuturesKlineInterval Parse(string? timeframe)
    {
        if (string.Equals(TimeframeParser.Normalize(timeframe), "ALL", StringComparison.Ordinal))
            return FuturesKlineInterval.OneDay;

        return (int)TimeframeParser.Parse(timeframe).TotalMinutes switch
        {
            1     => FuturesKlineInterval.OneMinute,
            5     => FuturesKlineInterval.FiveMinutes,
            15    => FuturesKlineInterval.FifteenMinutes,
            30    => FuturesKlineInterval.ThirtyMinutes,
            60    => FuturesKlineInterval.OneHour,
            120   => FuturesKlineInterval.TwoHours,
            240   => FuturesKlineInterval.FourHours,
            480   => FuturesKlineInterval.EightHours,
            720   => FuturesKlineInterval.TwelveHours,
            1440  => FuturesKlineInterval.OneDay,
            10080 => FuturesKlineInterval.OneWeek,
            _     => throw new ArgumentException(
                         $"KuCoin futures has no kline interval for timeframe '{timeframe}'.", nameof(timeframe)),
        };
    }
}
