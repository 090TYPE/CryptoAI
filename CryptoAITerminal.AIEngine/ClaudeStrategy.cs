using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// IStrategy adapter that delegates the buy/sell verdict to Claude.
///
/// MarketData is a streaming tick — Claude is a slow, expensive oracle, so we
/// aggregate ticks into synthetic 1-minute candles and only call the API once
/// per <see cref="_minPollInterval"/> (default 1 min). Between calls we replay
/// the last verdict so the bot keeps acting on a coherent signal.
///
/// The reason text from the model is surfaced via <see cref="LastReason"/> so
/// the UI can display "why" alongside each signal.
/// </summary>
public sealed class ClaudeStrategy : IStrategy
{
    private readonly ClaudeSignalProvider _provider;
    private readonly string _symbol;
    private readonly TimeSpan _minPollInterval;
    private readonly int _candleHistory;
    private readonly Action<string>? _logger;

    private readonly List<ClaudeCandle> _candles = new();
    private DateTime? _currentMinuteBucket;
    private ClaudeCandle _currentCandle;
    private bool _hasCurrentCandle;

    private DateTime _lastQueryUtc = DateTime.MinValue;
    private (string Signal, decimal Confidence) _lastVerdict = ("HOLD", 0m);

    // Single-flight: never run two Claude calls concurrently for the same strategy.
    private int _inFlight;

    public string Name => "Claude (AI verdict)";
    public string LastReason { get; private set; } = "";

    public ClaudeStrategy(
        ClaudeSignalProvider provider,
        string symbol,
        TimeSpan? minPollInterval = null,
        int candleHistory = 30,
        Action<string>? logger = null)
    {
        _provider         = provider ?? throw new ArgumentNullException(nameof(provider));
        _symbol           = symbol;
        _minPollInterval  = minPollInterval ?? TimeSpan.FromMinutes(1);
        _candleHistory    = Math.Max(5, candleHistory);
        _logger           = logger;
    }

    public (string Signal, decimal Confidence) Analyze(MarketData data)
    {
        AppendTickToCandle(data);

        // Throttle: even if the model is slow, we never queue multiple calls.
        if (DateTime.UtcNow - _lastQueryUtc < _minPollInterval) return _lastVerdict;
        if (Interlocked.Exchange(ref _inFlight, 1) == 1)        return _lastVerdict;

        _lastQueryUtc = DateTime.UtcNow;
        _ = Task.Run(QueryAsync);
        return _lastVerdict;
    }

    public void Reset()
    {
        _candles.Clear();
        _currentMinuteBucket = null;
        _hasCurrentCandle    = false;
        _lastVerdict         = ("HOLD", 0m);
        _lastQueryUtc        = DateTime.MinValue;
        LastReason           = "";
    }

    private void AppendTickToCandle(MarketData tick)
    {
        var bucket = new DateTime(tick.Timestamp.Year, tick.Timestamp.Month, tick.Timestamp.Day,
                                  tick.Timestamp.Hour, tick.Timestamp.Minute, 0, DateTimeKind.Utc);

        if (!_hasCurrentCandle || _currentMinuteBucket != bucket)
        {
            // Roll the previous bucket into history.
            if (_hasCurrentCandle)
            {
                _candles.Add(_currentCandle);
                if (_candles.Count > _candleHistory)
                    _candles.RemoveRange(0, _candles.Count - _candleHistory);
            }

            _currentMinuteBucket = bucket;
            _currentCandle = new ClaudeCandle(bucket,
                Open: tick.LastPrice, High: tick.LastPrice,
                Low: tick.LastPrice,  Close: tick.LastPrice, Volume: 0m);
            _hasCurrentCandle = true;
            return;
        }

        _currentCandle = _currentCandle with
        {
            High  = Math.Max(_currentCandle.High, tick.LastPrice),
            Low   = Math.Min(_currentCandle.Low,  tick.LastPrice),
            Close = tick.LastPrice,
            // No real per-tick volume on the stream — best-effort accumulation
            // from the 24h field would mislead, so we leave it at 0.
            Volume = _currentCandle.Volume
        };
    }

    private async Task QueryAsync()
    {
        try
        {
            var snapshot = new List<ClaudeCandle>(_candles);
            if (_hasCurrentCandle) snapshot.Add(_currentCandle);
            if (snapshot.Count < 5) return;

            var result = await _provider.GetSignalAsync(_symbol, snapshot).ConfigureAwait(false);
            if (result is null) return;

            _lastVerdict = (result.Signal, result.Confidence);
            LastReason   = result.Reason;
            _logger?.Invoke($"[Claude] {result.Signal} conf={result.Confidence:P0} — {result.Reason}");
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[Claude error] {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }
}
