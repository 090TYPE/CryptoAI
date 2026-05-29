using Bybit.Net;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using BybitOrderSide = Bybit.Net.Enums.OrderSide;
using BybitOrderType = Bybit.Net.Enums.OrderType;
using CoreOrderSide = CryptoAITerminal.Core.Enums.OrderSide;
using CoreOrderType = CryptoAITerminal.Core.Enums.OrderType;

namespace CryptoAITerminal.Gateway.Bybit;

public class BybitFuturesGateway : IExchangeGateway
{
    private readonly BybitRestClient _restClient;
    private readonly BybitSocketClient _socketClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly IReadOnlyList<string> _symbols;
    private readonly ConcurrentDictionary<string, string> _orderSymbols = new();

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    public BybitFuturesGateway(IEnumerable<string>? symbols = null, string? apiKey = null, string? apiSecret = null)
    {
        _symbols = (symbols ?? ["BTCUSDT", "ETHUSDT", "SOLUSDT"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var creds = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret)
            ? new BybitCredentials(apiKey, apiSecret)
            : null;

        _restClient = new BybitRestClient(opts =>
        {
            if (creds is not null) opts.ApiCredentials = creds;
        });
        _socketClient = new BybitSocketClient();
    }

    public async Task ConnectAsync()
    {
        var result = await _socketClient.V5LinearApi.SubscribeToTickerUpdatesAsync(_symbols, update =>
        {
            _marketDataSubject.OnNext(new MarketData
            {
                Symbol = update.Data.Symbol,
                BestBid = update.Data.BestBidPrice ?? update.Data.LastPrice ?? 0m,
                BestAsk = update.Data.BestAskPrice ?? update.Data.LastPrice ?? 0m,
                LastPrice = update.Data.LastPrice ?? 0m,
                Timestamp = DateTime.UtcNow
            });
        });

        if (!result.Success)
            throw new Exception($"Bybit Futures WebSocket connect failed: {result.Error}");
    }

    public Task DisconnectAsync()
    {
        _socketClient.UnsubscribeAllAsync();
        return Task.CompletedTask;
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        var result = await _restClient.V5Api.ExchangeData.GetOrderbookAsync(Category.Linear, symbol, depth);
        if (!result.Success)
            throw new Exception($"Bybit futures orderbook failed: {result.Error}");

        return new OrderBook
        {
            Symbol = symbol,
            Timestamp = DateTime.UtcNow,
            Bids = result.Data.Bids
                .Select(b => new OrderBookLevel { Price = b.Price, Quantity = b.Quantity })
                .ToList(),
            Asks = result.Data.Asks
                .Select(a => new OrderBookLevel { Price = a.Price, Quantity = a.Quantity })
                .ToList()
        };
    }

    public async Task<decimal> GetBalanceAsync(string asset)
    {
        var result = await _restClient.V5Api.Account.GetBalancesAsync(AccountType.Unified, asset);
        if (!result.Success) return 0m;

        var entry = result.Data.List
            .SelectMany(b => b.Assets)
            .FirstOrDefault(a => string.Equals(a.Asset, asset, StringComparison.OrdinalIgnoreCase));
        return entry?.WalletBalance ?? 0m;
    }

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        var side = order.Side == CoreOrderSide.Buy ? BybitOrderSide.Buy : BybitOrderSide.Sell;
        var type = order.Type == CoreOrderType.Market ? NewOrderType.Market : NewOrderType.Limit;
        decimal? price = order.Type == CoreOrderType.Limit ? order.Price : null;

        PositionIdx posIdx = order.PositionSide switch
        {
            FuturesPositionSide.Long => PositionIdx.BuyHedgeMode,
            FuturesPositionSide.Short => PositionIdx.SellHedgeMode,
            _ => PositionIdx.OneWayMode
        };

        var result = await _restClient.V5Api.Trading.PlaceOrderAsync(
            Category.Linear,
            order.Symbol,
            side,
            type,
            order.Quantity,
            price,
            reduceOnly: order.ReduceOnly ? true : null,
            positionIdx: posIdx);

        if (!result.Success)
            throw new Exception($"Bybit futures place order failed: {result.Error}");

        order.Id = result.Data.OrderId;
        _orderSymbols[result.Data.OrderId] = order.Symbol;
        return order;
    }

    public async Task CancelOrderAsync(string orderId)
    {
        if (!_orderSymbols.TryGetValue(orderId, out var symbol)) return;
        await _restClient.V5Api.Trading.CancelOrderAsync(Category.Linear, symbol, orderId);
        _orderSymbols.TryRemove(orderId, out _);
    }

    public async Task CancelOrderAsync(string symbol, string orderId)
    {
        await _restClient.V5Api.Trading.CancelOrderAsync(Category.Linear, symbol, orderId);
        _orderSymbols.TryRemove(orderId, out _);
    }

    public Task<Order> PlaceTakeProfitOrderAsync(string symbol, CoreOrderSide side, decimal quantity, decimal triggerPrice, FuturesPositionSide positionSide, bool reduceOnly = true)
        => PlaceConditionalOrderAsync(symbol, side, quantity, triggerPrice, positionSide, isTakeProfit: true, reduceOnly);

