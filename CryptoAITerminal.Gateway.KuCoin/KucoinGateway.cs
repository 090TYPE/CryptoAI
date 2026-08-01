using Kucoin.Net;
using Kucoin.Net.Clients;
using Kucoin.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Base;
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
    // No orderId → symbol map here on purpose: KuCoin cancels by order id alone, so the six other
    // gateways' cache would be write-only state that nothing ever read and only cancels cleared.
    private readonly SymbolFiltersCache _filters = new();
    private Timer? _tickerTimer;
    private int _polling;

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    // Set from the constructor: null creds means the client was built without an API key, so
    // every private call would 401. The pre-trade guard reads this instead of assuming true.
    private readonly bool _hasPrivateApiCredentials;
    public bool HasPrivateApiCredentials => _hasPrivateApiCredentials;

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

        _hasPrivateApiCredentials = creds is not null;

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
        // Период таймера 3 с, а последовательный обход символов при 150-400 мс на round-trip
        // занимает больше: без гарда тики накладываются и копятся — 429 и устаревшие котировки.
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;

        try
        {
            var ticks = await Task.WhenAll(_symbols.Select(FetchTickerAsync));

            // Публикуем последовательно: подписчики MarketDataStream не рассчитаны
            // на параллельные OnNext.
            foreach (var tick in ticks)
            {
                if (tick is null) continue;
                try { _marketDataSubject.OnNext(tick); }
                catch { /* исключение подписчика не должно ронять опрос */ }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private async Task<MarketData?> FetchTickerAsync(string sym)
    {
        try
        {
            var kucoinSymbol = KucoinSymbolHelper.ToSpotSymbol(sym);
            var result = await _restClient.SpotApi.ExchangeData.GetTickerAsync(kucoinSymbol);
            if (!result.Success || result.Data is null) return null;

            return new MarketData
            {
                Symbol    = sym,
                BestBid   = result.Data.BestBidPrice ?? 0m,
                BestAsk   = result.Data.BestAskPrice ?? 0m,
                LastPrice = result.Data.LastPrice    ?? 0m,
                Timestamp = result.Data.Timestamp,
            };
        }
        catch
        {
            // Игнорируем сетевые ошибки между тиками, следующий poll попробует снова.
            return null;
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
        if (!string.IsNullOrEmpty(id)) order.Id = id;
        return order;
    }

    public async Task CancelOrderAsync(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return;
        await _restClient.SpotApi.Trading.CancelOrderAsync(orderId);
    }

    /// <summary>
    /// Живые лимитки по символу. Метода здесь не было: шлюз размещал и отменял настоящие ордера,
    /// но перечислить их не умел — работала заглушка интерфейса с пустым списком, а по ней
    /// GridBot решает, исполнилась лимитка или нет. Та же дыра, что была на спотовом Binance.
    /// </summary>
    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
    {
        var kucoinSymbol = symbol is not null ? KucoinSymbolHelper.ToSpotSymbol(symbol) : null;
        var result = await _restClient.SpotApi.Trading.GetOrdersAsync(
            symbol: kucoinSymbol,
            status: Kucoin.Net.Enums.OrderStatus.Active);
        // См. BybitGateway: проглоченная ошибка неотличима от «ордеров нет» и даёт фантомный филл.
        if (!result.Success) throw new Exception($"Failed to get open spot orders: {result.Error}");

        return result.Data.Items.Select(o => new Order
        {
            Id = o.Id ?? string.Empty,
            ClientOrderId = o.ClientOrderId ?? string.Empty,
            Symbol = KucoinSymbolHelper.FromKucoinSymbol(o.Symbol ?? string.Empty),
            Side = o.Side == KucoinOrderSide.Buy ? CoreOrderSide.Buy : CoreOrderSide.Sell,
            Type = o.Type == Kucoin.Net.Enums.OrderType.Market ? CoreOrderType.Market : CoreOrderType.Limit,
            Quantity = o.Quantity ?? 0m,
            FilledQuantity = o.QuantityFilled,
            Price = o.Price ?? 0m,
            Status = CryptoAITerminal.Core.Enums.OrderStatus.New,
            MarketType = TradingMarketType.Spot,
        }).ToList();
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

    /// <summary>
    /// KuCoin /api/v2/symbols for one spot pair: priceIncrement, baseIncrement, baseMinSize and
    /// minFunds (the notional floor, which KuCoin leaves null on some pairs). Null when KuCoin will
    /// not answer — the caller then sends its own numbers rather than failing.
    /// </summary>
    public async Task<SymbolFilters?> GetSymbolFiltersAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        symbol = symbol.Trim().ToUpperInvariant();

        var cached = _filters.Get(symbol);
        if (cached is not null) return cached;

        // KuCoin has no per-symbol variant of this endpoint; one call returns every pair, so fill
        // the cache for all of them and answer from it.
        var result = await _restClient.SpotApi.ExchangeData.GetSymbolsAsync(ct: ct);
        if (!result.Success) return null;

        foreach (var info in result.Data)
        {
            var terminalSymbol = KucoinSymbolHelper.FromKucoinSymbol(info.Symbol);
            if (string.IsNullOrEmpty(terminalSymbol)) continue;
            _filters.Store(terminalSymbol, SymbolFilters.Create(
                terminalSymbol,
                info.PriceIncrement,
                info.BaseIncrement,
                info.BaseMinQuantity,
                info.MinFunds));
        }

        return _filters.Get(symbol);
    }

    public void Dispose()
    {
        _tickerTimer?.Dispose();
        _restClient?.Dispose();
    }
}
