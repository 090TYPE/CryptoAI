using OKX.Net;
using OKX.Net.Clients;
using OKX.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using OKXOrderSide = OKX.Net.Enums.OrderSide;
using OKXOrderType = OKX.Net.Enums.OrderType;
using OKXOrderStatus = OKX.Net.Enums.OrderStatus;
using OKXMarginMode = OKX.Net.Enums.MarginMode;
using OKXPositionSide = OKX.Net.Enums.PositionSide;
using CoreOrderSide = CryptoAITerminal.Core.Enums.OrderSide;
using CoreOrderType = CryptoAITerminal.Core.Enums.OrderType;

namespace CryptoAITerminal.Gateway.OKX;

public class OKXFuturesGateway : IExchangeGateway
{
    private readonly OKXRestClient _restClient;
    private readonly OKXSocketClient _socketClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly IReadOnlyList<string> _symbols;
    private readonly ConcurrentDictionary<string, string> _orderSymbols = new();

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    public OKXFuturesGateway(IEnumerable<string>? symbols = null,
                              string? apiKey = null, string? apiSecret = null, string? passphrase = null)
    {
        _symbols = (symbols ?? ["BTCUSDT", "ETHUSDT", "SOLUSDT"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var creds = !string.IsNullOrWhiteSpace(apiKey)
                 && !string.IsNullOrWhiteSpace(apiSecret)
                 && !string.IsNullOrWhiteSpace(passphrase)
            ? new OKXCredentials(apiKey, apiSecret, passphrase)
            : null;

        _restClient = new OKXRestClient(opts =>
        {
            if (creds is not null) opts.ApiCredentials = creds;
        });
        _socketClient = new OKXSocketClient();
    }

    public async Task ConnectAsync()
    {
        var swapSymbols = _symbols.Select(OKXSymbolHelper.ToSwapSymbol).ToList();
        var result = await _socketClient.UnifiedApi.ExchangeData.SubscribeToTickerUpdatesAsync(
            swapSymbols,
            update =>
            {
                _marketDataSubject.OnNext(new MarketData
                {
                    Symbol = OKXSymbolHelper.FromOkxSymbol(update.Data.Symbol),
                    BestBid = update.Data.BestBidPrice ?? 0m,
                    BestAsk = update.Data.BestAskPrice ?? 0m,
                    LastPrice = update.Data.LastPrice ?? 0m,
                    Timestamp = DateTime.UtcNow
                });
            });

        if (!result.Success)
            throw new Exception($"OKX Futures WebSocket connect failed: {result.Error}");
    }

    public Task DisconnectAsync()
    {
        _socketClient.UnsubscribeAllAsync();
        return Task.CompletedTask;
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        var result = await _restClient.UnifiedApi.ExchangeData.GetOrderBookAsync(
            OKXSymbolHelper.ToSwapSymbol(symbol), depth);
        if (!result.Success)
            throw new Exception($"OKX futures orderbook failed: {result.Error}");

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
        var result = await _restClient.UnifiedApi.Account.GetAccountBalanceAsync(asset);
        if (!result.Success) return 0m;

        var detail = result.Data.Details
            .FirstOrDefault(d => string.Equals(d.Asset, asset, StringComparison.OrdinalIgnoreCase));
        return detail?.AvailableBalance ?? 0m;
    }

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        var side = order.Side == CoreOrderSide.Buy ? OKXOrderSide.Buy : OKXOrderSide.Sell;
        var type = order.Type == CoreOrderType.Market ? OKXOrderType.Market : OKXOrderType.Limit;
        decimal? price = order.Type == CoreOrderType.Limit ? order.Price : null;

        var tradeMode = order.MarginMode == FuturesMarginMode.Isolated
            ? TradeMode.Isolated
            : TradeMode.Cross;

        OKXPositionSide? posSide = order.PositionSide switch
        {
            FuturesPositionSide.Long => OKXPositionSide.Long,
            FuturesPositionSide.Short => OKXPositionSide.Short,
            _ => null  // net / one-way mode
        };

        var result = await _restClient.UnifiedApi.Trading.PlaceOrderAsync(
            OKXSymbolHelper.ToSwapSymbol(order.Symbol),
            side,
            type,
            order.Quantity,
            price,
            positionSide: posSide,
            tradeMode: tradeMode);

        if (!result.Success)
            throw new Exception($"OKX futures place order failed: {result.Error}");

        var idStr = result.Data.OrderId?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(idStr))
        {
            order.Id = idStr;
            _orderSymbols[idStr] = OKXSymbolHelper.ToSwapSymbol(order.Symbol);
        }
        return order;
    }

