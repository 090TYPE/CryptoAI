using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Core.Interfaces;

public interface IExchangeGateway
{
    Task ConnectAsync();
    Task DisconnectAsync();
    Task<Order> PlaceOrderAsync(Order order);
    Task CancelOrderAsync(string orderId);
    Task<decimal> GetBalanceAsync(string asset);
    Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10);
    Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null) => Task.FromResult<IReadOnlyList<Order>>([]);
    Task<IReadOnlyList<TradeExecution>> GetRecentTradesAsync(string symbol, int limit = 50) => Task.FromResult<IReadOnlyList<TradeExecution>>([]);
    Task<IReadOnlyList<FuturesPosition>> GetOpenPositionsAsync() => Task.FromResult<IReadOnlyList<FuturesPosition>>([]);
    Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesAsync(string symbol, string timeframe, int limit = 180) => throw new NotSupportedException("Candles are not supported by this gateway.");
    Task SetLeverageAsync(string symbol, int leverage) => throw new NotSupportedException("Leverage is not supported by this gateway.");
    Task SetMarginModeAsync(string symbol, FuturesMarginMode marginMode) => throw new NotSupportedException("Margin mode is not supported by this gateway.");

    // Exchange-native TP/SL orders — throw NotSupportedException when not available;
    // callers fall back to software simulation.
    Task<Order> PlaceTakeProfitOrderAsync(string symbol, OrderSide side, decimal quantity, decimal triggerPrice, FuturesPositionSide positionSide, bool reduceOnly = true)
        => throw new NotSupportedException("Exchange-native take-profit orders are not supported by this gateway.");
    Task<Order> PlaceStopLossOrderAsync(string symbol, OrderSide side, decimal quantity, decimal triggerPrice, FuturesPositionSide positionSide, bool reduceOnly = true)
        => throw new NotSupportedException("Exchange-native stop-loss orders are not supported by this gateway.");

    // Symbol-aware cancel — required for Binance Futures; other gateways may ignore symbol.
    Task CancelOrderAsync(string symbol, string orderId) => CancelOrderAsync(orderId);

    IObservable<MarketData> MarketDataStream { get; } // для реального времени

    // True when the gateway has API credentials for private endpoints (account, order placement).
    // Default: true — non-Binance gateways always attempt private calls and fail with 401 if keys are missing.
    bool HasPrivateApiCredentials => true;
}