    public Task<Order> PlaceStopLossOrderAsync(string symbol, CoreOrderSide side, decimal quantity, decimal triggerPrice, FuturesPositionSide positionSide, bool reduceOnly = true)
        => PlaceConditionalOrderAsync(symbol, side, quantity, triggerPrice, positionSide, isTakeProfit: false, reduceOnly);

    private async Task<Order> PlaceConditionalOrderAsync(string symbol, CoreOrderSide side, decimal quantity, decimal triggerPrice, FuturesPositionSide positionSide, bool isTakeProfit, bool reduceOnly)
    {
        var bybitSide = side == CoreOrderSide.Buy ? BybitOrderSide.Buy : BybitOrderSide.Sell;
        PositionIdx posIdx = positionSide switch
        {
            FuturesPositionSide.Long  => PositionIdx.BuyHedgeMode,
            FuturesPositionSide.Short => PositionIdx.SellHedgeMode,
            _                         => PositionIdx.OneWayMode
        };

        // Trigger direction: TP fires when price moves in-profit direction; SL fires against it.
        // Sell (close long): TP rises to target; SL falls to floor.
        // Buy (close short): TP falls to target; SL rises to floor.
        var triggerDir = (side == CoreOrderSide.Sell)
            ? (isTakeProfit ? TriggerDirection.Rise : TriggerDirection.Fall)
            : (isTakeProfit ? TriggerDirection.Fall : TriggerDirection.Rise);

        var result = await _restClient.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, symbol,
            bybitSide, NewOrderType.Market, quantity,
            triggerDirection: triggerDir,
            orderFilter: OrderFilter.StopOrder,
            triggerPrice: triggerPrice,
            triggerBy: TriggerType.MarkPrice,
            positionIdx: posIdx,
            reduceOnly: reduceOnly ? true : null);

        if (!result.Success)
            throw new Exception($"Bybit futures conditional order failed: {result.Error}");

        var id = result.Data.OrderId;
        _orderSymbols[id] = symbol;
        return new Order
        {
            Id = id,
            Symbol = symbol,
            Side = side,
            Type = CoreOrderType.Market,
            Quantity = quantity,
            StopPrice = triggerPrice,
            ReduceOnly = reduceOnly,
            MarketType = TradingMarketType.FuturesUsdM,
            PositionSide = positionSide
        };
    }

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
    {
        var result = await _restClient.V5Api.Trading.GetOrdersAsync(Category.Linear, symbol);
        if (!result.Success) return [];

        return result.Data.List.Select(o => new Order
        {
            Id = o.OrderId,
            Symbol = o.Symbol,
            Side = o.Side == BybitOrderSide.Buy ? CoreOrderSide.Buy : CoreOrderSide.Sell,
            Type = o.OrderType == BybitOrderType.Market ? CoreOrderType.Market : CoreOrderType.Limit,
            Quantity = o.Quantity,
            Price = o.Price ?? 0m,
            Status = Core.Enums.OrderStatus.New,
            MarketType = TradingMarketType.FuturesUsdM
        }).ToList();
    }

    public async Task<IReadOnlyList<FuturesPosition>> GetOpenPositionsAsync()
    {
        var result = await _restClient.V5Api.Trading.GetPositionsAsync(Category.Linear);
        if (!result.Success) return [];

        return result.Data.List
            .Where(p => p.Quantity != 0m)
            .Select(p => new FuturesPosition
            {
                Symbol = p.Symbol,
                PositionSide = p.Side == PositionSide.Buy ? FuturesPositionSide.Long
                    : p.Side == PositionSide.Sell ? FuturesPositionSide.Short
                    : FuturesPositionSide.Both,
                Quantity = p.Quantity,
                EntryPrice = p.AveragePrice ?? 0m,
                MarkPrice = p.MarkPrice ?? 0m,
                UnrealizedPnl = p.UnrealizedPnl ?? 0m,
                LiquidationPrice = p.LiquidationPrice ?? 0m,
                Leverage = (int)(p.Leverage ?? 1m),
                UpdatedAtUtc = DateTime.UtcNow
            }).ToList();
    }

    public async Task SetLeverageAsync(string symbol, int leverage)
    {
        await _restClient.V5Api.Account.SetLeverageAsync(
            Category.Linear, symbol,
            buyLeverage: leverage,
            sellLeverage: leverage);
    }

    public async Task SetMarginModeAsync(string symbol, FuturesMarginMode marginMode)
    {
        var bybitMode = marginMode == FuturesMarginMode.Isolated
            ? MarginMode.IsolatedMargin
            : MarginMode.RegularMargin;
        await _restClient.V5Api.Account.SetMarginModeAsync(bybitMode);
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesAsync(string symbol, string timeframe, int limit = 180)
    {
        var interval = BybitTimeframeMap.Parse(timeframe);
        var result = await _restClient.V5Api.ExchangeData.GetKlinesAsync(
            Category.Linear, symbol, interval, limit: Math.Clamp(limit, 1, 1000));

        if (!result.Success)
            throw new Exception($"Bybit Futures candles failed: {result.Error}");

        return result.Data.List
            .OrderBy(k => k.StartTime)
            .Select(k => new DexOhlcvPoint
            {
                Timestamp = k.StartTime,
                Open      = k.OpenPrice,
                High      = k.HighPrice,
                Low       = k.LowPrice,
                Close     = k.ClosePrice,
                Volume    = k.Volume
            })
            .ToList();
    }
}
