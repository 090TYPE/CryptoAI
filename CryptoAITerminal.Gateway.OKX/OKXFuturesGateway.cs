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
    // OKX SetLeverage writes leverage for the (instrument, marginMode) pair, so
    // setting margin mode and setting leverage go through the SAME endpoint.
    // We remember the last requested leverage+mode per symbol and always re-apply
    // both together, so changing one never silently clobbers the other.
    private readonly ConcurrentDictionary<string, (int Leverage, OKXMarginMode Mode)> _leverageState = new();
    // OKX SWAP order size (`sz`) is in CONTRACTS, not base asset. One contract = ctVal base
    // (e.g. BTC-USDT-SWAP ctVal 0.01 BTC). We load ctVal for every swap once and convert
    // base<->contracts consistently on BOTH order placement and position reads.
    private readonly ConcurrentDictionary<string, decimal> _contractValues = new();
    private volatile bool _ctValLoaded;

    private async Task EnsureContractValuesAsync()
    {
        if (_ctValLoaded) return;
        var result = await _restClient.UnifiedApi.ExchangeData.GetSymbolsAsync(InstrumentType.Swap);
        if (result.Success && result.Data is not null)
        {
            foreach (var inst in result.Data)
                if (inst.ContractValue is { } cv && cv > 0m)
                    _contractValues[inst.Symbol] = cv;
            _ctValLoaded = true;
        }
    }

    /// <summary>ctVal (base per contract) for a swap symbol; throws rather than guessing, so a
    /// missing value never sizes an order wrong.</summary>
    private decimal ContractValueFor(string swapSymbol) =>
        _contractValues.TryGetValue(swapSymbol, out var v) && v > 0m
            ? v
            : throw new Exception($"OKX: unknown contract value (ctVal) for {swapSymbol}; refusing to size the order.");

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

        // order.Quantity is BASE asset; OKX swap `sz` is CONTRACTS → convert via ctVal.
        var swap = OKXSymbolHelper.ToSwapSymbol(order.Symbol);
        await EnsureContractValuesAsync();
        var ctVal = ContractValueFor(swap);
        var contracts = Math.Round(order.Quantity / ctVal, MidpointRounding.AwayFromZero);
        if (contracts <= 0m)
            throw new Exception(
                $"OKX futures order too small: {order.Quantity} {order.Symbol} < 1 contract ({ctVal} base).");

        var result = await _restClient.UnifiedApi.Trading.PlaceOrderAsync(
            swap,
            side,
            type,
            contracts,
            price,
            positionSide: posSide,
            tradeMode: tradeMode,
            reduceOnly: order.ReduceOnly ? true : (bool?)null);

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

        await EnsureContractValuesAsync();
        return result.Data
            .Where(p => p.PositionsQuantity != 0m)
            .Select(p => new FuturesPosition
            {
                Symbol = OKXSymbolHelper.FromOkxSymbol(p.Symbol),
                PositionSide = p.PositionSide == OKXPositionSide.Long ? FuturesPositionSide.Long
                    : p.PositionSide == OKXPositionSide.Short ? FuturesPositionSide.Short
                    : FuturesPositionSide.Both,
                // Contracts → base asset (same ctVal PlaceOrderAsync divides by), so the close
                // path GetOpenPositions→PlaceOrder round-trips to the right contract count.
                Quantity = (p.PositionsQuantity ?? 0m) *
                           (_contractValues.TryGetValue(p.Symbol, out var cv) && cv > 0m ? cv : 1m),
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
        var swap = OKXSymbolHelper.ToSwapSymbol(symbol);
        // Preserve the previously chosen margin mode instead of forcing Cross.
        var mode = _leverageState.TryGetValue(swap, out var s) ? s.Mode : OKXMarginMode.Cross;
        _leverageState[swap] = (leverage, mode);
        await _restClient.UnifiedApi.Account.SetLeverageAsync(leverage, mode, swap);
    }

    public async Task SetMarginModeAsync(string symbol, FuturesMarginMode marginMode)
    {
        var swap = OKXSymbolHelper.ToSwapSymbol(symbol);
        var okxMode = marginMode == FuturesMarginMode.Isolated
            ? OKXMarginMode.Isolated
            : OKXMarginMode.Cross;
        // Re-apply the user's chosen leverage; forcing 1 here reset every position to 1x.
        var leverage = _leverageState.TryGetValue(swap, out var s) ? s.Leverage : 1;
        _leverageState[swap] = (leverage, okxMode);
        await _restClient.UnifiedApi.Account.SetLeverageAsync(leverage, okxMode, swap);
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
