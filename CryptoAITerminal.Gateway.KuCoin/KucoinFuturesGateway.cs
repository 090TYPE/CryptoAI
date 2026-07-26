using Kucoin.Net;
using Kucoin.Net.Clients;
using Kucoin.Net.Enums;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Base;
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
    // No orderId → symbol map here on purpose: KuCoin cancels by order id alone, so the six other
    // gateways' cache would be write-only state that nothing ever read and only cancels cleared.
    private readonly ConcurrentDictionary<string, decimal> _contractMultipliers = new();
    private readonly SymbolFiltersCache _filters = new();
    private Timer? _tickerTimer;
    private int _polling;
    private int _defaultLeverage = 1;

    /// <summary>
    /// Returns the KuCoin futures contract multiplier (base-asset amount per 1 contract),
    /// cached per symbol. Throws (rather than guessing 1) when it cannot be resolved, so a
    /// transient API failure never places a wrong-sized order — and only successful lookups
    /// are cached, so a failure is retried on the next order instead of being poisoned.
    /// </summary>
    private async Task<decimal> GetContractMultiplierAsync(string kucoinSymbol)
    {
        if (_contractMultipliers.TryGetValue(kucoinSymbol, out var cached))
            return cached;

        var contract = await _restClient.FuturesApi.ExchangeData.GetContractAsync(kucoinSymbol);
        if (!contract.Success || contract.Data?.Multiplier is not > 0m)
            throw new Exception(
                $"KuCoin: cannot resolve contract multiplier for {kucoinSymbol} " +
                $"({contract.Error}); refusing to size the order.");

        _contractMultipliers[kucoinSymbol] = contract.Data.Multiplier;
        return contract.Data.Multiplier;
    }

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    // Set from the constructor: null creds means the client was built without an API key, so
    // every private call would 401. The pre-trade guard reads this instead of assuming true.
    private readonly bool _hasPrivateApiCredentials;
    public bool HasPrivateApiCredentials => _hasPrivateApiCredentials;

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

        _hasPrivateApiCredentials = creds is not null;

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
            var kucoinSymbol = KucoinSymbolHelper.ToFuturesSymbol(sym);
            var result = await _restClient.FuturesApi.ExchangeData.GetTickerAsync(kucoinSymbol);
            if (!result.Success || result.Data is null) return null;

            return new MarketData
            {
                Symbol    = sym,
                BestBid   = result.Data.BestBidPrice,
                BestAsk   = result.Data.BestAskPrice,
                LastPrice = result.Data.Price,
                Timestamp = DateTime.UtcNow,
            };
        }
        catch
        {
            return null;
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

        // KuCoin Futures требует quantity в КОНТРАКТАХ (int), где 1 контракт =
        // multiplier базового актива (напр. XBTUSDTM: 1 контракт = 0.001 BTC).
        // order.Quantity — это количество базового актива, поэтому делим на multiplier.
        var multiplier = await GetContractMultiplierAsync(kucoinSymbol);
        var contracts = (int)Math.Round(order.Quantity / multiplier, MidpointRounding.AwayFromZero);
        if (contracts <= 0)
            throw new Exception(
                $"KuCoin futures order too small: {order.Quantity} {order.Symbol} < 1 contract " +
                $"({multiplier} base). Increase size.");
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
        if (!string.IsNullOrEmpty(id)) order.Id = id;
        return order;
    }

    public async Task CancelOrderAsync(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return;
        await _restClient.FuturesApi.Trading.CancelOrderAsync(orderId);
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

        var positions = new List<FuturesPosition>();
        foreach (var p in result.Data.Where(p => p.CurrentQuantity != 0m))
        {
            // CurrentQuantity is in CONTRACTS, signed. Convert to SIGNED BASE asset
            // (× multiplier) so the long +/short − contract holds and the close path
            // GetOpenPositions→PlaceOrder round-trips to the right contract count.
            decimal multiplier;
            try { multiplier = await GetContractMultiplierAsync(p.Symbol); }
            catch { multiplier = 1m; } // best-effort for display; PlaceOrder still guards strictly
            positions.Add(new FuturesPosition
            {
                Symbol     = KucoinSymbolHelper.FromKucoinSymbol(p.Symbol),
                PositionSide = p.CurrentQuantity > 0m ? FuturesPositionSide.Long : FuturesPositionSide.Short,
                Quantity   = p.CurrentQuantity * multiplier,
                EntryPrice = p.AverageEntryPrice,
                MarkPrice  = p.MarkPrice,
                UnrealizedPnl   = p.UnrealizedPnl,
                LiquidationPrice = p.LiquidationPrice,
                Leverage   = (int)p.RealLeverage,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }
        return positions;
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

    /// <summary>
    /// KuCoin /api/v1/contracts/{symbol}: tickSize is a price, but lotSize is in CONTRACTS while
    /// every quantity crossing <see cref="IExchangeGateway"/> is in the base asset — so the lot step
    /// and the minimum are multiplied by the same contract multiplier that
    /// <see cref="PlaceOrderAsync"/> divides by. KuCoin publishes no notional floor for futures,
    /// so <see cref="SymbolFilters.MinNotional"/> stays 0 = "no rule".
    /// </summary>
    public async Task<SymbolFilters?> GetSymbolFiltersAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        symbol = symbol.Trim().ToUpperInvariant();

        var cached = _filters.Get(symbol);
        if (cached is not null) return cached;

        var kucoinSymbol = KucoinSymbolHelper.ToFuturesSymbol(symbol);
        var result = await _restClient.FuturesApi.ExchangeData.GetContractAsync(kucoinSymbol, ct);
        if (!result.Success || result.Data is null || result.Data.Multiplier <= 0m) return null;

        var contract = result.Data;
        var stepBase = contract.LotSize > 0m ? contract.LotSize * contract.Multiplier : contract.Multiplier;
        var filters = SymbolFilters.Create(symbol, contract.TickSize, stepBase, stepBase, null);

        _filters.Store(filters);
        return filters;
    }

    public void Dispose()
    {
        _tickerTimer?.Dispose();
        _restClient?.Dispose();
    }
}
