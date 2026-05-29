using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Donchian-channel breakout strategy (trend-following).
///
/// Maintains a rolling window of <see cref="_period"/> candles.
/// BUY  when close > previous-period highest high  (upside breakout).
/// SELL when close &lt; previous-period lowest  low   (downside breakout / exit).
///
/// Confidence scales with how far the breakout exceeds the channel,
/// normalised by the channel range (≈ ATR proxy).
///
/// High/Low are read from <see cref="MarketData.High24h"/> /
/// <see cref="MarketData.Low24h"/>, which the <see cref="BacktestEngine"/>
/// populates from the candle's own High/Low fields.
/// Falls back to LastPrice when those fields are zero (live streaming).
/// </summary>
public sealed class BreakoutStrategy : IStrategy
{
    private readonly Queue<decimal> _highs = new();
    private readonly Queue<decimal> _lows  = new();
    private readonly int _period;

    public string Name => $"Breakout({_period})";

    public BreakoutStrategy(int period = 20)
    {
        _period = Math.Max(2, period);
    }

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        var candleHigh = data.High24h > 0 ? data.High24h : data.LastPrice;
        var candleLow  = data.Low24h  > 0 ? data.Low24h  : data.LastPrice;

        _highs.Enqueue(candleHigh);
        _lows .Enqueue(candleLow);

        if (_highs.Count > _period) { _highs.Dequeue(); _lows.Dequeue(); }
        if (_highs.Count < _period) return ("HOLD", 0m);

        // "Previous period" = all but the current candle
        var prevHighs = _highs.SkipLast(1).ToArray();
        var prevLows  = _lows .SkipLast(1).ToArray();
        if (prevHighs.Length == 0) return ("HOLD", 0m);

        var resistance = prevHighs.Max();
        var support    = prevLows .Min();
        var range      = resistance - support;
        var close      = data.LastPrice;

        if (range <= 0m) return ("HOLD", 0m);

        // Upside breakout
        if (close > resistance)
        {
            var strength = (close - resistance) / range;
            return ("BUY", Math.Min(0.95m, 0.50m + strength * 3m));
        }

        // Downside breakout (exit long / reversal)
        if (close < support)
        {
            var strength = (support - close) / range;
            return ("SELL", Math.Min(0.95m, 0.50m + strength * 3m));
        }

        return ("HOLD", 0m);
    }

    public void Reset() { _highs.Clear(); _lows.Clear(); }
}
