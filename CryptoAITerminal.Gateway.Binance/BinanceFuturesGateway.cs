using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reactive.Subjects;
using System.Threading;

namespace CryptoAITerminal.Gateway.Binance;

public class BinanceFuturesGateway : IExchangeGateway
{
    private readonly BinanceRestClient _restClient;
    private readonly BinanceSocketClient _socketClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly Subject<Order> _orderUpdateSubject = new();
    private readonly Subject<TradeExecution> _tradeUpdateSubject = new();
    private readonly Subject<string> _accountStateChangedSubject = new();
    private readonly IReadOnlyList<string> _symbols;
    private readonly string? _apiKey;
    private readonly string? _apiSecret;
    private readonly ConcurrentDictionary<string, string> _orderSymbols = new();
    private Timer? _userStreamKeepAliveTimer;
    private string? _userStreamListenKey;

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;
    public IObservable<Order> OrderUpdateStream => _orderUpdateSubject;
    public IObservable<TradeExecution> TradeUpdateStream => _tradeUpdateSubject;
    public IObservable<string> AccountStateChangedStream => _accountStateChangedSubject;

    public BinanceFuturesGateway(IEnumerable<string>? symbols = null, string? apiKey = null, string? apiSecret = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _apiSecret = string.IsNullOrWhiteSpace(apiSecret) ? null : apiSecret;
        _symbols = (symbols ?? ["BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var credentials = HasCredentials
            ? new BinanceCredentials(_apiKey!, _apiSecret!)
            : null;

        _restClient = new BinanceRestClient(options =>
        {
            options.ApiCredentials = credentials;
        });

        _socketClient = new BinanceSocketClient(options =>
        {
            options.ApiCredentials = credentials;
        });
    }

    public async Task ConnectAsync()
    {
        var subscriptionResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToBookTickerUpdatesAsync(_symbols, update =>
        {
            var marketData = new MarketData
            {
                Symbol = GetStringProperty(update.Data, "Symbol") ?? string.Empty,
                BestBid = GetDecimalProperty(update.Data, "BestBidPrice", "BidPrice"),
                BestAsk = GetDecimalProperty(update.Data, "BestAskPrice", "AskPrice"),
                LastPrice = GetDecimalProperty(update.Data, "LastPrice", "BestAskPrice", "AskPrice", "BestBidPrice", "BidPrice"),
                Timestamp = DateTime.UtcNow
            };
            _marketDataSubject.OnNext(marketData);
        });

        if (!subscriptionResult.Success)
        {
            throw new Exception($"Failed to connect to Binance USD-M futures: {subscriptionResult.Error}");
        }

        if (HasCredentials)
        {
            await EnsureUserDataStreamAsync();
        }
    }

    public async Task DisconnectAsync()
    {
        _userStreamKeepAliveTimer?.Dispose();
        _userStreamKeepAliveTimer = null;

        if (!string.IsNullOrWhiteSpace(_userStreamListenKey))
        {
            try
            {
                await _restClient.UsdFuturesApi.Account.StopUserStreamAsync(_userStreamListenKey);
            }
            catch
            {
                // Ignore cleanup failures during disconnect.
            }

            _userStreamListenKey = null;
        }

        await _socketClient.UnsubscribeAllAsync();
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        var result = await _restClient.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, depth);
        if (!result.Success)
        {
            throw new Exception($"Failed to get futures order book: {result.Error}");
        }

        return new OrderBook
        {
            Symbol = symbol,
            Timestamp = DateTime.UtcNow,
            Bids = result.Data.Bids.Select(b => new OrderBookLevel { Price = b.Price, Quantity = b.Quantity }).ToList(),
            Asks = result.Data.Asks.Select(a => new OrderBookLevel { Price = a.Price, Quantity = a.Quantity }).ToList()
        };
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesAsync(string symbol, string timeframe, int limit = 180)
    {
        var interval = timeframe switch
        {
            "1M" => KlineInterval.OneMinute,
            "5M" => KlineInterval.FiveMinutes,
            "15M" => KlineInterval.FifteenMinutes,
            "1H" => KlineInterval.OneHour,
            "4H" => KlineInterval.FourHour,
            "1D" => KlineInterval.OneDay,
            "1W" => KlineInterval.OneWeek,
            "1MN" => KlineInterval.OneMonth,
            _ => KlineInterval.OneMinute
        };

        var result = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit);
        if (!result.Success)
        {
            throw new Exception($"Failed to get futures candles: {result.Error}");
        }

        return result.Data
            .Select(candle => new DexOhlcvPoint
            {
                Timestamp = candle.OpenTime,
                Open = candle.OpenPrice,
                High = candle.HighPrice,
                Low = candle.LowPrice,
                Close = candle.ClosePrice,
                Volume = candle.Volume
            })
            .ToList();
    }

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        EnsurePrivateApiConfigured();

        if (order.Leverage is > 0)
        {
            await SetLeverageAsync(order.Symbol, order.Leverage.Value);
        }

        await SetMarginModeAsync(order.Symbol, order.MarginMode);

        var orderType = order.Type == OrderType.Limit ? FuturesOrderType.Limit : FuturesOrderType.Market;
        var side = order.Side == CryptoAITerminal.Core.Enums.OrderSide.Buy
            ? global::Binance.Net.Enums.OrderSide.Buy
            : global::Binance.Net.Enums.OrderSide.Sell;
        var positionSide = order.PositionSide switch
        {
            FuturesPositionSide.Long => PositionSide.Long,
            FuturesPositionSide.Short => PositionSide.Short,
            _ => PositionSide.Both
        };

        var result = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
            order.Symbol,
            side,
            orderType,
            quantity: order.Quantity,
            price: order.Type == OrderType.Limit && order.Price > 0 ? order.Price : null,
            positionSide: positionSide,
            timeInForce: order.Type == OrderType.Limit ? TimeInForce.GoodTillCanceled : null,
            reduceOnly: order.ReduceOnly,
            newClientOrderId: string.IsNullOrWhiteSpace(order.ClientOrderId) ? null : order.ClientOrderId,
            stopPrice: order.StopPrice);

