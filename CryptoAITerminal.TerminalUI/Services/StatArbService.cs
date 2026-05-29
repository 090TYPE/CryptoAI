using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Statistical arbitrage (pairs trading) service.
/// Monitors the log-ratio spread between two symbols on the same exchange.
/// Opens a delta-neutral position when the z-score exceeds EntryZScore,
/// and closes when it reverts below ExitZScore.
/// Uses Futures (short leg) + Spot (long leg) or two Futures legs.
/// </summary>
public sealed class StatArbService : IDisposable
{
    private readonly IExchangeGateway _gateway;

    private StatArbConfig _cfg;
    private decimal _priceA;
    private decimal _priceB;
    private decimal _hedgeRatio = 1m;

    private readonly Queue<decimal> _spreadHistory = new();

    public StatArbPosition? CurrentPosition { get; private set; }
    public decimal CurrentZScore { get; private set; }
    public decimal CurrentSpread { get; private set; }
    public IReadOnlyList<SpreadPoint> SpreadHistory => _spreadHistory.ToArray().Select(
        (s, i) => new SpreadPoint(DateTime.UtcNow.AddSeconds(-(_spreadHistory.Count - i)), s, 0)).ToList();

    public bool IsRunning { get; private set; }
    public decimal TotalPnlUsd => CurrentPosition?.PnlUsd ?? 0m;

    public event Action<string>?    LogMessage;
    public event Action?            StateChanged;

    private IDisposable? _subA;
    private IDisposable? _subB;
    private CancellationTokenSource? _cts;

    public StatArbService(IExchangeGateway gateway, StatArbConfig cfg)
    {
        _gateway = gateway;
        _cfg     = cfg;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();

        Log($"Starting StatArb: {_cfg.SymbolA} / {_cfg.SymbolB}  Window={_cfg.Window}  EntryZ={_cfg.EntryZScore}");

        // Seed spread history from candles
        await SeedSpreadHistoryAsync(_cts.Token);

        // Subscribe to live price feed
        _subA = _gateway.MarketDataStream
            .Where(m => m.Symbol == _cfg.SymbolA && m.LastPrice > 0)
            .Subscribe(m => { _priceA = m.LastPrice; OnNewPrices(); });

        _subB = _gateway.MarketDataStream
            .Where(m => m.Symbol == _cfg.SymbolB && m.LastPrice > 0)
            .Subscribe(m => { _priceB = m.LastPrice; OnNewPrices(); });

        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        _subA?.Dispose();
        _subB?.Dispose();
        _subA = _subB = null;

        if (CurrentPosition is not null)
        {
            Log("Stopping — closing open position...");
            await ClosePositionAsync();
        }

        StateChanged?.Invoke();
    }

    // ── Price logic ───────────────────────────────────────────────────────────

    private void OnNewPrices()
    {
        if (_priceA <= 0 || _priceB <= 0) return;

        // spread = ln(priceA) - hedgeRatio × ln(priceB)
        var spread = (decimal)Math.Log((double)_priceA)
                   - _hedgeRatio * (decimal)Math.Log((double)_priceB);

        _spreadHistory.Enqueue(spread);
        if (_spreadHistory.Count > _cfg.Window * 2)
            _spreadHistory.Dequeue();

        CurrentSpread = spread;
        CurrentZScore = ComputeZScore(spread);

        if (CurrentPosition is not null)
            CurrentPosition.CurrentZScore = CurrentZScore;

        _ = EvaluateSignalAsync();

        StateChanged?.Invoke();
    }

    private decimal ComputeZScore(decimal spread)
    {
        var arr = _spreadHistory.ToArray();
        if (arr.Length < 5) return 0m;
        var window = arr.TakeLast(Math.Min(_cfg.Window, arr.Length)).ToArray();
        var mean   = window.Average();
        var std    = (decimal)Math.Sqrt((double)window.Select(s => (s - mean) * (s - mean)).Average());
        return std > 0 ? (spread - mean) / std : 0m;
    }

    private async Task EvaluateSignalAsync()
    {
        // Close signal
        if (CurrentPosition is not null)
        {
            if (Math.Abs(CurrentZScore) < _cfg.ExitZScore)
            {
                Log($"Z-score {CurrentZScore:+0.00;-0.00} reverted — closing position");
                await ClosePositionAsync();
            }
            return;
        }

        // Entry signal
        if (CurrentZScore >= _cfg.EntryZScore)
        {
            Log($"Z-score {CurrentZScore:+0.00} — A overpriced: Short {_cfg.SymbolA}, Long {_cfg.SymbolB}");
            await OpenPositionAsync(StatArbPositionDirection.LongBShortA);
        }
        else if (CurrentZScore <= -_cfg.EntryZScore)
        {
            Log($"Z-score {CurrentZScore:-0.00} — B overpriced: Long {_cfg.SymbolA}, Short {_cfg.SymbolB}");
            await OpenPositionAsync(StatArbPositionDirection.LongAShortB);
        }
    }

    // ── Position management ───────────────────────────────────────────────────

