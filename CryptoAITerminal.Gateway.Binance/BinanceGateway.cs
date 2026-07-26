using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Base;
using System.Reactive.Subjects;

namespace CryptoAITerminal.Gateway.Binance;

public class BinanceGateway : IExchangeGateway
{
    private readonly BinanceRestClient _restClient;
    private readonly BinanceSocketClient _socketClient;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly List<string> _symbols;
    private readonly string? _apiKey;
    private readonly string? _apiSecret;

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;
    public IReadOnlyList<string> Symbols => _symbols;
    public bool HasPrivateApiCredentials => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_apiSecret);

    /// <param name="testnet">Route to the Binance testnet. Testnet issues its own API keys —
    /// a mainnet key is rejected there, and vice versa.</param>
    public BinanceGateway(IEnumerable<string>? symbols = null, string? apiKey = null, string? apiSecret = null,
        bool testnet = false)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _apiSecret = string.IsNullOrWhiteSpace(apiSecret) ? null : apiSecret;
        var credentials = HasPrivateApiCredentials ? new BinanceCredentials(_apiKey!, _apiSecret!) : null;

        _restClient = new BinanceRestClient(options =>
        {
            options.ApiCredentials = credentials;
            if (testnet) options.Environment = BinanceEnvironment.Testnet;
        });
        _socketClient = new BinanceSocketClient(options =>
        {
            options.ApiCredentials = credentials;
            if (testnet) options.Environment = BinanceEnvironment.Testnet;
        });
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

        var interval = BinanceTimeframeMap.Parse(timeframe);

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
        var interval = BinanceTimeframeMap.Parse(timeframe);

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

    /// <summary>
    /// Throws when private endpoints are used without keys. Previously these three methods were
    /// stubs that printed "[SIMULATION]" and reported Filled without contacting Binance — and this
    /// gateway is the spot default in both the desktop terminal and the server executor, so P&amp;L,
    /// balances and the "placed" rows written to the database were all describing trades that had
    /// never happened. Failing loudly beats a convincing fake.
    /// </summary>
    private void EnsurePrivateApiConfigured()
    {
        if (!HasPrivateApiCredentials)
            throw new InvalidOperationException(
                "Binance spot API keys are not configured — set them in Settings → Exchange keys.");
    }

    // Binance cancels spot orders by symbol + id, but IExchangeGateway.CancelOrderAsync(orderId)
    // only carries the id, so remember which symbol each order belongs to.
    private readonly OrderSymbolCache _orderSymbols = new();

    // exchangeInfo is one round-trip per symbol and its answer changes once in months.
    private readonly SymbolFiltersCache _filters = new();

    /// <summary>
    /// PRICE_FILTER / LOT_SIZE / NOTIONAL for one spot symbol. Returns null rather than throwing
    /// when Binance will not answer: the caller then sends its own numbers, exactly as it did
    /// before filters existed, instead of the bot dying on a metadata lookup.
    /// </summary>
    public async Task<SymbolFilters?> GetSymbolFiltersAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        symbol = symbol.Trim().ToUpperInvariant();

        var cached = _filters.Get(symbol);
        if (cached is not null) return cached;

        var result = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol, null, ct);
        if (!result.Success) return null;

        var info = result.Data.Symbols
            .FirstOrDefault(s => string.Equals(s.Name, symbol, StringComparison.OrdinalIgnoreCase));
        if (info is null) return null;

        // NOTIONAL superseded MIN_NOTIONAL in 2023; older symbols still carry only the latter.
        var filters = SymbolFilters.Create(
            symbol,
            info.PriceFilter?.TickSize,
            info.LotSizeFilter?.StepSize,
            info.LotSizeFilter?.MinQuantity,
            info.NotionalFilter?.MinNotional ?? info.MinNotionalFilter?.MinNotional);

        _filters.Store(filters);
        return filters;
    }

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        EnsurePrivateApiConfigured();

        var side = order.Side == CryptoAITerminal.Core.Enums.OrderSide.Buy
            ? global::Binance.Net.Enums.OrderSide.Buy
            : global::Binance.Net.Enums.OrderSide.Sell;
        var type = order.Type == CryptoAITerminal.Core.Enums.OrderType.Limit
            ? SpotOrderType.Limit
            : SpotOrderType.Market;

        var result = await _restClient.SpotApi.Trading.PlaceOrderAsync(
            order.Symbol,
            side,
            type,
            quantity: order.Quantity,
            price: type == SpotOrderType.Limit && order.Price > 0 ? order.Price : null,
            timeInForce: type == SpotOrderType.Limit ? TimeInForce.GoodTillCanceled : null,
            newClientOrderId: string.IsNullOrWhiteSpace(order.ClientOrderId) ? null : order.ClientOrderId);

        if (!result.Success)
            throw new Exception($"Failed to place spot order: {result.Error}");

        order.MarketType = TradingMarketType.Spot;
        order.Id = result.Data.Id.ToString();
        _orderSymbols.Remember(order.Id, order.Symbol);
        order.ClientOrderId = result.Data.ClientOrderId ?? order.ClientOrderId;
        order.FilledQuantity = result.Data.QuantityFilled;
        order.Status = result.Data.Status switch
        {
            global::Binance.Net.Enums.OrderStatus.Filled => CryptoAITerminal.Core.Enums.OrderStatus.Filled,
            global::Binance.Net.Enums.OrderStatus.PartiallyFilled => CryptoAITerminal.Core.Enums.OrderStatus.PartiallyFilled,
            global::Binance.Net.Enums.OrderStatus.Canceled or
            global::Binance.Net.Enums.OrderStatus.PendingCancel => CryptoAITerminal.Core.Enums.OrderStatus.Canceled,
            global::Binance.Net.Enums.OrderStatus.Rejected or
            global::Binance.Net.Enums.OrderStatus.Expired or
            global::Binance.Net.Enums.OrderStatus.ExpiredInMatch => CryptoAITerminal.Core.Enums.OrderStatus.Rejected,
            _ => CryptoAITerminal.Core.Enums.OrderStatus.New,
        };

        // A market order comes back already terminal and can never be cancelled, so the symbol it
        // was placed for is dead weight. Only cancels used to drop entries, which meant every
        // filled order stayed remembered for the whole session.
        if (IsTerminal(order.Status)) _orderSymbols.Forget(order.Id);
        return order;
    }

    private static bool IsTerminal(CryptoAITerminal.Core.Enums.OrderStatus status) =>
        status is CryptoAITerminal.Core.Enums.OrderStatus.Filled
               or CryptoAITerminal.Core.Enums.OrderStatus.Canceled
               or CryptoAITerminal.Core.Enums.OrderStatus.Rejected;

    public Task CancelOrderAsync(string orderId) =>
        CancelOrderAsync(_orderSymbols.TryGet(orderId, out var symbol) ? symbol : string.Empty, orderId);

    public async Task CancelOrderAsync(string symbol, string orderId)
    {
        EnsurePrivateApiConfigured();

        if (string.IsNullOrWhiteSpace(symbol))
            throw new InvalidOperationException($"Cannot cancel spot order {orderId}: symbol is unknown.");

        if (!long.TryParse(orderId, out var id))
            throw new InvalidOperationException($"Cannot cancel spot order '{orderId}': not a Binance order id.");

        var result = await _restClient.SpotApi.Trading.CancelOrderAsync(symbol, id);
        if (!result.Success)
            throw new Exception($"Failed to cancel spot order {orderId}: {result.Error}");

        _orderSymbols.Forget(orderId);
    }

    public async Task<decimal> GetBalanceAsync(string asset)
    {
        EnsurePrivateApiConfigured();

        var result = await _restClient.SpotApi.Account.GetAccountInfoAsync();
        if (!result.Success)
            throw new Exception($"Failed to get spot balances: {result.Error}");

        var balance = result.Data.Balances
            .FirstOrDefault(b => string.Equals(b.Asset, asset, StringComparison.OrdinalIgnoreCase));
        return balance?.Available ?? 0m;
    }
}
