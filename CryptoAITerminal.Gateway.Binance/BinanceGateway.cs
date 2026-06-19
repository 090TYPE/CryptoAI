using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System.Reactive.Subjects;

namespace CryptoAITerminal.Gateway.Binance;

public class BinanceGateway : IExchangeGateway
{
    private readonly BinanceRestClient _restClient;
    private readonly BinanceSocketClient _socketClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly List<string> _symbols;

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;
    public IReadOnlyList<string> Symbols => _symbols;

    public BinanceGateway(IEnumerable<string>? symbols = null)
    {
        _restClient = new BinanceRestClient();
        _socketClient = new BinanceSocketClient();
        _symbols = (symbols ?? ["BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void PublishTicker(string symbol, decimal bestBid, decimal bestAsk, decimal last,
        decimal quoteVol, decimal changePct, decimal high, decimal low)
    {
        _marketDataSubject.OnNext(new MarketData
        {
            Symbol       = symbol,
            BestBid      = bestBid,
            BestAsk      = bestAsk,
            LastPrice    = last,
            Timestamp    = DateTime.UtcNow,
            Volume24hUsd = quoteVol,
            ChangePct24h = changePct,
            High24h      = high,
            Low24h       = low,
        });
    }

    /// <summary>
    /// Subscribe to one more symbol at runtime (used by the Markets page "add coin").
    /// Returns false if Binance rejects the subscription (e.g. unknown symbol).
    /// </summary>
    public async Task<bool> AddSymbolAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        symbol = symbol.Trim().ToUpperInvariant();
        if (_symbols.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return true;

        var sub = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync(
            new[] { symbol }, update =>
            {
                var d = update.Data;
                PublishTicker(d.Symbol, d.BestBidPrice, d.BestAskPrice, d.LastPrice,
                    d.QuoteVolume, d.PriceChangePercent, d.HighPrice, d.LowPrice);
            });

        if (!sub.Success) return false;
        _symbols.Add(symbol);
        return true;
    }

    public async Task ConnectAsync()
    {
        var subscriptionResult = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync(_symbols, update =>
        {
            var d = update.Data;
            var marketData = new MarketData
            {
                Symbol       = d.Symbol,
                BestBid      = d.BestBidPrice,
                BestAsk      = d.BestAskPrice,
                LastPrice    = d.LastPrice,
                Timestamp    = DateTime.UtcNow,
                Volume24hUsd = d.QuoteVolume,
                ChangePct24h = d.PriceChangePercent,
                High24h      = d.HighPrice,
                Low24h       = d.LowPrice,
            };
            _marketDataSubject.OnNext(marketData);
        });

        if (!subscriptionResult.Success)
        {
            throw new Exception($"Failed to connect: {subscriptionResult.Error}");
        }
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        var result = await _restClient.SpotApi.ExchangeData.GetOrderBookAsync(symbol, depth);
        if (!result.Success)
        {
            throw new Exception($"Failed to get order book: {result.Error}");
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
        if (string.Equals(timeframe, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            return await GetAllCandlesAsync(symbol);
        }

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

        var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit);
        if (!result.Success)
        {
            throw new Exception($"Failed to get candles: {result.Error}");
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

    // Загружает свечи за произвольный диапазон дат с пагинацией (батчи по 1000)
    public async Task<IReadOnlyList<DexOhlcvPoint>> GetCandlesByDateRangeAsync(
        string symbol, string timeframe, DateTime startDate, DateTime endDate)
    {
        var interval = timeframe switch
        {
            "1M"  => KlineInterval.OneMinute,
            "5M"  => KlineInterval.FiveMinutes,
            "15M" => KlineInterval.FifteenMinutes,
            "1H"  => KlineInterval.OneHour,
            "4H"  => KlineInterval.FourHour,
            "1D"  => KlineInterval.OneDay,
            "1W"  => KlineInterval.OneWeek,
            "1MN" => KlineInterval.OneMonth,
            _     => KlineInterval.OneHour
        };

        var candles  = new List<DexOhlcvPoint>();
        var cursor   = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endUtc   = DateTime.SpecifyKind(endDate,   DateTimeKind.Utc);
        const int batchSize = 1000;

        while (cursor < endUtc)
        {
            var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                symbol, interval,
                startTime: cursor,
                endTime: endUtc,
                limit: batchSize);

            if (!result.Success)
                throw new Exception($"Ошибка загрузки свечей: {result.Error}");

            var batch = result.Data
                .Select(c => new DexOhlcvPoint
                {
                    Timestamp = c.OpenTime,
                    Open  = c.OpenPrice,
                    High  = c.HighPrice,
                    Low   = c.LowPrice,
                    Close = c.ClosePrice,
                    Volume = c.Volume
                })
                .ToList();

            if (batch.Count == 0) break;
            candles.AddRange(batch);
            cursor = batch[^1].Timestamp.AddSeconds(1);
            if (batch.Count < batchSize) break;
        }

        return candles
            .GroupBy(c => c.Timestamp)
            .Select(g => g.First())
            .OrderBy(c => c.Timestamp)
            .ToList();
    }

    private async Task<IReadOnlyList<DexOhlcvPoint>> GetAllCandlesAsync(string symbol)
    {
        var candles = new List<DexOhlcvPoint>();
        var cursor = new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;
        const int batchSize = 1000;
        const int maxCandles = 3000;

        while (cursor < now && candles.Count < maxCandles)
        {
            var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                symbol,
                KlineInterval.OneDay,
                startTime: cursor,
                limit: batchSize);

            if (!result.Success)
            {
                throw new Exception($"Failed to get full-history candles: {result.Error}");
            }

            var batch = result.Data
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

            if (batch.Count == 0)
            {
                break;
            }

            candles.AddRange(batch);
            cursor = batch[^1].Timestamp.AddDays(1);

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return candles
            .GroupBy(candle => candle.Timestamp)
            .Select(group => group.First())
            .OrderBy(candle => candle.Timestamp)
            .ToList();
    }

    public Task DisconnectAsync() => _socketClient.UnsubscribeAllAsync();

    public Task<Order> PlaceOrderAsync(Order order)
    {
        order.Status = CryptoAITerminal.Core.Enums.OrderStatus.Filled;
        order.Id = Guid.NewGuid().ToString();
        Console.WriteLine($"[SIMULATION] {order.Side} {order.Quantity} of {order.Symbol} at market price");
        return Task.FromResult(order);
    }

    public Task CancelOrderAsync(string orderId)
    {
        Console.WriteLine($"[SIMULATION] Cancel order {orderId}");
        return Task.CompletedTask;
    }

    public Task<decimal> GetBalanceAsync(string asset)
    {
        return Task.FromResult(10000m);
    }
}