    private async Task OpenPositionAsync(StatArbPositionDirection dir)
    {
        if (CurrentPosition is not null) return;
        if (_priceA <= 0 || _priceB <= 0) return;

        var notional = _cfg.NotionalUsd;
        var qtyA = Math.Round(notional / _priceA, 6);
        var qtyB = Math.Round(notional / _priceB, 6);

        try
        {
            // Long leg: Buy market; Short leg: Sell futures market
            var longSym  = dir == StatArbPositionDirection.LongAShortB ? _cfg.SymbolA : _cfg.SymbolB;
            var shortSym = dir == StatArbPositionDirection.LongAShortB ? _cfg.SymbolB : _cfg.SymbolA;
            var longQty  = dir == StatArbPositionDirection.LongAShortB ? qtyA : qtyB;
            var shortQty = dir == StatArbPositionDirection.LongAShortB ? qtyB : qtyA;

            await _gateway.PlaceOrderAsync(new Order
            {
                Symbol = longSym, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = longQty
            });
            await _gateway.PlaceOrderAsync(new Order
            {
                Symbol       = shortSym,
                Side         = OrderSide.Sell,
                Type         = OrderType.Market,
                Quantity     = shortQty,
                PositionSide = FuturesPositionSide.Short,
            });

            CurrentPosition = new StatArbPosition
            {
                SymbolA      = _cfg.SymbolA,
                SymbolB      = _cfg.SymbolB,
                Direction    = dir,
                QtyA         = qtyA,
                QtyB         = qtyB,
                EntryPriceA  = _priceA,
                EntryPriceB  = _priceB,
                EntrySpread  = CurrentSpread,
                EntryZScore  = CurrentZScore,
                CurrentZScore = CurrentZScore,
                CurrentPriceA = _priceA,
                CurrentPriceB = _priceB,
            };

            Log($"Opened {dir}: {longSym}↑ {shortSym}↓  NotionalUsd≈{notional:N0}");
        }
        catch (Exception ex)
        {
            Log($"Open failed: {ex.Message}");
        }

        StateChanged?.Invoke();
    }

    private async Task ClosePositionAsync()
    {
        if (CurrentPosition is null) return;
        var pos = CurrentPosition;
        CurrentPosition = null;

        try
        {
            var longSym  = pos.Direction == StatArbPositionDirection.LongAShortB ? pos.SymbolA : pos.SymbolB;
            var shortSym = pos.Direction == StatArbPositionDirection.LongAShortB ? pos.SymbolB : pos.SymbolA;
            var longQty  = pos.Direction == StatArbPositionDirection.LongAShortB ? pos.QtyA : pos.QtyB;
            var shortQty = pos.Direction == StatArbPositionDirection.LongAShortB ? pos.QtyB : pos.QtyA;

            await _gateway.PlaceOrderAsync(new Order
            {
                Symbol = longSym, Side = OrderSide.Sell, Type = OrderType.Market, Quantity = longQty
            });
            await _gateway.PlaceOrderAsync(new Order
            {
                Symbol     = shortSym, Side = OrderSide.Buy, Type = OrderType.Market,
                Quantity   = shortQty, ReduceOnly = true, PositionSide = FuturesPositionSide.Short,
            });

            Log($"Closed StatArb — P&L ≈ ${pos.PnlUsd:+0.00;-0.00}");
        }
        catch (Exception ex)
        {
            Log($"Close failed: {ex.Message}");
        }

        StateChanged?.Invoke();
    }

    // ── Seed history from candles ─────────────────────────────────────────────

    private async Task SeedSpreadHistoryAsync(CancellationToken ct)
    {
        try
        {
            var candlesA = await _gateway.GetCandlesAsync(_cfg.SymbolA, "1h", _cfg.Window * 2);
            var candlesB = await _gateway.GetCandlesAsync(_cfg.SymbolB, "1h", _cfg.Window * 2);

            var closesA = candlesA.Select(c => c.Close).ToArray();
            var closesB = candlesB.Select(c => c.Close).ToArray();
            var n       = Math.Min(closesA.Length, closesB.Length);
            if (n < 10) return;

            // Compute hedge ratio (beta = cov / var)
            var logA = closesA.TakeLast(n).Select(p => (decimal)Math.Log((double)p)).ToArray();
            var logB = closesB.TakeLast(n).Select(p => (decimal)Math.Log((double)p)).ToArray();
            _hedgeRatio = ComputeHedgeRatio(logA, logB);

            // Seed spread history
            for (int i = 0; i < n; i++)
                _spreadHistory.Enqueue(logA[i] - _hedgeRatio * logB[i]);

            Log($"Seeded {n} historical spreads  hedge_ratio={_hedgeRatio:N4}");
        }
        catch (Exception ex)
        {
            Log($"Could not seed history: {ex.Message} — starting with empty window");
        }
    }

    private static decimal ComputeHedgeRatio(decimal[] logA, decimal[] logB)
    {
        var n    = logA.Length;
        var meanA = logA.Average();
        var meanB = logB.Average();
        decimal cov = 0, varB = 0;
        for (int i = 0; i < n; i++)
        {
            cov  += (logA[i] - meanA) * (logB[i] - meanB);
            varB += (logB[i] - meanB) * (logB[i] - meanB);
        }
        return varB > 0 ? cov / varB : 1m;
    }

    private void Log(string msg) => LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public void Dispose()
    {
        _cts?.Cancel();
        _subA?.Dispose();
        _subB?.Dispose();
    }
}
