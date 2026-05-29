using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Bollinger Bands mean-reversion strategy.
///
/// BUY  when close drops below the lower band  (oversold).
/// SELL when close rises above the upper band  (overbought),
///      OR when close returns to the upper quarter of the band
///         after having been below the middle (partial profit).
///
/// Confidence scales with how far price is outside the band,
/// normalised by one standard deviation.
/// </summary>
public sealed class BollingerBandsStrategy : IStrategy
{
    private readonly Queue<decimal> _prices = new();
    private readonly int     _period;
    private readonly decimal _multiplier;

    public string Name => $"BB({_period}, {_multiplier:0.#}σ)";

    public BollingerBandsStrategy(int period = 20, decimal multiplier = 2.0m)
    {
        _period     = Math.Max(2, period);
        _multiplier = multiplier;
    }

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        _prices.Enqueue(data.LastPrice);
        if (_prices.Count > _period) _prices.Dequeue();
        if (_prices.Count < _period) return ("HOLD", 0m);

        var arr    = _prices.ToArray();
        var sma    = arr.Average();
        var stdDev = StdDev(arr, sma);

        if (stdDev == 0m) return ("HOLD", 0m);

        var upper = sma + _multiplier * stdDev;
        var lower = sma - _multiplier * stdDev;
        var close = data.LastPrice;

        // Price below lower band → BUY (oversold)
        if (close < lower)
        {
            var dist = (lower - close) / stdDev;
            return ("BUY", Math.Min(0.95m, 0.50m + dist * 0.25m));
        }

        // Price above upper band → SELL (overbought)
        if (close > upper)
        {
            var dist = (close - upper) / stdDev;
            return ("SELL", Math.Min(0.95m, 0.50m + dist * 0.25m));
        }

        // Price climbed back into the upper quarter of the band → mean-reversion SELL
        if (upper > lower)
        {
            var bandPos = (close - lower) / (upper - lower); // 0..1
            if (bandPos >= 0.75m)
            {
                var conf = Math.Min(0.85m, 0.35m + (bandPos - 0.75m) * 2.0m);
                return ("SELL", conf);
            }
        }

        return ("HOLD", 0m);
    }

    public void Reset() => _prices.Clear();

    private static decimal StdDev(decimal[] arr, decimal mean)
    {
        var variance = arr.Sum(p => (p - mean) * (p - mean)) / arr.Length;
        return (decimal)Math.Sqrt((double)variance);
    }
}
