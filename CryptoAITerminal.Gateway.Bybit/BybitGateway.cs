using Bybit.Net;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Base;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using BybitOrderSide = Bybit.Net.Enums.OrderSide;
using BybitOrderType = Bybit.Net.Enums.OrderType;
using CoreOrderSide = CryptoAITerminal.Core.Enums.OrderSide;
using CoreOrderType = CryptoAITerminal.Core.Enums.OrderType;

namespace CryptoAITerminal.Gateway.Bybit;

public class BybitGateway : IExchangeGateway
{
    private readonly BybitRestClient _restClient;
    private readonly BybitSocketClient _socketClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly IReadOnlyList<string> _symbols;
    // orderId → symbol — Bybit cancel requires symbol alongside orderId
    private readonly OrderSymbolCache _orderSymbols = new();
    // instruments-info answers change once in months; one lookup per symbol per session is enough.
    private readonly SymbolFiltersCache _filters = new();

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    // Set from the constructor: null creds means the client was built without an API key, so
    // every private call would 401. The pre-trade guard reads this instead of assuming true.
    private readonly bool _hasPrivateApiCredentials;
    public bool HasPrivateApiCredentials => _hasPrivateApiCredentials;

    /// <param name="testnet">Route to the Bybit testnet, which issues its own API keys.</param>
    public BybitGateway(IEnumerable<string>? symbols = null, string? apiKey = null, string? apiSecret = null,
        bool testnet = false)
    {
        _symbols = (symbols ?? ["BTCUSDT", "ETHUSDT", "SOLUSDT"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var creds = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret)
            ? new BybitCredentials(apiKey, apiSecret)
            : null;

        _hasPrivateApiCredentials = creds is not null;

        _restClient = new BybitRestClient(opts =>
        {
            if (creds is not null) opts.ApiCredentials = creds;
            if (testnet) opts.Environment = BybitEnvironment.Testnet;
        });
        _socketClient = new BybitSocketClient(opts =>
        {
            if (testnet) opts.Environment = BybitEnvironment.Testnet;
        });
    }

    private record TickerSnapshot(decimal Vol, decimal ChgPct, decimal High, decimal Low);

    // Последние ticker-метрики, чтобы 24h Volume/Change/High/Low не сбрасывались,
    // когда приходит обновление стакана без них.
    private readonly ConcurrentDictionary<string, TickerSnapshot> _tickerCache = new();

    public async Task ConnectAsync()
    {
        // Поток №1 — ticker. Содержит Volume24h / High24h / Low24h / Change% 24h.
        var tickerResult = await _socketClient.V5SpotApi.SubscribeToTickerUpdatesAsync(_symbols, update =>
        {
            var d = update.Data;
            _tickerCache[d.Symbol] = new TickerSnapshot(d.Turnover24h, d.PricePercentage24h, d.HighPrice24h, d.LowPrice24h);

            _marketDataSubject.OnNext(new MarketData
            {
                Symbol       = d.Symbol,
                BestBid      = d.LastPrice,
                BestAsk      = d.LastPrice,
                LastPrice    = d.LastPrice,
                Timestamp    = DateTime.UtcNow,
                Volume24hUsd = d.Turnover24h,
                ChangePct24h = d.PricePercentage24h,
                High24h      = d.HighPrice24h,
                Low24h       = d.LowPrice24h,
            });
        });

        if (!tickerResult.Success)
            throw new Exception($"Bybit Spot ticker WebSocket connect failed: {tickerResult.Error}");

        // Поток №2 — best bid/ask из стакана глубины 1. Spot ticker не отдаёт bid/ask
        // отдельно, поэтому без этого потока MarketData.BestBid==BestAsk==LastPrice —
        // ladder в UI и арбитражные модули вычисляют спред некорректно.
        var bookResult = await _socketClient.V5SpotApi.SubscribeToOrderbookUpdatesAsync(_symbols, 1, update =>
        {
            var book = update.Data;
            var bestBid = book.Bids.FirstOrDefault()?.Price ?? 0m;
            var bestAsk = book.Asks.FirstOrDefault()?.Price ?? 0m;
            if (bestBid <= 0m && bestAsk <= 0m) return;

            var midOrLast = bestBid > 0m && bestAsk > 0m ? (bestBid + bestAsk) / 2m
                : bestBid > 0m ? bestBid : bestAsk;

            var cache = _tickerCache.TryGetValue(book.Symbol, out var c)
                ? c : new TickerSnapshot(0m, 0m, 0m, 0m);

            _marketDataSubject.OnNext(new MarketData
            {
                Symbol       = book.Symbol,
                BestBid      = bestBid,
                BestAsk      = bestAsk,
                LastPrice    = midOrLast,
                Timestamp    = DateTime.UtcNow,
                Volume24hUsd = cache.Vol,
                ChangePct24h = cache.ChgPct,
                High24h      = cache.High,
                Low24h       = cache.Low,
            });
        });

        if (!bookResult.Success)
            throw new Exception($"Bybit Spot orderbook WebSocket connect failed: {bookResult.Error}");
    }

    public Task DisconnectAsync()
    {
        _socketClient.UnsubscribeAllAsync();
        return Task.CompletedTask;
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        var result = await _restClient.V5Api.ExchangeData.GetOrderbookAsync(Category.Spot, symbol, depth);
        if (!result.Success)
            throw new Exception($"Bybit orderbook failed: {result.Error}");

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

        // marketUnit must be explicit: for a spot MARKET BUY Bybit reads qty in the QUOTE asset by
        // default, so an order for 0.01 BTC was sent as 0.01 USDT. Order.Quantity is always base.
        var result = await _restClient.V5Api.Trading.PlaceOrderAsync(
            Category.Spot,
            order.Symbol,
            side,
            type,
            order.Quantity,
            price,
            marketUnit: MarketUnit.BaseAsset);

        if (!result.Success)
            throw new Exception($"Bybit place order failed: {result.Error}");

        order.Id = result.Data.OrderId;
        // Bybit's place-order response carries no status, but a market order is already filled by
        // the time the id comes back and can never be cancelled — remembering it would leave an
        // entry that only a cancel ever cleared.
        if (order.Type != CoreOrderType.Market) _orderSymbols.Remember(order.Id, order.Symbol);
        return order;
    }

    public async Task CancelOrderAsync(string orderId)
    {
        if (!_orderSymbols.TryGet(orderId, out var symbol)) return;
        await _restClient.V5Api.Trading.CancelOrderAsync(Category.Spot, symbol, orderId);
        _orderSymbols.Forget(orderId);
    }

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
    {
        var result = await _restClient.V5Api.Trading.GetOrdersAsync(Category.Spot, symbol);
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
            MarketType = TradingMarketType.Spot
        }).ToList();
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesAsync(string symbol, string timeframe, int limit = 180)
    {
        var interval = BybitTimeframeMap.Parse(timeframe);
        var result = await _restClient.V5Api.ExchangeData.GetKlinesAsync(
            Category.Spot, symbol, interval, limit: Math.Clamp(limit, 1, 1000));

        if (!result.Success)
            throw new Exception($"Bybit Spot candles failed: {result.Error}");

        // Bybit возвращает свечи в обратном порядке (новейшие первыми) — переворачиваем.
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

    /// <summary>
    /// Bybit spot instruments-info: tick size, base precision (the lot step), minimum order
    /// quantity and minimum order value. Null when Bybit will not answer — the caller then sends
    /// its own numbers rather than failing on a metadata lookup.
    /// </summary>
    public async Task<SymbolFilters?> GetSymbolFiltersAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        symbol = symbol.Trim().ToUpperInvariant();

        var cached = _filters.Get(symbol);
        if (cached is not null) return cached;

        var result = await _restClient.V5Api.ExchangeData.GetSpotSymbolsAsync(symbol, ct);
        if (!result.Success) return null;

        var info = result.Data.List
            .FirstOrDefault(s => string.Equals(s.Name, symbol, StringComparison.OrdinalIgnoreCase));
        if (info is null) return null;

        var filters = SymbolFilters.Create(
            symbol,
            info.PriceFilter?.TickSize,
            info.LotSizeFilter?.BasePrecision,
            info.LotSizeFilter?.MinOrderQuantity,
            info.LotSizeFilter?.MinOrderValue);

        _filters.Store(filters);
        return filters;
    }
}
