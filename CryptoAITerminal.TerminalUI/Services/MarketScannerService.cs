using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Aggregates live market data from all connected gateways, computes RSI(14),
/// activity scores and 24-h stats, then periodically publishes filtered ScanResult lists.
/// Thread-safe for multi-gateway subscriptions.
/// </summary>
public sealed class MarketScannerService : IDisposable
{
    // ── Per-symbol internal state ──────────────────────────────────────────────

    private sealed class SymbolState
    {
        public string  Exchange       = "";
        public decimal LastPrice;
        public decimal Volume24hUsd;
        public decimal ChangePct24h;
        public decimal High24h;
        public decimal Low24h;
        public DateTime UpdatedAt    = DateTime.MinValue;

        // Rolling price buffer: snapshot taken every SnapshotIntervalSec
        public readonly Queue<decimal> PriceHistory = new(MaxHistory + 1);

        // Tick counter for activity score (last 60 s)
        public readonly Queue<DateTime> RecentTicks = new(600);

        public DateTime LastSnapshotAt = DateTime.MinValue;
    }

    private const int MaxHistory         = 20;   // snapshots — 20 × 10 s = 200 s of history
    private const int SnapshotIntervalSec = 10;
    private const int RsiPeriods         = 14;

    private readonly ConcurrentDictionary<string, SymbolState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _subs = [];

    // ── Alert infrastructure ───────────────────────────────────────────────────

    private readonly List<PriceLevel> _levels = [];
    private readonly ConcurrentDictionary<string, DateTime> _alertCooldowns = new();
    private const int AlertCooldownSec = 60;

    // ── Timer ──────────────────────────────────────────────────────────────────

    private readonly Timer _scanTimer;

    // ── Public surface ─────────────────────────────────────────────────────────

    public event Action<IReadOnlyList<ScanResult>>? ResultsUpdated;
    public event Action<ScannerAlert>?              AlertFired;

    // ── Config ─────────────────────────────────────────────────────────────────

    public decimal RsiOversoldThreshold    { get; set; } = 30m;
    public decimal RsiOverboughtThreshold  { get; set; } = 70m;
    public decimal BigMoveThresholdPct     { get; set; } = 5m;   // |24h %| ≥ this → Hot
    public decimal HighActivityThreshold   { get; set; } = 10m;  // ticks/min in last 60s

    // ── Constructor ───────────────────────────────────────────────────────────

