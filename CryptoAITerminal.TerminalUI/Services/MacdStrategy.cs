using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// MACD (Moving Average Convergence Divergence) momentum strategy.
///
/// macdLine    = EMA(fast) − EMA(slow)
/// signalLine  = EMA(macdLine, signalPeriod)
/// histogram   = macdLine − signalLine
///
/// BUY  when histogram crosses up through zero (MACD crosses above signal).
/// SELL when histogram crosses down through zero (MACD crosses below signal).
/// Confidence scales with the magnitude of the histogram normalised by EMA(slow).
/// </summary>
public sealed class MacdStrategy : IStrategy
{
    private readonly int _fastPeriod;
    private readonly int _slowPeriod;
    private readonly int _signalPeriod;

    private decimal _emaFast;
    private decimal _emaSlow;
    private decimal _emaSignal;
    private decimal? _prevHistogram;
    private int _samples;

    public string Name => $"MACD({_fastPeriod}/{_slowPeriod}/{_signalPeriod})";

    public MacdStrategy(int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9)
    {
        _fastPeriod   = Math.Max(2, fastPeriod);
        _slowPeriod   = Math.Max(_fastPeriod + 1, slowPeriod);
        _signalPeriod = Math.Max(2, signalPeriod);
    }

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        var price = data.LastPrice;
        _samples++;

        if (_samples == 1)
        {
            _emaFast = _emaSlow = _emaSignal = price;
            return ("HOLD", 0m);
        }

        _emaFast = Ema(_emaFast, price, _fastPeriod);
        _emaSlow = Ema(_emaSlow, price, _slowPeriod);

        if (_samples < _slowPeriod) return ("HOLD", 0m);

        var macdLine  = _emaFast - _emaSlow;
        _emaSignal    = _samples == _slowPeriod ? macdLine : Ema(_emaSignal, macdLine, _signalPeriod);
        var histogram = macdLine - _emaSignal;

        if (_samples < _slowPeriod + _signalPeriod || !_prevHistogram.HasValue)
        {
            _prevHistogram = histogram;
            return ("HOLD", 0m);
        }

        string signal = "HOLD";
        decimal confidence = 0m;

        // Zero-line cross of the histogram = MACD/signal cross.
        if (_prevHistogram.Value <= 0m && histogram > 0m)
        {
            signal     = "BUY";
            confidence = ScaleConfidence(histogram);
        }
        else if (_prevHistogram.Value >= 0m && histogram < 0m)
        {
            signal     = "SELL";
            confidence = ScaleConfidence(-histogram);
        }

        _prevHistogram = histogram;
        return (signal, confidence);
    }

    public void Reset()
    {
        _emaFast = _emaSlow = _emaSignal = 0m;
        _prevHistogram = null;
        _samples = 0;
    }

    private static decimal Ema(decimal previous, decimal value, int period)
    {
        var k = 2m / (period + 1);
        return previous + k * (value - previous);
    }

    private decimal ScaleConfidence(decimal histAbs)
    {
        if (_emaSlow == 0m) return 0.55m;
        var normalised = histAbs / _emaSlow * 1000m;
        return Math.Clamp(0.50m + normalised * 0.40m, 0.50m, 0.95m);
    }
}
