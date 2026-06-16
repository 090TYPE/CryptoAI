using System.Collections.Generic;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.RiskManager;

public class RiskManager
{
    private const int MaxBlockHistory = 20;
    private const int MaxLossSamples = 40;

    private decimal _maxPositionSizeUsd;
    private decimal _maxDailyLossUsd;
    private decimal _dailyLoss;
    private string _lastBlockReason = string.Empty;
    private DateTime _currentDate = DateTime.UtcNow.Date;
    private readonly object _lockObj = new(); // БАГ-21: thread-safe

    // Bounded audit trail for the Risk page. Newest entries are appended; the oldest are
    // trimmed once the cap is hit so memory stays flat over a long session.
    private readonly List<RiskBlockEntry> _blockHistory = new();
    // Cumulative daily-loss readings, one per RecordLoss, for the daily-loss sparkline.
    private readonly List<decimal> _dailyLossSamples = new();

    public RiskManager(decimal maxPositionSizeUsd = 1000, decimal maxDailyLossUsd = 500)
    {
        _maxPositionSizeUsd = maxPositionSizeUsd;
        _maxDailyLossUsd = maxDailyLossUsd;
    }

    public bool CanPlaceOrder(Order order, decimal currentPrice, decimal availableBalanceUsd, decimal currentOpenExposureUsd = 0m)
        => Evaluate(order, currentPrice, availableBalanceUsd, currentOpenExposureUsd).Allowed;

    /// <summary>
    /// Same gate as <see cref="CanPlaceOrder"/> but returns a structured reason so the
    /// UI can surface why an order was blocked instead of losing it to the console.
    /// </summary>
    public RiskCheckResult Evaluate(Order order, decimal currentPrice, decimal availableBalanceUsd, decimal currentOpenExposureUsd = 0m)
    {
        lock (_lockObj)
        {
            RollDailyLossIfNewDay();

            if (_dailyLoss >= _maxDailyLossUsd)
            {
                return Block($"Daily loss limit reached. {_dailyLoss:C} / {_maxDailyLossUsd:C}");
            }

            if (order.Quantity <= 0 || currentPrice <= 0)
            {
                return Block("Quantity and current price must be positive.");
            }

            var orderValueUsd = order.Quantity * currentPrice;
            if (orderValueUsd > _maxPositionSizeUsd)
            {
                return Block($"Order value {orderValueUsd:C} exceeds max {_maxPositionSizeUsd:C}");
            }

            var projectedExposure = order.ReduceOnly
                ? Math.Max(0m, currentOpenExposureUsd - orderValueUsd)
                : currentOpenExposureUsd + orderValueUsd;
            if (projectedExposure > _maxPositionSizeUsd)
            {
                return Block($"Projected exposure {projectedExposure:C} exceeds max {_maxPositionSizeUsd:C}");
            }

            var requiredBalanceUsd = orderValueUsd;
            if (order.MarketType == TradingMarketType.FuturesUsdM)
            {
                var leverage = Math.Max(1, order.Leverage ?? 1);
                requiredBalanceUsd = order.ReduceOnly ? 0m : orderValueUsd / leverage;
            }

            if (requiredBalanceUsd > availableBalanceUsd)
            {
                return Block($"Insufficient balance. Need {requiredBalanceUsd:C}, have {availableBalanceUsd:C}");
            }

            _lastBlockReason = string.Empty;
            return RiskCheckResult.Allow;
        }
    }

    public void RecordLoss(decimal lossUsd)
    {
        lock (_lockObj)
        {
            RollDailyLossIfNewDay();
            _dailyLoss += Math.Max(0m, lossUsd);
            RecordLossSample();
            if (_dailyLoss > _maxDailyLossUsd)
            {
                _lastBlockReason = $"Daily loss limit reached! {_dailyLoss:C} / {_maxDailyLossUsd:C}";
            }
        }
    }

    /// <summary>
    /// Manually clears today's accumulated loss and the block log (e.g. the "Reset daily"
    /// button on the Risk page after the trader has reviewed and accepted the day's drawdown).
    /// </summary>
    public void ResetDailyLoss()
    {
        lock (_lockObj)
        {
            _dailyLoss = 0m;
            _currentDate = DateTime.UtcNow.Date;
            _lastBlockReason = string.Empty;
            _dailyLossSamples.Clear();
            _blockHistory.Clear();
        }
    }

