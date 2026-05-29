using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Tracks per-position trailing stops and multi-level take-profit orders.
/// Call UpdatePrice() on each price tick. When a level fires, the returned
/// ExitTrigger tells the caller what fraction to sell and why.
/// </summary>
public sealed class SniperTrailingStopService
{
    private readonly ConcurrentDictionary<string, PositionExitState> _states = new(StringComparer.OrdinalIgnoreCase);

    // ── Configuration ─────────────────────────────────────────────────────────

    public sealed class TrailingStopConfig
    {
        /// <summary>Activate trailing stop once price rises this many % above entry.</summary>
        public decimal ActivationPercent { get; init; } = 20m;

        /// <summary>Trail distance: how far below the peak before stop fires.</summary>
        public decimal TrailPercent { get; init; } = 15m;

        /// <summary>Hard stop loss below entry (fires immediately, no activation needed).</summary>
        public decimal HardStopLossPercent { get; init; } = 30m;

        /// <summary>Fraction of position to sell on hard stop.</summary>
        public decimal HardStopSellFraction { get; init; } = 1.0m;

        /// <summary>Fraction of position to sell when trailing stop fires.</summary>
        public decimal TrailStopSellFraction { get; init; } = 1.0m;

        public static TrailingStopConfig Default => new();

        public static TrailingStopConfig Aggressive => new()
        {
            ActivationPercent = 10m,
            TrailPercent = 8m,
            HardStopLossPercent = 20m
        };

        public static TrailingStopConfig Conservative => new()
        {
            ActivationPercent = 40m,
            TrailPercent = 25m,
            HardStopLossPercent = 40m
        };
    }

    public sealed class TakeProfitLevel
    {
        /// <summary>% gain above entry at which this level fires.</summary>
        public decimal TriggerPercent { get; init; }

        /// <summary>Fraction of original position size to sell at this level.</summary>
        public decimal SellFraction { get; init; }

        public string Label { get; init; } = string.Empty;
    }

