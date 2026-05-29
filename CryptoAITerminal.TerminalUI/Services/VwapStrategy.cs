using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Globalization;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Volume-Weighted Average Price strategy.
///
/// Maintains a running session VWAP using the per-tick increment in 24h quote
/// volume as the weight (with a unit-weight fallback when the stream does not
/// expose volume). When price crosses above VWAP from below → BUY; crosses
/// below VWAP from above → SELL.
/// </summary>
public sealed class VwapStrategy : IStrategy
{
    private readonly decimal _bandThresholdPct;

    private decimal _cumPriceVolume;
    private decimal _cumVolume;
    private decimal _prevVolume24h;
    private decimal? _prevPrice;
    private decimal? _prevVwap;
    private int _samples;

    public string Name => string.Create(CultureInfo.InvariantCulture,
        $"VWAP(band={_bandThresholdPct:0.##}%)");

    /// <param name="bandThresholdPct">
    /// Minimum distance (percent of VWAP) by which the price must cross VWAP
    /// before a signal is emitted. Filters noise around the VWAP line.
    /// </param>
    public VwapStrategy(decimal bandThresholdPct = 0.05m)
    {
        _bandThresholdPct = Math.Max(0m, bandThresholdPct);
    }

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        var price = data.LastPrice;
        if (price <= 0m) return ("HOLD", 0m);

        _samples++;

        var weight = 0m;
        if (data.Volume24hUsd > 0m)
        {
            // Use the increment in 24h cumulative volume as the per-tick weight.
            // The 24h window rolls, so deltas can be negative; treat those as
            // missing data and fall back to unit weight.
            var delta = data.Volume24hUsd - _prevVolume24h;
            _prevVolume24h = data.Volume24hUsd;
            if (delta > 0m) weight = delta;
        }

        if (weight <= 0m) weight = 1m;

        _cumPriceVolume += price * weight;
        _cumVolume      += weight;

        if (_cumVolume <= 0m) return ("HOLD", 0m);

        var vwap = _cumPriceVolume / _cumVolume;

        if (_samples < 5 || !_prevPrice.HasValue || !_prevVwap.HasValue)
        {
            _prevPrice = price;
            _prevVwap  = vwap;
            return ("HOLD", 0m);
        }

        var band = vwap * _bandThresholdPct / 100m;

        string signal = "HOLD";
        decimal confidence = 0m;

        var wasBelow  = _prevPrice.Value < _prevVwap.Value - band;
        var wasAbove  = _prevPrice.Value > _prevVwap.Value + band;
        var nowAbove  = price > vwap + band;
        var nowBelow  = price < vwap - band;

        if (wasBelow && nowAbove)
        {
            signal     = "BUY";
            confidence = ScaleConfidence(price, vwap);
        }
        else if (wasAbove && nowBelow)
        {
            signal     = "SELL";
            confidence = ScaleConfidence(price, vwap);
        }

        _prevPrice = price;
        _prevVwap  = vwap;
        return (signal, confidence);
    }

    public void Reset()
    {
        _cumPriceVolume = 0m;
        _cumVolume = 0m;
        _prevVolume24h = 0m;
        _prevPrice = null;
        _prevVwap = null;
        _samples = 0;
    }

    private static decimal ScaleConfidence(decimal price, decimal vwap)
    {
        if (vwap <= 0m) return 0.55m;
        var distance = Math.Abs(price - vwap) / vwap * 100m;
        return Math.Clamp(0.50m + distance * 0.10m, 0.50m, 0.95m);
    }
}