    public async Task CancelOrderAsync(string orderId)
    {
        if (!_orderSymbols.TryGetValue(orderId, out var okxSymbol)) return;
        if (long.TryParse(orderId, out var longId))
            await _restClient.UnifiedApi.Trading.CancelOrderAsync(okxSymbol, longId);
        _orderSymbols.TryRemove(orderId, out _);
    }

    public async Task CancelOrderAsync(string symbol, string orderId)
    {
        var okxSymbol = _orderSymbols.TryGetValue(orderId, out var cached)
            ? cached
            : OKXSymbolHelper.ToSwapSymbol(symbol);
        if (long.TryParse(orderId, out var longId))
            await _restClient.UnifiedApi.Trading.CancelOrderAsync(okxSymbol, longId);
        _orderSymbols.TryRemove(orderId, out _);
    }

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
    {
        var okxSymbol = symbol is not null ? OKXSymbolHelper.ToSwapSymbol(symbol) : null;
        var result = await _restClient.UnifiedApi.Trading.GetOrdersAsync(
            InstrumentType.Swap, okxSymbol);
        if (!result.Success) return [];

        return result.Data.Select(o => new Order
        {
            Id = o.OrderId?.ToString() ?? string.Empty,
            Symbol = OKXSymbolHelper.FromOkxSymbol(o.Symbol),
            Side = o.OrderSide == OKXOrderSide.Buy ? CoreOrderSide.Buy : CoreOrderSide.Sell,
            Type = o.OrderType == OKXOrderType.Market ? CoreOrderType.Market : CoreOrderType.Limit,
            Quantity = o.Quantity ?? 0m,
            Price = o.Price ?? 0m,
            Status = Core.Enums.OrderStatus.New,
            MarketType = TradingMarketType.FuturesUsdM
        }).ToList();
    }

    public async Task<IReadOnlyList<FuturesPosition>> GetOpenPositionsAsync()
    {
        var result = await _restClient.UnifiedApi.Account.GetPositionsAsync(InstrumentType.Swap);
        if (!result.Success) return [];

        return result.Data
            .Where(p => p.PositionsQuantity != 0m)
            .Select(p => new FuturesPosition
            {
                Symbol = OKXSymbolHelper.FromOkxSymbol(p.Symbol),
                PositionSide = p.PositionSide == OKXPositionSide.Long ? FuturesPositionSide.Long
                    : p.PositionSide == OKXPositionSide.Short ? FuturesPositionSide.Short
                    : FuturesPositionSide.Both,
                Quantity = p.PositionsQuantity ?? 0m,
                EntryPrice = p.AveragePrice ?? 0m,
                MarkPrice = p.MarkPrice ?? 0m,
                UnrealizedPnl = p.UnrealizedProfitAndLoss ?? 0m,
                LiquidationPrice = p.LiquidationPrice ?? 0m,
                Leverage = (int)(p.Leverage ?? 1m),
                UpdatedAtUtc = DateTime.UtcNow
            }).ToList();
    }

    public async Task SetLeverageAsync(string symbol, int leverage)
    {
        await _restClient.UnifiedApi.Account.SetLeverageAsync(
            leverage,
            OKXMarginMode.Cross,
            OKXSymbolHelper.ToSwapSymbol(symbol));
    }

    public async Task SetMarginModeAsync(string symbol, FuturesMarginMode marginMode)
    {
        var okxMode = marginMode == FuturesMarginMode.Isolated
            ? OKXMarginMode.Isolated
            : OKXMarginMode.Cross;
        await _restClient.UnifiedApi.Account.SetLeverageAsync(
            1,          // leverage placeholder — only margin mode matters here
            okxMode,
            OKXSymbolHelper.ToSwapSymbol(symbol));
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesAsync(string symbol, string timeframe, int limit = 180)
    {
        var interval = OKXTimeframeMap.Parse(timeframe);
        var result = await _restClient.UnifiedApi.ExchangeData.GetKlinesAsync(
            OKXSymbolHelper.ToSwapSymbol(symbol), interval, limit: Math.Clamp(limit, 1, 300));

        if (!result.Success)
            throw new Exception($"OKX Futures candles failed: {result.Error}");

        return result.Data
            .OrderBy(k => k.Time)
            .Select(k => new DexOhlcvPoint
            {
                Timestamp = k.Time,
                Open      = k.OpenPrice,
                High      = k.HighPrice,
                Low       = k.LowPrice,
                Close     = k.ClosePrice,
                Volume    = k.Volume
            })
            .ToList();
    }
}
