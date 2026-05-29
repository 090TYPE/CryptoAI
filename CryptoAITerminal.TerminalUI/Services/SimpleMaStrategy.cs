using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

public class SimpleMaStrategy : IStrategy
{
    private readonly Queue<decimal> _prices = new();
    private readonly int _fastPeriod;
    private readonly int _slowPeriod;
    private decimal? _prevFastMa;
    private decimal? _prevSlowMa;

    public string Name => $"SMA({_fastPeriod}/{_slowPeriod})";

    public SimpleMaStrategy(int fastPeriod = 10, int slowPeriod = 30)
    {
        _fastPeriod = fastPeriod;
        _slowPeriod = slowPeriod;
    }

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        _prices.Enqueue(data.LastPrice);
        if (_prices.Count > _slowPeriod)
            _prices.Dequeue();

        if (_prices.Count < _slowPeriod)
            return ("HOLD", 0m);

        var pricesArray = _prices.ToArray();
        var fastMa = pricesArray.TakeLast(_fastPeriod).Average();
        var slowMa = pricesArray.Average();

        if (!_prevFastMa.HasValue || !_prevSlowMa.HasValue)
        {
            _prevFastMa = fastMa;
            _prevSlowMa = slowMa;
            return ("HOLD", 0m);
        }

        string signal = "HOLD";
        decimal confidence = 0m;

        if (_prevFastMa <= _prevSlowMa && fastMa > slowMa)
        {
            signal = "BUY";
            // Confidence растёт с силой расхождения MA
            var spread = (fastMa - slowMa) / slowMa;
            confidence = Math.Min(0.95m, 0.55m + spread * 80m);
        }
        else if (_prevFastMa >= _prevSlowMa && fastMa < slowMa)
        {
            signal = "SELL";
            var spread = (slowMa - fastMa) / slowMa;
            confidence = Math.Min(0.95m, 0.55m + spread * 80m);
        }

        _prevFastMa = fastMa;
        _prevSlowMa = slowMa;
        return (signal, confidence);
    }

    public void Reset()
    {
        _prices.Clear();
        _prevFastMa = null;
        _prevSlowMa = null;
    }
}
