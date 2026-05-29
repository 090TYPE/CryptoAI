using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// RSI mean-reversion strategy.
/// BUY  when RSI ≤ OversoldLevel  (price is oversold).
/// SELL when RSI ≥ OverboughtLevel (price is overbought).
/// Confidence scales linearly with distance from the threshold.
///
/// Uses Wilder's smoothing (the original 1978 definition) — matches TradingView,
/// Binance and other platforms. A simple SMA over the window gives different
/// numbers and makes backtests incomparable with live setups.
/// </summary>
public sealed class RsiStrategy : IStrategy
{
    private readonly int     _period;
    private readonly decimal _overbought;
    private readonly decimal _oversold;

    // Initial seed window — used until we have `period+1` prices for the first SMA.
    private readonly Queue<decimal> _seedPrices = new();

    // Wilder smoothed state — populated after the seed window.
    private decimal _prevPrice;
    private decimal _avgGain;
    private decimal _avgLoss;
    private bool    _seeded;

    public string Name =>
        $"RSI({_period}, OS={_oversold:0}, OB={_overbought:0})";

    public RsiStrategy(int period = 14, decimal overbought = 70m, decimal oversold = 30m)
    {
        _period     = Math.Max(2, period);
        _overbought = overbought;
        _oversold   = oversold;
    }

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        var rsi = UpdateAndCompute(data.LastPrice);
        if (!rsi.HasValue) return ("HOLD", 0m);

        var value = rsi.Value;

        if (value <= _oversold)
        {
            var depth      = (_oversold - value) / Math.Max(1m, _oversold);
            var confidence = Math.Min(0.95m, 0.50m + depth * 0.45m);
            return ("BUY", confidence);
        }

        if (value >= _overbought)
        {
            var depth      = (value - _overbought) / Math.Max(1m, 100m - _overbought);
            var confidence = Math.Min(0.95m, 0.50m + depth * 0.45m);
            return ("SELL", confidence);
        }

        return ("HOLD", 0m);
    }

    public void Reset()
    {
        _seedPrices.Clear();
        _prevPrice = 0m;
        _avgGain = 0m;
        _avgLoss = 0m;
        _seeded = false;
    }

    private decimal? UpdateAndCompute(decimal price)
    {
        if (!_seeded)
        {
            _seedPrices.Enqueue(price);
            if (_seedPrices.Count < _period + 1) return null;

            // Seed: first averages are simple means of gains/losses over `period` deltas.
            var arr = _seedPrices.ToArray();
            decimal gainSum = 0m, lossSum = 0m;
            for (int i = 1; i < arr.Length; i++)
            {
                var delta = arr[i] - arr[i - 1];
                if (delta > 0) gainSum += delta;
                else           lossSum -= delta;
            }
            _avgGain   = gainSum / _period;
            _avgLoss   = lossSum / _period;
            _prevPrice = arr[^1];
            _seeded    = true;
            _seedPrices.Clear();
        }
        else
        {
            var delta = price - _prevPrice;
            var gain  = delta > 0 ? delta : 0m;
            var loss  = delta < 0 ? -delta : 0m;

            // Wilder's smoothing: α = 1 / period
            _avgGain   = (_avgGain * (_period - 1) + gain) / _period;
            _avgLoss   = (_avgLoss * (_period - 1) + loss) / _period;
            _prevPrice = price;
        }

        if (_avgLoss == 0m) return _avgGain == 0m ? 50m : 100m;
        var rs = _avgGain / _avgLoss;
        return 100m - 100m / (1m + rs);
    }
}
