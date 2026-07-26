using System;
using System.Collections.Generic;

namespace CryptoAITerminal.Gateway.Base;

/// <summary>
/// Single source of truth for the timeframe tokens the UI passes to <c>GetCandlesAsync</c>.
/// Every gateway used to carry its own copy of the same switch, each with a silent default
/// (<c>OneHour</c> in most, <c>OneMinute</c> in Binance): a typo or a token the copy did not know
/// produced candles of the wrong interval instead of an error. Parse here, then map the resulting
/// <see cref="TimeSpan"/> to the SDK enum in the gateway — and throw on anything unknown.
/// </summary>
public static class TimeframeParser
{
    // "1MN" is the month token ("1M" is one minute, as everywhere in this app). TimeSpan has no
    // calendar month, so 30 days stands in — it is only ever used for ordering and comparison.
    private static readonly Dictionary<string, TimeSpan> Map = new(StringComparer.Ordinal)
    {
        ["1M"]  = TimeSpan.FromMinutes(1),
        ["3M"]  = TimeSpan.FromMinutes(3),
        ["5M"]  = TimeSpan.FromMinutes(5),
        ["15M"] = TimeSpan.FromMinutes(15),
        ["30M"] = TimeSpan.FromMinutes(30),
        ["1H"]  = TimeSpan.FromHours(1),
        ["2H"]  = TimeSpan.FromHours(2),
        ["4H"]  = TimeSpan.FromHours(4),
        ["6H"]  = TimeSpan.FromHours(6),
        ["8H"]  = TimeSpan.FromHours(8),
        ["12H"] = TimeSpan.FromHours(12),
        ["1D"]  = TimeSpan.FromDays(1),
        ["1W"]  = TimeSpan.FromDays(7),
        ["1MN"] = TimeSpan.FromDays(30),
    };

    /// <summary>Tokens this parser accepts, in canonical (upper-case) form.</summary>
    public static IReadOnlyCollection<string> SupportedTokens => Map.Keys;

    /// <summary>Trims and upper-cases the token so "4h", " 4H " and "4H" are one key.</summary>
    public static string Normalize(string? timeframe)
        => (timeframe ?? string.Empty).Trim().ToUpperInvariant();

    public static bool TryParse(string? timeframe, out TimeSpan interval)
        => Map.TryGetValue(Normalize(timeframe), out interval);

    /// <summary>
    /// Candle length for the token. Throws instead of guessing: a wrong interval is invisible in
    /// the chart but wrong in every indicator computed from it.
    /// </summary>
    /// <exception cref="ArgumentException">The token is not one of <see cref="SupportedTokens"/>.</exception>
    public static TimeSpan Parse(string? timeframe)
    {
        var key = Normalize(timeframe);
        if (Map.TryGetValue(key, out var interval)) return interval;
        throw new ArgumentException(
            $"Unknown timeframe '{timeframe}'. Supported: {string.Join(", ", Map.Keys)}.",
            nameof(timeframe));
    }
}