        if (!result.Success)
        {
            throw new Exception($"Failed to place futures order: {result.Error}");
        }

        order.MarketType = TradingMarketType.FuturesUsdM;
        order.Id = GetStringProperty(result.Data, "Id", "OrderId") ?? order.Id;
        if (!string.IsNullOrEmpty(order.Id)) _orderSymbols[order.Id] = order.Symbol;
        order.ClientOrderId = GetStringProperty(result.Data, "ClientOrderId", "NewClientOrderId") ?? order.ClientOrderId;
        order.ExchangeType = GetStringProperty(result.Data, "Type") ?? orderType.ToString();
        order.TimeInForce = GetStringProperty(result.Data, "TimeInForce") ?? order.TimeInForce;
        order.FilledQuantity = GetDecimalProperty(result.Data, "QuantityFilled", "ExecutedQuantity", "ExecutedQty");
        order.Status = MapOrderStatus(GetStringProperty(result.Data, "Status")) ??
            (order.Type == OrderType.Market && order.StopPrice is null
                ? CryptoAITerminal.Core.Enums.OrderStatus.Filled
                : CryptoAITerminal.Core.Enums.OrderStatus.New);
        return order;
    }

    public async Task CancelOrderAsync(string orderId)
    {
        if (_orderSymbols.TryGetValue(orderId, out var sym))
        {
            try { await CancelOrderAsync(sym, orderId); }
            catch { /* best-effort */ }
        }
        // If symbol is unknown we cannot cancel — Binance Futures requires the symbol.
    }

    // Implements IExchangeGateway.CancelOrderAsync(string symbol, string orderId)
    public async Task CancelOrderAsync(string symbol, string orderId)
    {
        EnsurePrivateApiConfigured();
        long? parsedOrderId = null;
        if (!string.IsNullOrWhiteSpace(orderId) && long.TryParse(orderId, out var parsed))
            parsedOrderId = parsed;
        var result = await _restClient.UsdFuturesApi.Trading.CancelOrderAsync(symbol, parsedOrderId);
        if (!result.Success)
            throw new Exception($"Failed to cancel futures order for {symbol}: {result.Error}");
        _orderSymbols.TryRemove(orderId, out _);
    }

    public async Task CancelOrderAsync(string symbol, string? orderId, string? clientOrderId)
    {
        EnsurePrivateApiConfigured();
        long? parsedOrderId = null;
        if (!string.IsNullOrWhiteSpace(orderId) && long.TryParse(orderId, out var parsed))
            parsedOrderId = parsed;
        var result = await _restClient.UsdFuturesApi.Trading.CancelOrderAsync(symbol, parsedOrderId, clientOrderId);
        if (!result.Success)
            throw new Exception($"Failed to cancel futures order for {symbol}: {result.Error}");
        if (orderId is not null) _orderSymbols.TryRemove(orderId, out _);
    }

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
    {
        EnsurePrivateApiConfigured();

        var result = await _restClient.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
        if (!result.Success)
        {
            throw new Exception($"Failed to get open futures orders: {result.Error}");
        }

        return result.Data.Select(item =>
        {
            var side = string.Equals(GetStringProperty(item, "Side"), "BUY", StringComparison.OrdinalIgnoreCase)
                ? CryptoAITerminal.Core.Enums.OrderSide.Buy
                : CryptoAITerminal.Core.Enums.OrderSide.Sell;
            var rawType = GetStringProperty(item, "Type") ?? string.Empty;
            return new Order
            {
                Id = GetStringProperty(item, "Id", "OrderId") ?? Guid.NewGuid().ToString("N"),
                ClientOrderId = GetStringProperty(item, "ClientOrderId", "NewClientOrderId") ?? string.Empty,
                Symbol = GetStringProperty(item, "Symbol") ?? string.Empty,
                Side = side,
                Type = rawType.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ? OrderType.Limit : OrderType.Market,
                MarketType = TradingMarketType.FuturesUsdM,
                PositionSide = MapPositionSide(GetStringProperty(item, "PositionSide")),
                MarginMode = FuturesMarginMode.Cross,
                ReduceOnly = GetBooleanProperty(item, "ReduceOnly"),
                Quantity = GetDecimalProperty(item, "Quantity", "OriginalQuantity", "OriginalQty"),
                FilledQuantity = GetDecimalProperty(item, "QuantityFilled", "ExecutedQuantity", "ExecutedQty"),
                Price = GetDecimalProperty(item, "Price"),
                StopPrice = GetNullableDecimalProperty(item, "StopPrice"),
                Status = MapOrderStatus(GetStringProperty(item, "Status")) ?? CryptoAITerminal.Core.Enums.OrderStatus.New,
                ExchangeType = rawType,
                TimeInForce = GetStringProperty(item, "TimeInForce") ?? "GTC",
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)GetDecimalProperty(item, "CreateTime", "UpdateTime", "Timestamp")).UtcDateTime
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<TradeExecution>> GetRecentTradesAsync(string symbol, int limit = 50)
    {
        EnsurePrivateApiConfigured();

        var result = await _restClient.UsdFuturesApi.Trading.GetUserTradesAsync(symbol, limit: limit);
        if (!result.Success)
        {
            throw new Exception($"Failed to get futures trades for {symbol}: {result.Error}");
        }

        return result.Data.Select(item => new TradeExecution
        {
            Id = GetStringProperty(item, "Id", "TradeId") ?? Guid.NewGuid().ToString("N"),
            Symbol = GetStringProperty(item, "Symbol") ?? symbol,
            OrderId = GetStringProperty(item, "OrderId") ?? string.Empty,
            ClientOrderId = GetStringProperty(item, "ClientOrderId") ?? string.Empty,
            Side = ResolveOrderSide(item),
            Price = GetDecimalProperty(item, "Price"),
            Quantity = GetDecimalProperty(item, "Quantity", "BaseQuantity", "Qty"),
            RealizedPnl = GetDecimalProperty(item, "RealizedPnl", "RealizedProfit"),
            Fee = GetDecimalProperty(item, "Fee", "Commission"),
            FeeAsset = GetStringProperty(item, "FeeAsset", "CommissionAsset") ?? string.Empty,
            TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)GetDecimalProperty(item, "Timestamp", "Time", "TradeTime")).UtcDateTime
        }).ToList();
    }

    public Task<Order> PlaceTakeProfitOrderAsync(string symbol, CryptoAITerminal.Core.Enums.OrderSide side, decimal quantity, decimal triggerPrice, FuturesPositionSide positionSide, bool reduceOnly = true)
    {
        return PlaceTriggerOrderAsync(symbol, side, quantity, triggerPrice, FuturesOrderType.TakeProfitMarket, positionSide, reduceOnly);
    }

    public Task<Order> PlaceStopLossOrderAsync(string symbol, CryptoAITerminal.Core.Enums.OrderSide side, decimal quantity, decimal triggerPrice, FuturesPositionSide positionSide, bool reduceOnly = true)
    {
        return PlaceTriggerOrderAsync(symbol, side, quantity, triggerPrice, FuturesOrderType.StopMarket, positionSide, reduceOnly);
    }

    public async Task<decimal> GetBalanceAsync(string asset)
    {
        EnsurePrivateApiConfigured();

        var result = await _restClient.UsdFuturesApi.Account.GetBalancesAsync();
        if (!result.Success)
        {
            throw new Exception($"Failed to get futures balances: {result.Error}");
        }

        foreach (var item in result.Data)
        {
            if (!string.Equals(GetStringProperty(item, "Asset"), asset, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return GetDecimalProperty(item, "AvailableBalance", "Available", "Balance", "WalletBalance");
        }

        return 0m;
    }

    public async Task<IReadOnlyList<FuturesPosition>> GetOpenPositionsAsync()
    {
        EnsurePrivateApiConfigured();

        var result = await _restClient.UsdFuturesApi.Account.GetPositionInformationAsync(null!);
        if (!result.Success)
        {
            throw new Exception($"Failed to get futures positions: {result.Error}");
        }

        var positions = new List<FuturesPosition>();
        foreach (var item in result.Data)
        {
            var quantity = GetDecimalProperty(item, "Quantity", "PositionAmount");
            if (quantity == 0m)
            {
                continue;
            }

            positions.Add(new FuturesPosition
            {
                Symbol = GetStringProperty(item, "Symbol") ?? string.Empty,
                PositionSide = MapPositionSide(GetStringProperty(item, "PositionSide")),
                Quantity = quantity,
                EntryPrice = GetDecimalProperty(item, "EntryPrice"),
                MarkPrice = GetDecimalProperty(item, "MarkPrice"),
                UnrealizedPnl = GetDecimalProperty(item, "UnrealizedPnl", "UnrealizedProfit"),
                LiquidationPrice = GetDecimalProperty(item, "LiquidationPrice"),
                Leverage = (int)GetDecimalProperty(item, "Leverage"),
                MarginMode = MapMarginMode(GetStringProperty(item, "MarginType")),
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        return positions;
    }

    public async Task SetLeverageAsync(string symbol, int leverage)
    {
        EnsurePrivateApiConfigured();

        var result = await _restClient.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage);
        if (!result.Success)
        {
            throw new Exception($"Failed to set leverage for {symbol}: {result.Error}");
        }
    }

    public async Task SetMarginModeAsync(string symbol, FuturesMarginMode marginMode)
    {
        EnsurePrivateApiConfigured();

        var mapped = marginMode == FuturesMarginMode.Isolated
            ? FuturesMarginType.Isolated
            : FuturesMarginType.Cross;

        var result = await _restClient.UsdFuturesApi.Account.ChangeMarginTypeAsync(symbol, mapped);
        if (!result.Success && !ContainsAlreadySetMessage(result.Error?.Message))
        {
            throw new Exception($"Failed to set margin mode for {symbol}: {result.Error}");
        }
    }

    public bool HasPrivateApiCredentials => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_apiSecret);
    private bool HasCredentials => HasPrivateApiCredentials;

    private async Task EnsureUserDataStreamAsync()
    {
        if (!HasCredentials || !string.IsNullOrWhiteSpace(_userStreamListenKey))
        {
            return;
        }

        var listenKeyResult = await _restClient.UsdFuturesApi.Account.StartUserStreamAsync();
        if (!listenKeyResult.Success)
        {
            throw new Exception($"Failed to start Binance futures user stream: {listenKeyResult.Error}");
        }

        _userStreamListenKey = GetStringProperty(listenKeyResult.Data, "ListenKey");
        if (string.IsNullOrWhiteSpace(_userStreamListenKey))
        {
            throw new Exception("Binance futures user stream listen key is empty.");
        }

        var subscribeResult = await _socketClient.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
            _userStreamListenKey,
            _ => { },
            _ => { },
            accountUpdate => HandleAccountUpdate(accountUpdate.Data),
            orderUpdate => HandleOrderUpdate(orderUpdate.Data),
            tradeUpdate => HandleTradeUpdate(tradeUpdate.Data),
            _ => { },
            _ => { },
            _ => { },
            _ => { });

        if (!subscribeResult.Success)
        {
            throw new Exception($"Failed to subscribe to Binance futures user stream: {subscribeResult.Error}");
        }

        _userStreamKeepAliveTimer = new Timer(async _ =>
        {
            if (string.IsNullOrWhiteSpace(_userStreamListenKey))
            {
                return;
            }

            try
            {
                await _restClient.UsdFuturesApi.Account.KeepAliveUserStreamAsync(_userStreamListenKey);
            }
            catch
            {
                // Keepalive issues are handled by later reconnect/reconciliation.
            }
        }, null, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20));
    }

    private async Task<Order> PlaceTriggerOrderAsync(string symbol, CryptoAITerminal.Core.Enums.OrderSide side, decimal quantity, decimal triggerPrice, FuturesOrderType orderType, FuturesPositionSide positionSide, bool reduceOnly)
    {
        EnsurePrivateApiConfigured();

        var mappedPositionSide = positionSide switch
        {
            FuturesPositionSide.Long => PositionSide.Long,
            FuturesPositionSide.Short => PositionSide.Short,
            _ => PositionSide.Both
        };
        var mappedSide = side == CryptoAITerminal.Core.Enums.OrderSide.Buy
            ? global::Binance.Net.Enums.OrderSide.Buy
            : global::Binance.Net.Enums.OrderSide.Sell;

        var result = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
            symbol,
            mappedSide,
            orderType,
            quantity: quantity,
            positionSide: mappedPositionSide,
            reduceOnly: reduceOnly,
            stopPrice: triggerPrice,
            workingType: WorkingType.Mark);

        if (!result.Success)
        {
            throw new Exception($"Failed to place futures trigger order for {symbol}: {result.Error}");
        }

        var triggerId = GetStringProperty(result.Data, "Id", "OrderId") ?? Guid.NewGuid().ToString("N");
        _orderSymbols[triggerId] = symbol;

        return new Order
        {
            Id = triggerId,
            ClientOrderId = GetStringProperty(result.Data, "ClientOrderId", "NewClientOrderId") ?? string.Empty,
            Symbol = symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = quantity,
            FilledQuantity = GetDecimalProperty(result.Data, "QuantityFilled", "ExecutedQuantity", "ExecutedQty"),
            StopPrice = triggerPrice,
            ReduceOnly = reduceOnly,
            MarketType = TradingMarketType.FuturesUsdM,
            PositionSide = positionSide,
            ExchangeType = orderType.ToString(),
            TimeInForce = "GTC",
            Status = CryptoAITerminal.Core.Enums.OrderStatus.New
        };
    }

    private void HandleAccountUpdate(object update)
    {
        var updateData = GetPropertyValue(update, "UpdateData");
        var positions = GetPropertyValue(updateData, "Positions") as System.Collections.IEnumerable;
        if (positions is not null)
        {
            foreach (var position in positions)
            {
                var symbol = GetStringProperty(position, "Symbol");
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    _accountStateChangedSubject.OnNext(symbol!);
                }
            }
        }

        var balances = GetPropertyValue(updateData, "Balances") as System.Collections.IEnumerable;
        if (balances is not null)
        {
            foreach (var balance in balances)
            {
                var asset = GetStringProperty(balance, "Asset");
                if (!string.IsNullOrWhiteSpace(asset))
                {
                    _accountStateChangedSubject.OnNext(asset!);
                }
            }
        }
    }

    private void HandleOrderUpdate(object update)
    {
        var data = GetPropertyValue(update, "UpdateData") ?? update;
        var order = new Order
        {
            Id = GetStringProperty(data, "OrderId") ?? Guid.NewGuid().ToString("N"),
            ClientOrderId = GetStringProperty(data, "ClientOrderId") ?? string.Empty,
            Symbol = GetStringProperty(data, "Symbol") ?? string.Empty,
            Side = ResolveOrderSide(data),
            Type = ResolveOrderType(GetStringProperty(data, "Type"), GetStringProperty(data, "OriginalType")),
            MarketType = TradingMarketType.FuturesUsdM,
            PositionSide = MapPositionSide(GetStringProperty(data, "PositionSide")),
            ReduceOnly = GetBooleanProperty(data, "IsReduce"),
            Quantity = GetDecimalProperty(data, "Quantity"),
            FilledQuantity = GetDecimalProperty(data, "AccumulatedQuantityOfFilledTrades", "QuantityOfLastFilledTrade"),
            Price = GetDecimalProperty(data, "Price", "AveragePrice"),
            StopPrice = GetNullableDecimalProperty(data, "StopPrice"),
            Status = MapOrderStatus(GetStringProperty(data, "Status")) ?? CryptoAITerminal.Core.Enums.OrderStatus.New,
            ExchangeType = GetStringProperty(data, "Type", "OriginalType") ?? string.Empty,
            TimeInForce = GetStringProperty(data, "TimeInForce") ?? "GTC",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)GetDecimalProperty(data, "UpdateTime", "TransactionTime")).UtcDateTime
        };

        _orderUpdateSubject.OnNext(order);
        if (!string.IsNullOrWhiteSpace(order.Symbol))
        {
            _accountStateChangedSubject.OnNext(order.Symbol);
        }
    }

    private void HandleTradeUpdate(object update)
    {
        var trade = new TradeExecution
        {
            Id = GetStringProperty(update, "TradeId") ?? Guid.NewGuid().ToString("N"),
            Symbol = GetStringProperty(update, "Symbol") ?? string.Empty,
            OrderId = GetStringProperty(update, "OrderId") ?? string.Empty,
            ClientOrderId = GetStringProperty(update, "ClientOrderId") ?? string.Empty,
            Side = ResolveOrderSide(update),
            Price = GetDecimalProperty(update, "PriceLastFilledTrade", "Price"),
            Quantity = GetDecimalProperty(update, "QuantityOfLastFilledTrade", "Quantity"),
            RealizedPnl = GetDecimalProperty(update, "RealizedProfit", "RealizedPnl"),
            Fee = GetDecimalProperty(update, "Fee", "Commission"),
            FeeAsset = GetStringProperty(update, "FeeAsset", "CommissionAsset") ?? string.Empty,
            TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)GetDecimalProperty(update, "TransactionTime")).UtcDateTime
        };

        _tradeUpdateSubject.OnNext(trade);
        if (!string.IsNullOrWhiteSpace(trade.Symbol))
        {
            _accountStateChangedSubject.OnNext(trade.Symbol);
        }
    }

    // ── Funding Rate API ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns current mark price + funding rate for all USD-M perpetual instruments.
    /// No private credentials required.
    /// </summary>
    public async Task<IReadOnlyList<FundingRateSnapshot>> GetCurrentFundingRatesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var result = await _restClient.UsdFuturesApi.ExchangeData.GetMarkPricesAsync(ct: ct);
            if (!result.Success) return [];

            var snapshots = new List<FundingRateSnapshot>(result.Data.Count());
            foreach (var item in result.Data)
            {
                var symbol      = GetStringProperty(item, "Symbol")        ?? string.Empty;
                var fundingRate = GetDecimalProperty(item, "FundingRate");
                var markPrice   = GetDecimalProperty(item, "MarkPrice");
                var nextFunding = GetDateTimeProperty(item, "NextFundingTime");

                // Skip instruments that returned no funding data (e.g. non-perps)
                if (string.IsNullOrWhiteSpace(symbol)) continue;

                snapshots.Add(new FundingRateSnapshot(symbol, fundingRate, markPrice, nextFunding));
            }
            return snapshots;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Returns historical funding rate records for a single perpetual symbol.
    /// Ordered ascending by time. No private credentials required.
    /// </summary>
    public async Task<IReadOnlyList<FundingHistoryPoint>> GetFundingHistoryAsync(
        string symbol,
        int limit = 90,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _restClient.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(
                symbol, null, null, limit, ct);
            if (!result.Success) return [];

            var points = new List<FundingHistoryPoint>(result.Data.Count());
            foreach (var item in result.Data)
            {
                var time = GetDateTimeProperty(item, "FundingTime");
                var rate = GetDecimalProperty(item, "FundingRate");
                points.Add(new FundingHistoryPoint(time, rate));
            }
            // Sort ascending so chart renders left-to-right
            points.Sort((a, b) => a.Time.CompareTo(b.Time));
            return points;
        }
        catch
        {
            return [];
        }
    }

    // ── DateTime reflection helper (mirrors GetDecimalProperty pattern) ───────

    private static DateTime GetDateTimeProperty(object source, params string[] propertyNames)
    {
        var type = source.GetType();
        foreach (var name in propertyNames)
        {
            var prop = type.GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null) continue;
            var val = prop.GetValue(source);
            if (val is DateTime dt)   return dt;
            if (val is DateTimeOffset dto) return dto.UtcDateTime;
        }
        return DateTime.MinValue;
    }

    private void EnsurePrivateApiConfigured()
    {
        if (!HasCredentials)
        {
            throw new InvalidOperationException("Binance USD-M futures private API credentials are required for balances, positions, leverage and order placement.");
        }
    }

    private static bool ContainsAlreadySetMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
               (message.Contains("No need to change margin type", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("already", StringComparison.OrdinalIgnoreCase));
    }

    private static FuturesPositionSide MapPositionSide(string? value)
    {
        return value?.ToUpperInvariant() switch
        {
            "LONG" => FuturesPositionSide.Long,
            "SHORT" => FuturesPositionSide.Short,
            _ => FuturesPositionSide.Both
        };
    }

    private static FuturesMarginMode MapMarginMode(string? value)
    {
        return value?.ToUpperInvariant() switch
        {
            "ISOLATED" => FuturesMarginMode.Isolated,
            _ => FuturesMarginMode.Cross
        };
    }

    private static decimal GetDecimalProperty(object source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null)
            {
                continue;
            }

            var value = property.GetValue(source);
            if (value is null)
            {
                continue;
            }

            if (value is decimal decimalValue)
            {
                return decimalValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            if (decimal.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0m;
    }

    private static string? GetStringProperty(object source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            var value = property?.GetValue(source)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static object? GetPropertyValue(object? source, string propertyName)
    {
        if (source is null)
        {
            return null;
        }

        var property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return property?.GetValue(source);
    }

    private static decimal? GetNullableDecimalProperty(object source, params string[] propertyNames)
    {
        var value = GetDecimalProperty(source, propertyNames);
        return value == 0m ? null : value;
    }

    private static bool GetBooleanProperty(object source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            var value = property?.GetValue(source);
            if (value is null)
            {
                continue;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (bool.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private static CryptoAITerminal.Core.Enums.OrderSide ResolveOrderSide(object source)
    {
        var sideValue = GetStringProperty(source, "Side");
        if (!string.IsNullOrWhiteSpace(sideValue))
        {
            return string.Equals(sideValue, "BUY", StringComparison.OrdinalIgnoreCase)
                ? CryptoAITerminal.Core.Enums.OrderSide.Buy
                : CryptoAITerminal.Core.Enums.OrderSide.Sell;
        }

        return GetBooleanProperty(source, "Buyer")
            ? CryptoAITerminal.Core.Enums.OrderSide.Buy
            : CryptoAITerminal.Core.Enums.OrderSide.Sell;
    }

    private static OrderType ResolveOrderType(string? primaryType, string? fallbackType)
    {
        var type = primaryType ?? fallbackType ?? string.Empty;
        return type.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            ? OrderType.Limit
            : OrderType.Market;
    }

    private static CryptoAITerminal.Core.Enums.OrderStatus? MapOrderStatus(string? status)
    {
        return status?.ToUpperInvariant() switch
        {
            "NEW" => CryptoAITerminal.Core.Enums.OrderStatus.New,
            "PARTIALLY_FILLED" => CryptoAITerminal.Core.Enums.OrderStatus.PartiallyFilled,
            "FILLED" => CryptoAITerminal.Core.Enums.OrderStatus.Filled,
            "CANCELED" => CryptoAITerminal.Core.Enums.OrderStatus.Canceled,
            "REJECTED" => CryptoAITerminal.Core.Enums.OrderStatus.Rejected,
            _ => null
        };
    }
}
