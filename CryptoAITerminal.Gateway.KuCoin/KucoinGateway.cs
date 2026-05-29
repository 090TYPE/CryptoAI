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
using KucoinAccountType  = Kucoin.Net.Enums.AccountType;
using CoreOrderSide = CryptoAITerminal.Core.Enums.OrderSide;
using CoreOrderType = CryptoAITerminal.Core.Enums.OrderType;

namespace CryptoAITerminal.Gateway.KuCoin;

public class KucoinGateway : IExchangeGateway, IDisposable
{
    private readonly KucoinRestClient _restClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly IReadOnlyList<string> _symbols;
    private readonly ConcurrentDictionary<string, string> _orderSymbols = new();
    private Timer? _tickerTimer;

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    public KucoinGateway(
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
        // KuCoin Spot ticker через REST polling: KucoinStreamTick поля не документированы
        // в XML, REST KucoinTick проверен (BestBidPrice/BestAskPrice/LastPrice).
        // Интервал 3 сек ниже стандартных rate limits.
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
                var kucoinSymbol = KucoinSymbolHelper.ToSpotSymbol(sym);
                var result = await _restClient.SpotApi.ExchangeData.GetTickerAsync(kucoinSymbol);
                if (!result.Success || result.Data is null) continue;

                _marketDataSubject.OnNext(new MarketData
                {
                    Symbol    = sym,
                    BestBid   = result.Data.BestBidPrice ?? 0m,
                    BestAsk   = result.Data.BestAskPrice ?? 0m,
                    LastPrice = result.Data.LastPrice    ?? 0m,
                    Timestamp = result.Data.Timestamp,
                });
            }
            catch
            {
                // Игнорируем сетевые ошибки между тиками, следующий poll попробует снова.
            }
        }
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        var kucoinSymbol = KucoinSymbolHelper.ToSpotSymbol(symbol);
        var safeDepth = depth switch { <= 20 => 20, _ => 100 };
        var result = await _restClient.SpotApi.ExchangeData.GetAggregatedPartialOrderBookAsync(kucoinSymbol, safeDepth);

        if (!result.Success)
            throw new Exception($"KuCoin orderbook failed: {result.Error}");

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
        var result = await _restClient.SpotApi.Account.GetAccountsAsync(asset, KucoinAccountType.Trade);
        if (!result.Success) return 0m;

        var entry = result.Data
            .FirstOrDefault(a => string.Equals(a.Asset, asset, StringComparison.OrdinalIgnoreCase));
        return entry?.Available ?? 0m;
    }

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        var side = order.Side == CoreOrderSide.Buy ? KucoinOrderSide.Buy : KucoinOrderSide.Sell;
        var type = order.Type == CoreOrderType.Market ? KucoinNewOrderType.Market : KucoinNewOrderType.Limit;
        var kucoinSymbol = KucoinSymbolHelper.ToSpotSymbol(order.Symbol);

        // Market: quantity (base asset). Limit: price + quantity.
        decimal? price    = order.Type == CoreOrderType.Limit ? order.Price : null;
        decimal? quantity = order.Quantity > 0m ? order.Quantity : null;

        var result = await _restClient.SpotApi.Trading.PlaceOrderAsync(
            kucoinSymbol, side, type,
            quantity: quantity,
            price: price);

        if (!result.Success)
            throw new Exception($"KuCoin place order failed: {result.Error}");

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
        await _restClient.SpotApi.Trading.CancelOrderAsync(orderId);
        _orderSymbols.TryRemove(orderId, out _);
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesAsync(string symbol, string timeframe, int limit = 180)
    {
        var interval = KucoinSpotTimeframeMap.Parse(timeframe);
        var kucoinSymbol = KucoinSymbolHelper.ToSpotSymbol(symbol);
        var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(kucoinSymbol, interval);

        if (!result.Success)
            throw new Exception($"KuCoin Spot candles failed: {result.Error}");

        // KuCoin отдаёт klines newest-first — переворачиваем и обрезаем до limit.
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

internal static class KucoinSpotTimeframeMap
{
    public static KlineInterval Parse(string timeframe) => (timeframe ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "1M"   => KlineInterval.OneMinute,
        "3M"   => KlineInterval.ThreeMinutes,
        "5M"   => KlineInterval.FiveMinutes,
        "15M"  => KlineInterval.FifteenMinutes,
        "30M"  => KlineInterval.ThirtyMinutes,
        "1H"   => KlineInterval.OneHour,
        "2H"   => KlineInterval.TwoHours,
        "4H"   => KlineInterval.FourHours,
        "6H"   => KlineInterval.SixHours,
        "8H"   => KlineInterval.EightHours,
        "12H"  => KlineInterval.TwelveHours,
        "1D"   => KlineInterval.OneDay,
        "1W"   => KlineInterval.OneWeek,
        "1MN"  => KlineInterval.OneMonth,
        _      => KlineInterval.OneHour,
    };
}
