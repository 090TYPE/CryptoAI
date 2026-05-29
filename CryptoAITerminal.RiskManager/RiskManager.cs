using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.RiskManager;

public class RiskManager
{
    private readonly decimal _maxPositionSizeUsd;
    private readonly decimal _maxDailyLossUsd;
    private decimal _dailyLoss;
    private DateTime _currentDate = DateTime.UtcNow.Date;
    private readonly object _lockObj = new(); // БАГ-21: thread-safe

    public RiskManager(decimal maxPositionSizeUsd = 1000, decimal maxDailyLossUsd = 500)
    {
        _maxPositionSizeUsd = maxPositionSizeUsd;
        _maxDailyLossUsd = maxDailyLossUsd;
    }

    public bool CanPlaceOrder(Order order, decimal currentPrice, decimal availableBalanceUsd, decimal currentOpenExposureUsd = 0m)
    {
        lock (_lockObj)
        {
            if (DateTime.UtcNow.Date != _currentDate)
            {
                _dailyLoss = 0;
                _currentDate = DateTime.UtcNow.Date;
            }

            if (_dailyLoss >= _maxDailyLossUsd)
            {
                Console.WriteLine($"RiskManager: Daily loss limit reached. {_dailyLoss:C} / {_maxDailyLossUsd:C}");
                return false;
            }

            if (order.Quantity <= 0 || currentPrice <= 0)
            {
                Console.WriteLine("RiskManager: Quantity and current price must be positive.");
                return false;
            }

            var orderValueUsd = order.Quantity * currentPrice;
            if (orderValueUsd > _maxPositionSizeUsd)
            {
                Console.WriteLine($"RiskManager: Order value {orderValueUsd:C} exceeds max {_maxPositionSizeUsd:C}");
                return false;
            }

            var projectedExposure = order.ReduceOnly
                ? Math.Max(0m, currentOpenExposureUsd - orderValueUsd)
                : currentOpenExposureUsd + orderValueUsd;
            if (projectedExposure > _maxPositionSizeUsd)
            {
                Console.WriteLine($"RiskManager: Projected exposure {projectedExposure:C} exceeds max {_maxPositionSizeUsd:C}");
                return false;
            }

            var requiredBalanceUsd = orderValueUsd;
            if (order.MarketType == TradingMarketType.FuturesUsdM)
            {
                var leverage = Math.Max(1, order.Leverage ?? 1);
                requiredBalanceUsd = order.ReduceOnly ? 0m : orderValueUsd / leverage;
            }

            if (requiredBalanceUsd > availableBalanceUsd)
            {
                Console.WriteLine($"RiskManager: Insufficient balance. Need {requiredBalanceUsd:C}, have {availableBalanceUsd:C}");
                return false;
            }

            return true;
        }
    }

    public void RecordLoss(decimal lossUsd)
    {
        lock (_lockObj)
        {
            _dailyLoss += Math.Max(0m, lossUsd);
            if (_dailyLoss > _maxDailyLossUsd)
            {
                Console.WriteLine($"RiskManager: Daily loss limit reached! {_dailyLoss:C} / {_maxDailyLossUsd:C}");
            }
        }
    }
}