    public static IReadOnlyList<TakeProfitLevel> DefaultTakeProfitLevels =>
    [
        new() { TriggerPercent = 50m,  SellFraction = 0.25m, Label = "TP1 +50%" },
        new() { TriggerPercent = 100m, SellFraction = 0.25m, Label = "TP2 +100%" },
        new() { TriggerPercent = 200m, SellFraction = 0.25m, Label = "TP3 +200%" },
        new() { TriggerPercent = 500m, SellFraction = 0.25m, Label = "TP4 +500%" },
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    public void Register(
        string positionId,
        decimal entryPriceUsd,
        TrailingStopConfig? config = null,
        IEnumerable<TakeProfitLevel>? takeProfitLevels = null)
    {
        var levels = (takeProfitLevels ?? DefaultTakeProfitLevels)
            .OrderBy(l => l.TriggerPercent)
            .ToList();

        _states[positionId] = new PositionExitState(
            EntryPrice: entryPriceUsd,
            Config: config ?? TrailingStopConfig.Default,
            TakeProfitLevels: levels);
    }

    public void Unregister(string positionId) => _states.TryRemove(positionId, out _);

    /// <summary>
    /// Call on every price update. Returns a trigger if any stop/TP fires, null otherwise.
    /// </summary>
    public ExitTrigger? UpdatePrice(string positionId, decimal currentPriceUsd)
    {
        if (!_states.TryGetValue(positionId, out var state)) return null;
        if (state.EntryPrice <= 0m || currentPriceUsd <= 0m) return null;

        var gainPct = (currentPriceUsd - state.EntryPrice) / state.EntryPrice * 100m;

        // 1. Hard stop loss (fires regardless of trailing state)
        if (gainPct <= -state.Config.HardStopLossPercent)
        {
            _states.TryRemove(positionId, out _);
            return new ExitTrigger(
                positionId,
                ExitTriggerKind.HardStopLoss,
                state.Config.HardStopSellFraction,
                $"Hard stop -{state.Config.HardStopLossPercent:F0}% (price {currentPriceUsd:F6}, entry {state.EntryPrice:F6})",
                currentPriceUsd);
        }

        // 2. Take-profit levels (fired in order, lowest first)
        for (var i = 0; i < state.TakeProfitLevels.Count; i++)
        {
            var level = state.TakeProfitLevels[i];
            if (state.FiredTpLevels.Contains(i)) continue;
            if (gainPct >= level.TriggerPercent)
            {
                state.FiredTpLevels.Add(i);
                return new ExitTrigger(
                    positionId,
                    ExitTriggerKind.TakeProfit,
                    level.SellFraction,
                    $"{level.Label} (+{gainPct:F1}%)",
                    currentPriceUsd);
            }
        }

        // 3. Trailing stop
        if (currentPriceUsd > state.PeakPrice)
        {
            state.PeakPrice = currentPriceUsd;
        }

        var peakGainPct = (state.PeakPrice - state.EntryPrice) / state.EntryPrice * 100m;
        if (peakGainPct >= state.Config.ActivationPercent)
        {
            state.TrailingActive = true;
        }

        if (state.TrailingActive)
        {
            var dropFromPeak = (state.PeakPrice - currentPriceUsd) / state.PeakPrice * 100m;
            if (dropFromPeak >= state.Config.TrailPercent)
            {
                _states.TryRemove(positionId, out _);
                return new ExitTrigger(
                    positionId,
                    ExitTriggerKind.TrailingStop,
                    state.Config.TrailStopSellFraction,
                    $"Trailing stop -{state.Config.TrailPercent:F0}% from peak (peak {state.PeakPrice:F6}, now {currentPriceUsd:F6})",
                    currentPriceUsd);
            }
        }

        return null;
    }

    /// <summary>Returns current trailing state for display (peak, trail active, etc.).</summary>
    public PositionExitSnapshot? GetSnapshot(string positionId)
    {
        if (!_states.TryGetValue(positionId, out var state)) return null;
        return new PositionExitSnapshot(
            state.EntryPrice,
            state.PeakPrice,
            state.TrailingActive,
            state.Config.ActivationPercent,
            state.Config.TrailPercent,
            state.Config.HardStopLossPercent,
            state.FiredTpLevels.Count,
            state.TakeProfitLevels.Count);
    }

    public bool IsTracked(string positionId) => _states.ContainsKey(positionId);

    // ── Internal state ────────────────────────────────────────────────────────

    private sealed class PositionExitState
    {
        public decimal EntryPrice { get; }
        public TrailingStopConfig Config { get; }
        public IReadOnlyList<TakeProfitLevel> TakeProfitLevels { get; }
        public decimal PeakPrice { get; set; }
        public bool TrailingActive { get; set; }
        public HashSet<int> FiredTpLevels { get; } = [];

        public PositionExitState(decimal EntryPrice, TrailingStopConfig Config, List<TakeProfitLevel> TakeProfitLevels)
        {
            this.EntryPrice = EntryPrice;
            this.Config = Config;
            this.TakeProfitLevels = TakeProfitLevels;
            PeakPrice = EntryPrice;
        }
    }
}

public enum ExitTriggerKind { HardStopLoss, TrailingStop, TakeProfit }

public sealed record ExitTrigger(
    string PositionId,
    ExitTriggerKind Kind,
    decimal SellFraction,
    string Reason,
    decimal TriggerPriceUsd);

public sealed record PositionExitSnapshot(
    decimal EntryPrice,
    decimal PeakPrice,
    bool TrailingActive,
    decimal ActivationPercent,
    decimal TrailPercent,
    decimal HardStopPercent,
    int FiredTpCount,
    int TotalTpLevels)
{
    public decimal PeakGainPercent =>
        EntryPrice > 0m ? (PeakPrice - EntryPrice) / EntryPrice * 100m : 0m;

    public decimal TrailStopPrice =>
        TrailingActive ? PeakPrice * (1m - TrailPercent / 100m) : 0m;

    public string StatusLabel =>
        TrailingActive
            ? $"Trailing (peak +{PeakGainPercent:F0}%, stop {TrailStopPrice:F6})"
            : $"Waiting for +{ActivationPercent:F0}% activation";
}
