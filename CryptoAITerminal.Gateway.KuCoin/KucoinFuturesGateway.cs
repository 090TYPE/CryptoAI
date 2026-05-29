using Kucoin.Net;
using Kucoin.Net.Clients;
using Kucoin.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using KucoinOrderSide = Kucoin.Net.Enums.OrderSide;
using KucoinNewOrderType = Kucoin.Net.Enums.NewOrderType;
using KucoinFuturesMarginMode = Kucoin.Net.Enums.FuturesMarginMode;
using CoreOrderSide = CryptoAITerminal.Core.Enums.OrderSide;
using CoreOrderType = CryptoAITerminal.Core.Enums.OrderType;

namespace CryptoAITerminal.Gateway.KuCoin;

public class KucoinFuturesGateway : IExchangeGateway, IDisposable
{
    private readonly KucoinRestClient _restClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly IReadOnlyList<string> _symbols;
    private readonly ConcurrentDictionary<string, string> _orderSymbols = new();
    private Timer? _tickerTimer;
    private int _defaultLeverage = 1;

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    public KucoinFuturesGateway(
        IEnumerable<string>? symbols = null,
        string? apiKey = null, string? apiSecret = null, string? passphrase = null)
    {
        _symbols = (symbols ?? ["BTCUSDT", "ETHUSDT", "SOLUSDT"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var creds = !string.IsNullOrWhiteSpace(apiKey)
                 && !string.IsNullOrWhiteSpace(apiSecret)
                 && !string.IsNullOrWhiteSpace(passphrase)
            ? new KucoinCredentials(apiKey, apiSecret, passphrase)
            : null;

        _restClient = new KucoinRestClient(opts =>
        {
            if (creds is not null) opts.ApiCredentials = creds;
        });
    }

    public Task ConnectAsync()
    {
        _tickerTimer = new Timer(_ => _ = PollTickersAsync(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _tickerTimer?.Dispose();
        _tickerTimer = null;
        return Task.CompletedTask;
    }

    private async Task PollTickersAsync()
    {
        foreach (var sym in _symbols)
        {
            try
            {
                var kucoinSymbol = KucoinSymbolHelper.ToFuturesSymbol(sym);
                var result = await _restClient.FuturesApi.ExchangeData.GetTickerAsync(kucoinSymbol);
                if (!result.Success || result.Data is null) continue;

                _marketDataSubject.OnNext(new MarketData
                {
                    Symbol    = sym,
                    BestBid   = result.Data.BestBidPrice,
                    BestAsk   = result.Data.BestAskPrice,
                    LastPrice = result.Data.Price,
                    Timestamp = DateTime.UtcNow,
                });
            }
            catch
            {
            }
        }
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        var kucoinSymbol = KucoinSymbolHelper.ToFuturesSymbol(symbol);
        var safeDepth = depth switch { <= 20 => 20, _ => 100 };
        var result = await _restClient.FuturesApi.ExchangeData.GetAggregatedPartialOrderBookAsync(kucoinSymbol, safeDepth);

        if (!result.Success)
            throw new Exception($"KuCoin futures orderbook failed: {result.Error}");

        return new OrderBook
        {
            Symbol = symbol,
            Timestamp = result.Data.Timestamp,
            Bids = result.Data.Bids
                .Take(depth)
                .Select(b => new OrderBookLevel { Price = b.Price, Quantity = b.Quantity })
                .ToList(),
            Asks = result.Data.Asks
                .Take(depth)
                .Select(a => new OrderBookLevel { Price = a.Price, Quantity = a.Quantity })
                .ToList(),
        };
    }

    public async Task<decimal> GetBalanceAsync(string asset)
    {
        var result = await _restClient.FuturesApi.Account.GetAccountOverviewAsync(asset);
        if (!result.Success) return 0m;
        return result.Data?.AvailableBalance ?? 0m;
    }

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        var side = order.Side == CoreOrderSide.Buy ? KucoinOrderSide.Buy : KucoinOrderSide.Sell;
        var type = order.Type == CoreOrderType.Market ? KucoinNewOrderType.Market : KucoinNewOrderType.Limit;
        var kucoinSymbol = KucoinSymbolHelper.ToFuturesSymbol(order.Symbol);

        // KuCoin Futures требует quantity в контрактах (int) и leverage.
        var contracts = Math.Max(1, (int)Math.Round(order.Quantity));
        var leverage  = order.Leverage ?? _defaultLeverage;
        decimal? price = order.Type == CoreOrderType.Limit ? order.Price : null;

        var marginMode = order.MarginMode == CryptoAITerminal.Core.Enums.FuturesMarginMode.Isolated
            ? KucoinFuturesMarginMode.Isolated
            : KucoinFuturesMarginMode.Cross;

        var result = await _restClient.FuturesApi.Trading.PlaceOrderAsync(
            kucoinSymbol, side, type,
            quantity: contracts,
            leverage: leverage,
            price: price,
            reduceOnly: order.ReduceOnly ? true : null,
            marginMode: marginMode);

        if (!result.Success)
            throw new Exception($"KuCoin futures place order failed: {result.Error}");

        var id = result.Data.Id ?? string.Empty;
        if (!string.IsNullOrEmpty(id))
        {
            order.Id = id;
            _orderSymbols[id] = kucoinSymbol;
        }
        return order;
    }

    public async Task CancelOrderAsync(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return;
        await _restClient.FuturesApi.Trading.CancelOrderAsync(orderId);
        _orderSymbols.TryRemove(orderId, out _);
    }

    // Symbol is ignored — KuCoin Futures cancel needs only the order ID.
    public Task CancelOrderAsync(string symbol, string orderId) => CancelOrderAsync(orderId);

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
    {
        var kucoinSymbol = symbol is not null ? KucoinSymbolHelper.ToFuturesSymbol(symbol) : null;
        var result = await _restClient.FuturesApi.Trading.GetOrdersAsync(
            symbol: kucoinSymbol,
            status: Kucoin.Net.Enums.OrderStatus.Active);
        if (!result.Success) return [];

        return result.Data.Items.Select(o => new Order
        {
            Id     = o.Id ?? string.Empty,
            Symbol = KucoinSymbolHelper.FromKucoinSymbol(o.Symbol ?? string.Empty),
            Side   = o.Side == KucoinOrderSide.Buy ? CoreOrderSide.Buy : CoreOrderSide.Sell,
            Type   = o.Type == Kucoin.Net.Enums.OrderType.Market ? CoreOrderType.Market : CoreOrderType.Limit,
            Quantity = o.Quantity ?? 0m,
            Price    = o.Price ?? 0m,
            Status   = CryptoAITerminal.Core.Enums.OrderStatus.New,
            MarketType = TradingMarketType.FuturesUsdM
        }).ToList();
    }

    // KuCoin Futures does not have a global leverage endpoint — leverage is per-order.
    // We store it as a default used in PlaceOrderAsync.
    public Task SetLeverageAsync(string symbol, int leverage)
    {
        _defaultLeverage = Math.Max(1, leverage);
        return Task.CompletedTask;
    }

    // KuCoin Futures margin mode is set per-order via PlaceOrderAsync.marginMode — no global endpoint.
    public Task SetMarginModeAsync(string symbol, CryptoAITerminal.Core.Enums.FuturesMarginMode marginMode) => Task.CompletedTask;

    public async Task<IReadOnlyList<FuturesPosition>> GetOpenPositionsAsync()
    {
        var result = await _restClient.FuturesApi.Account.GetPositionsAsync();
        if (!result.Success) return [];

        return result.Data
            .Where(p => p.CurrentQuantity != 0m)
            .Select(p => new FuturesPosition
            {
                Symbol     = KucoinSymbolHelper.FromKucoinSymbol(p.Symbol),
                PositionSide = p.CurrentQuantity > 0m ? FuturesPositionSide.Long : FuturesPositionSide.Short,
                Quantity   = Math.Abs(p.CurrentQuantity),
                EntryPrice = p.AverageEntryPrice,
                MarkPrice  = p.MarkPrice,
                UnrealizedPnl   = p.UnrealizedPnl,
                LiquidationPrice = p.LiquidationPrice,
                Leverage   = (int)p.RealLeverage,
                UpdatedAtUtc = DateTime.UtcNow,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesAsync(string symbol, string timeframe, int limit = 180)
    {
        var interval = KucoinFuturesTimeframeMap.Parse(timeframe);
        var kucoinSymbol = KucoinSymbolHelper.ToFuturesSymbol(symbol);
        var result = await _restClient.FuturesApi.ExchangeData.GetKlinesAsync(kucoinSymbol, interval);

        if (!result.Success)
            throw new Exception($"KuCoin Futures candles failed: {result.Error}");

        return result.Data
            .OrderBy(k => k.OpenTime)
            .TakeLast(Math.Max(1, limit))
            .Select(k => new DexOhlcvPoint
            {
                Timestamp = k.OpenTime,
                Open      = k.OpenPrice,
                High      = k.HighPrice,
                Low       = k.LowPrice,
                Close     = k.ClosePrice,
                Volume    = k.Volume,
            })
            .ToList();
    }

    public void Dispose()
    {
        _tickerTimer?.Dispose();
        _restClient?.Dispose();
    }
}

internal static class KucoinFuturesTimeframeMap
{
    public static FuturesKlineInterval Parse(string timeframe) => (timeframe ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "1M"   => FuturesKlineInterval.OneMinute,
        "5M"   => FuturesKlineInterval.FiveMinutes,
        "15M"  => FuturesKlineInterval.FifteenMinutes,
        "30M"  => FuturesKlineInterval.ThirtyMinutes,
        "1H"   => FuturesKlineInterval.OneHour,
        "2H"   => FuturesKlineInterval.TwoHours,
        "4H"   => FuturesKlineInterval.FourHours,
        "8H"   => FuturesKlineInterval.EightHours,
        "12H"  => FuturesKlineInterval.TwelveHours,
        "1D"   => FuturesKlineInterval.OneDay,
        "1W"   => FuturesKlineInterval.OneWeek,
        _      => FuturesKlineInterval.OneHour,
    };
}
