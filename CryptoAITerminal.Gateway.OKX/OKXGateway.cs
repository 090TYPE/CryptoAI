using OKX.Net;
using OKX.Net.Clients;
using OKX.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Base;
using System.Reactive.Subjects;
using OKXOrderSide = OKX.Net.Enums.OrderSide;
using OKXOrderType = OKX.Net.Enums.OrderType;
using OKXOrderStatus = OKX.Net.Enums.OrderStatus;
using CoreOrderSide = CryptoAITerminal.Core.Enums.OrderSide;
using CoreOrderType = CryptoAITerminal.Core.Enums.OrderType;

namespace CryptoAITerminal.Gateway.OKX;

public class OKXGateway : IExchangeGateway
{
    private readonly OKXRestClient _restClient;
    private readonly OKXSocketClient _socketClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly IReadOnlyList<string> _symbols;
    // orderId (long as string) → OKX spot symbol ("BTC-USDT")
    private readonly OrderSymbolCache _orderSymbols = new();
    // /public/instruments answers change once in months; one lookup per symbol per session is enough.
    private readonly SymbolFiltersCache _filters = new();

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    // Set from the constructor: null creds means the client was built without an API key, so
    // every private call would 401. The pre-trade guard reads this instead of assuming true.
    private readonly bool _hasPrivateApiCredentials;
    public bool HasPrivateApiCredentials => _hasPrivateApiCredentials;

