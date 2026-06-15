using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.RiskManager;

public class RiskManager
{
    private decimal _maxPositionSizeUsd;
    private decimal _maxDailyLossUsd;
    private decimal _dailyLoss;
    private string _lastBlockReason = string.Empty;
    private DateTime _currentDate = DateTime.UtcNow.Date;
    private readonly object _lockObj = new(); // БАГ-21: thread-safe

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
            if (_dailyLoss > _maxDailyLossUsd)
            {
                _lastBlockReason = $"Daily loss limit reached! {_dailyLoss:C} / {_maxDailyLossUsd:C}";
            }
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
        }
    }

    private RiskCheckResult Block(string reason)
    {
        _lastBlockReason = reason;
        Console.WriteLine($"RiskManager: {reason}");
        return new RiskCheckResult(false, reason);
    }
}

public sealed record RiskCheckResult(bool Allowed, string Reason)
{
    public static readonly RiskCheckResult Allow = new(true, string.Empty);
}

public sealed record RiskBudgetSnapshot(
    decimal MaxPositionSizeUsd,
    decimal MaxDailyLossUsd,
    decimal DailyLossUsd,
    decimal RemainingDailyLossBudgetUsd,
    string LastBlockReason);
