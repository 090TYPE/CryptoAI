using System;
using Binance.Net.Enums;
using CryptoAITerminal.Gateway.Base;

namespace CryptoAITerminal.Gateway.Binance;

/// <summary>
/// Terminal timeframe token → Binance <see cref="KlineInterval"/>. Both Binance gateways used to
/// carry the same inline switch with <c>_ =&gt; KlineInterval.OneMinute</c> at the bottom, so a token
/// neither copy knew silently produced one-minute candles — wrong data that looks right on the
/// chart and poisons every indicator computed from it. Parsing is shared now, and anything the
/// venue cannot serve throws.
/// </summary>
internal static class BinanceTimeframeMap
{
    /// <exception cref="ArgumentException">Unknown token, or one Binance has no interval for.</exception>
    public static KlineInterval Parse(string? timeframe) =>
        (int)TimeframeParser.Parse(timeframe).TotalMinutes switch
        {
            1     => KlineInterval.OneMinute,
            3     => KlineInterval.ThreeMinutes,
            5     => KlineInterval.FiveMinutes,
            15    => KlineInterval.FifteenMinutes,
            30    => KlineInterval.ThirtyMinutes,
            60    => KlineInterval.OneHour,
            120   => KlineInterval.TwoHour,
            240   => KlineInterval.FourHour,
            360   => KlineInterval.SixHour,
            480   => KlineInterval.EightHour,
            720   => KlineInterval.TwelveHour,
            1440  => KlineInterval.OneDay,
            10080 => KlineInterval.OneWeek,
            43200 => KlineInterval.OneMonth,
            _     => throw new ArgumentException(
                         $"Binance has no kline interval for timeframe '{timeframe}'.", nameof(timeframe)),
        };
}