    /// <param name="demoTrading">Route to OKX demo trading — OKX's equivalent of a testnet.
    /// Demo keys are created separately in the OKX demo account.</param>
    public OKXGateway(IEnumerable<string>? symbols = null,
                      string? apiKey = null, string? apiSecret = null, string? passphrase = null,
                      bool demoTrading = false)
    {
        _symbols = (symbols ?? ["BTCUSDT", "ETHUSDT", "SOLUSDT"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var creds = !string.IsNullOrWhiteSpace(apiKey)
                 && !string.IsNullOrWhiteSpace(apiSecret)
                 && !string.IsNullOrWhiteSpace(passphrase)
            ? new OKXCredentials(apiKey, apiSecret, passphrase)
            : null;

        _hasPrivateApiCredentials = creds is not null;

        _restClient = new OKXRestClient(opts =>
        {
            if (creds is not null) opts.ApiCredentials = creds;
            if (demoTrading) opts.Environment = OKXEnvironment.Demo;
        });
        _socketClient = new OKXSocketClient(opts =>
        {
            if (demoTrading) opts.Environment = OKXEnvironment.Demo;
        });
    }

    public async Task ConnectAsync()
    {
        var okxSymbols = _symbols.Select(OKXSymbolHelper.ToSpotSymbol).ToList();
        var result = await _socketClient.UnifiedApi.ExchangeData.SubscribeToTickerUpdatesAsync(
            okxSymbols,
            update =>
            {
                var d    = update.Data;
                var last = d.LastPrice ?? 0m;
                // OKX 24h change: (last - open24h) / open24h × 100
                var open = d.OpenPrice ?? 0m;
                var chg  = open > 0 ? Math.Round((last - open) / open * 100m, 4) : 0m;
                _marketDataSubject.OnNext(new MarketData
                {
                    Symbol       = OKXSymbolHelper.FromOkxSymbol(d.Symbol),
                    BestBid      = d.BestBidPrice  ?? 0m,
                    BestAsk      = d.BestAskPrice  ?? 0m,
                    LastPrice    = last,
                    Timestamp    = DateTime.UtcNow,
                    Volume24hUsd = d.QuoteVolume,
                    ChangePct24h = chg,
                    High24h      = d.HighPrice     ?? 0m,
                    Low24h       = d.LowPrice      ?? 0m,
                });
            });

        if (!result.Success)
            throw new Exception($"OKX Spot WebSocket connect failed: {result.Error}");
    }

    public Task DisconnectAsync()
    {
        _socketClient.UnsubscribeAllAsync();
        return Task.CompletedTask;
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        var result = await _restClient.UnifiedApi.ExchangeData.GetOrderBookAsync(
            OKXSymbolHelper.ToSpotSymbol(symbol), depth);
        if (!result.Success)
            throw new Exception($"OKX orderbook failed: {result.Error}");

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

        // quantityAsset must be explicit: in cash mode OKX defaults tgtCcy to the quote asset for a
        // spot MARKET BUY, so an order for 0.01 BTC went out as 0.01 USDT. Order.Quantity is base.
        var result = await _restClient.UnifiedApi.Trading.PlaceOrderAsync(
            OKXSymbolHelper.ToSpotSymbol(order.Symbol),
            side,
            type,
            order.Quantity,
            price,
            tradeMode: TradeMode.Cash,
            quantityAsset: QuantityAsset.BaseAsset);

        if (!result.Success)
            throw new Exception($"OKX place order failed: {result.Error}");

        var idStr = result.Data.OrderId?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(idStr))
        {
            order.Id = idStr;
            // OKX's place-order response carries no status, but a market order is already filled by
            // the time the id comes back and can never be cancelled — remembering it would leave an
            // entry that only a cancel ever cleared.
            if (order.Type != CoreOrderType.Market)
                _orderSymbols.Remember(idStr, OKXSymbolHelper.ToSpotSymbol(order.Symbol));
        }
        return order;
    }

    public async Task CancelOrderAsync(string orderId)
    {
        if (!_orderSymbols.TryGet(orderId, out var okxSymbol)) return;
        if (long.TryParse(orderId, out var longId))
            await _restClient.UnifiedApi.Trading.CancelOrderAsync(okxSymbol, longId);
        _orderSymbols.Forget(orderId);
    }

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
    {
        var okxSymbol = symbol is not null ? OKXSymbolHelper.ToSpotSymbol(symbol) : null;
        var result = await _restClient.UnifiedApi.Trading.GetOrdersAsync(
            InstrumentType.Spot, okxSymbol);
        // См. BybitGateway: проглоченная ошибка неотличима от «ордеров нет» и даёт фантомный филл.
        if (!result.Success) throw new Exception($"Failed to get open spot orders: {result.Error}");

        return result.Data.Select(o => new Order
        {
            Id = o.OrderId?.ToString() ?? string.Empty,
            Symbol = OKXSymbolHelper.FromOkxSymbol(o.Symbol),
            Side = o.OrderSide == OKXOrderSide.Buy ? CoreOrderSide.Buy : CoreOrderSide.Sell,
            Type = o.OrderType == OKXOrderType.Market ? CoreOrderType.Market : CoreOrderType.Limit,
            Quantity = o.Quantity ?? 0m,
            Price = o.Price ?? 0m,
            Status = Core.Enums.OrderStatus.New,
            MarketType = TradingMarketType.Spot
        }).ToList();
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesAsync(string symbol, string timeframe, int limit = 180)
    {
        var interval = OKXTimeframeMap.Parse(timeframe);
        var result = await _restClient.UnifiedApi.ExchangeData.GetKlinesAsync(
            OKXSymbolHelper.ToSpotSymbol(symbol), interval, limit: Math.Clamp(limit, 1, 300));

        if (!result.Success)
            throw new Exception($"OKX Spot candles failed: {result.Error}");

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

    /// <summary>
    /// OKX /public/instruments for one spot instrument: tickSz, lotSz and minSz. OKX publishes no
    /// notional floor for spot, so <see cref="SymbolFilters.MinNotional"/> stays 0 = "no rule".
    /// Null when OKX will not answer — the caller then sends its own numbers.
    /// </summary>
    public async Task<SymbolFilters?> GetSymbolFiltersAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        symbol = symbol.Trim().ToUpperInvariant();

        var cached = _filters.Get(symbol);
        if (cached is not null) return cached;

        var okxSymbol = OKXSymbolHelper.ToSpotSymbol(symbol);
        var result = await _restClient.UnifiedApi.ExchangeData.GetSymbolsAsync(
            InstrumentType.Spot, symbol: okxSymbol, ct: ct);
        if (!result.Success || result.Data is null) return null;

        var info = result.Data
            .FirstOrDefault(i => string.Equals(i.Symbol, okxSymbol, StringComparison.OrdinalIgnoreCase));
        if (info is null) return null;

        var filters = SymbolFilters.Create(symbol, info.TickSize, info.LotSize, info.MinimumOrderSize, null);
        _filters.Store(filters);
        return filters;
    }
}