    /// <summary>Most-recent-first copy of the bounded block log.</summary>
    public IReadOnlyList<RiskBlockEntry> GetRecentBlocks()
    {
        lock (_lockObj)
        {
            var copy = new List<RiskBlockEntry>(_blockHistory);
            copy.Reverse();
            return copy;
        }
    }

    /// <summary>Cumulative daily-loss readings (oldest first) for the sparkline.</summary>
    public IReadOnlyList<decimal> GetDailyLossHistory()
    {
        lock (_lockObj)
        {
            return new List<decimal>(_dailyLossSamples);
        }
    }

    /// <summary>Adjusts the limits at runtime (e.g. from the Risk page). Non-positive values are ignored.</summary>
    public void UpdateLimits(decimal maxPositionSizeUsd, decimal maxDailyLossUsd)
    {
        lock (_lockObj)
        {
            if (maxPositionSizeUsd > 0m)
            {
                _maxPositionSizeUsd = maxPositionSizeUsd;
            }

            if (maxDailyLossUsd > 0m)
            {
                _maxDailyLossUsd = maxDailyLossUsd;
            }
        }
    }

    public decimal MaxPositionSizeUsd { get { lock (_lockObj) { return _maxPositionSizeUsd; } } }

    public decimal MaxDailyLossUsd { get { lock (_lockObj) { return _maxDailyLossUsd; } } }

    public string LastBlockReason { get { lock (_lockObj) { return _lastBlockReason; } } }

    /// <summary>A thread-safe, point-in-time view of the daily-loss budget for the UI.</summary>
    public RiskBudgetSnapshot GetBudgetSnapshot()
    {
        lock (_lockObj)
        {
            RollDailyLossIfNewDay();
            var remaining = Math.Max(0m, _maxDailyLossUsd - _dailyLoss);
            return new RiskBudgetSnapshot(_maxPositionSizeUsd, _maxDailyLossUsd, _dailyLoss, remaining, _lastBlockReason);
        }
    }

    private void RollDailyLossIfNewDay()
    {
        if (DateTime.UtcNow.Date != _currentDate)
        {
            _dailyLoss = 0;
            _currentDate = DateTime.UtcNow.Date;
            _dailyLossSamples.Clear();
        }
    }

    private void RecordLossSample()
    {
        _dailyLossSamples.Add(_dailyLoss);
        if (_dailyLossSamples.Count > MaxLossSamples)
        {
            _dailyLossSamples.RemoveAt(0);
        }
    }

    private RiskCheckResult Block(string reason)
    {
        _lastBlockReason = reason;
        _blockHistory.Add(new RiskBlockEntry(DateTime.UtcNow, reason));
        if (_blockHistory.Count > MaxBlockHistory)
        {
            _blockHistory.RemoveAt(0);
        }

        Console.WriteLine($"RiskManager: {reason}");
        return new RiskCheckResult(false, reason);
    }
}

/// <summary>A single blocked-order event recorded for the Risk page audit trail.</summary>
public sealed record RiskBlockEntry(DateTime TimeUtc, string Reason);

public sealed record RiskCheckResult(bool Allowed, string Reason)
{
    public static readonly RiskCheckResult Allow = new(true, string.Empty);
}

public enum RiskBudgetLevel
{
    Ok,
    Caution,
    Critical
}

public sealed record RiskBudgetSnapshot(
    decimal MaxPositionSizeUsd,
    decimal MaxDailyLossUsd,
    decimal DailyLossUsd,
    decimal RemainingDailyLossBudgetUsd,
    string LastBlockReason)
{
    /// <summary>Fraction of the daily-loss cap already consumed (0 when no cap is set).</summary>
    public decimal DailyLossUsedFraction =>
        MaxDailyLossUsd <= 0m ? 0m : DailyLossUsd / MaxDailyLossUsd;

    /// <summary>Caution at 50% of the daily-loss cap, Critical at 80%.</summary>
    public RiskBudgetLevel Level
    {
        get
        {
            var used = DailyLossUsedFraction;
            if (used >= 0.8m)
            {
                return RiskBudgetLevel.Critical;
            }

            return used >= 0.5m ? RiskBudgetLevel.Caution : RiskBudgetLevel.Ok;
        }
    }
}