    public MarketScannerService(params IExchangeGateway?[] gateways)
    {
        string[] names = ["Binance", "Bybit", "OKX"];
        for (int i = 0; i < gateways.Length && i < names.Length; i++)
        {
            if (gateways[i] is null) continue;
            var name = names[i];
            var sub  = gateways[i]!.MarketDataStream.Subscribe(md => OnTick(name, md));
            _subs.Add(sub);
        }

        _scanTimer = new Timer(_ => Scan(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    // ── Tick handler ──────────────────────────────────────────────────────────

    private void OnTick(string exchange, MarketData md)
    {
        if (md.LastPrice <= 0) return;

        var state = _states.GetOrAdd(md.Symbol, _ => new SymbolState());
        lock (state)
        {
            state.Exchange    = exchange;
            state.LastPrice   = md.LastPrice;
            state.ChangePct24h = md.ChangePct24h;
            state.High24h     = md.High24h;
            state.Low24h      = md.Low24h;
            state.UpdatedAt   = md.Timestamp;

            // Only update volume when the stream provides it (avoid overwriting with 0)
            if (md.Volume24hUsd > 0) state.Volume24hUsd = md.Volume24hUsd;

            // Activity tick
            var now = DateTime.UtcNow;
            state.RecentTicks.Enqueue(now);
            // Trim old ticks (> 60 s ago)
            while (state.RecentTicks.Count > 0 &&
                   (now - state.RecentTicks.Peek()).TotalSeconds > 60)
                state.RecentTicks.Dequeue();

            // Price snapshot for RSI
            if ((now - state.LastSnapshotAt).TotalSeconds >= SnapshotIntervalSec)
            {
                state.PriceHistory.Enqueue(md.LastPrice);
                if (state.PriceHistory.Count > MaxHistory)
                    state.PriceHistory.Dequeue();
                state.LastSnapshotAt = now;
            }
        }
    }

    // ── Scan tick ─────────────────────────────────────────────────────────────

    private void Scan()
    {
        if (_states.IsEmpty) return;

        var results = new List<ScanResult>(_states.Count);

        foreach (var (sym, state) in _states)
        {
            ScanResult result;
            lock (state)
            {
                if ((DateTime.UtcNow - state.UpdatedAt).TotalSeconds > 30) continue;

                var rsi      = ComputeRsi(state.PriceHistory.ToArray());
                var activity = state.RecentTicks.Count;   // ticks in last 60 s

                var isHot = Math.Abs(state.ChangePct24h) >= BigMoveThresholdPct
                            || activity >= HighActivityThreshold
                            || rsi <= RsiOversoldThreshold
                            || rsi >= RsiOverboughtThreshold;

                result = new ScanResult
                {
                    Symbol        = sym,
                    Exchange      = state.Exchange,
                    LastPrice     = state.LastPrice,
                    High24h       = state.High24h,
                    Low24h        = state.Low24h,
                    ChangePct24h  = state.ChangePct24h,
                    Volume24hUsd  = state.Volume24hUsd,
                    Rsi14         = rsi,
                    ActivityScore = activity,
                    IsHot         = isHot,
                    UpdatedAt     = state.UpdatedAt,
                };
            }

            results.Add(result);
            CheckAlerts(result);
        }

        // Sort: hot first, then by |change%| descending
        results.Sort((a, b) =>
        {
            if (a.IsHot != b.IsHot) return a.IsHot ? -1 : 1;
            return Math.Abs(b.ChangePct24h).CompareTo(Math.Abs(a.ChangePct24h));
        });

        ResultsUpdated?.Invoke(results);
    }

    // ── Alert detection ───────────────────────────────────────────────────────

    private void CheckAlerts(ScanResult r)
    {
        TryFireAlert(r, $"rsi_low_{r.Symbol}",
            r.Rsi14 > 0 && r.Rsi14 <= RsiOversoldThreshold,
            AlertSeverity.Hot,
            $"RSI {r.Rsi14:F0} — перепродан 💎");

        TryFireAlert(r, $"rsi_high_{r.Symbol}",
            r.Rsi14 > 0 && r.Rsi14 >= RsiOverboughtThreshold,
            AlertSeverity.Warning,
            $"RSI {r.Rsi14:F0} — перекуплен ⚠");

        TryFireAlert(r, $"bigmove_up_{r.Symbol}",
            r.ChangePct24h >= BigMoveThresholdPct,
            AlertSeverity.Hot,
            $"+{r.ChangePct24h:F2}% за 24ч 🚀");

        TryFireAlert(r, $"bigmove_dn_{r.Symbol}",
            r.ChangePct24h <= -BigMoveThresholdPct,
            AlertSeverity.Warning,
            $"{r.ChangePct24h:F2}% за 24ч 🔻");

        TryFireAlert(r, $"activity_{r.Symbol}",
            r.ActivityScore >= HighActivityThreshold * 2,
            AlertSeverity.Info,
            $"Активность ×{r.ActivityScore / Math.Max(1, HighActivityThreshold):F1} от нормы 🔥");

        // Level breaks
        foreach (var lvl in _levels)
        {
            if (!string.Equals(lvl.Symbol, r.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (lvl.Triggered) continue;

            bool crossed = lvl.IsResistance
                ? r.LastPrice >= lvl.Price
                : r.LastPrice <= lvl.Price;

            if (!crossed) continue;

            lvl.Triggered   = true;
            lvl.TriggeredAt = DateTime.UtcNow;
            var kind = lvl.IsResistance ? "сопротивление" : "поддержка";
            FireAlert(r, AlertSeverity.Hot, $"Пробой {kind} ${lvl.Price:N2}" +
                (string.IsNullOrEmpty(lvl.Note) ? "" : $" ({lvl.Note})"));
        }
    }

    private void TryFireAlert(ScanResult r, string key, bool condition,
                               AlertSeverity sev, string message)
    {
        if (!condition) return;
        var now = DateTime.UtcNow;
        if (_alertCooldowns.TryGetValue(key, out var last) &&
            (now - last).TotalSeconds < AlertCooldownSec) return;
        _alertCooldowns[key] = now;
        FireAlert(r, sev, message);
    }

    private void FireAlert(ScanResult r, AlertSeverity sev, string message) =>
        AlertFired?.Invoke(new ScannerAlert
        {
            Symbol      = r.Symbol,
            Exchange    = r.Exchange,
            Severity    = sev,
            Message     = message,
            TriggeredAt = DateTime.UtcNow,
        });

    // ── Price level management ─────────────────────────────────────────────────

    public void AddPriceLevel(PriceLevel level)
    {
        lock (_levels) _levels.Add(level);
    }

    public void RemovePriceLevel(Guid id)
    {
        lock (_levels) _levels.RemoveAll(l => l.Id == id);
    }

    public IReadOnlyList<PriceLevel> GetLevels()
    {
        lock (_levels) return _levels.ToList();
    }

    // ── RSI computation ───────────────────────────────────────────────────────

    private static decimal ComputeRsi(decimal[] prices)
    {
        if (prices.Length < RsiPeriods + 1) return 0m;   // not enough data yet

        decimal gains = 0, losses = 0;
        int start = prices.Length - RsiPeriods;
        for (int i = start; i < prices.Length; i++)
        {
            var diff = prices[i] - prices[i - 1];
            if (diff > 0) gains  += diff;
            else          losses -= diff;
        }

        var avgGain = gains  / RsiPeriods;
        var avgLoss = losses / RsiPeriods;
        if (avgLoss == 0) return 100m;

        var rs = avgGain / avgLoss;
        return Math.Round(100m - 100m / (1 + rs), 1);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _scanTimer.Dispose();
        foreach (var s in _subs) s.Dispose();
    }
}
