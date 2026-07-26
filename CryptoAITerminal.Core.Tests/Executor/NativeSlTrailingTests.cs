using System.Reactive.Linq;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.3 native SL — on futures with nativeSl, the runner keeps one exchange-native
/// reduce-only stop-loss order that trails (cancel + replace on a move) instead of software-closing
/// on the cross; TP still market-closes and cancels the resting stop.</summary>
public class NativeSlTrailingTests
{
    private sealed class FakeStore : ITslPositionStore
    {
        public TrailingPosition? Pos;
        public Task<TrailingPosition?> LoadAsync(Guid botId, CancellationToken ct) => Task.FromResult(Pos);
        public Task SaveAsync(TrailingPosition pos, CancellationToken ct) { Pos = pos; return Task.CompletedTask; }
    }

    private sealed class FuturesGateway : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;   // test double: behaves as a fully credentialed gateway
        public readonly List<Order> Market = new();
        public readonly List<(decimal Trigger, decimal Qty, string Id)> Sl = new();
        public readonly List<string> Cancelled = new();
        private int _seq;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order o) { o.Id = "M" + (++_seq); Market.Add(o); return Task.FromResult(o); }
        public Task CancelOrderAsync(string id) { Cancelled.Add(id); return Task.CompletedTask; }
        public Task<decimal> GetBalanceAsync(string a) => Task.FromResult(0m);
        public Task<OrderBook> GetOrderBookAsync(string s, int d = 10) => Task.FromResult<OrderBook>(null!);
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
        public Task<Order> PlaceStopLossOrderAsync(string symbol, OrderSide side, decimal qty, decimal trigger, FuturesPositionSide ps, bool reduceOnly = true)
        { var id = "SL" + (++_seq); Sl.Add((trigger, qty, id)); return Task.FromResult(new Order { Id = id, Price = trigger, Quantity = qty }); }
    }

    private static TrailingPosition Pos(TpSlConfig cfg, decimal qty)
        => new(Guid.NewGuid(), "BTCUSDT", IsLong: true, Entry: 100m, Futures: true,
               ServerTrailingStop.Init(true, 100m, cfg) with { RemainingQty = qty }, Closed: false, NativeSl: true);

    [Fact]
    public async Task First_poll_places_a_native_reduce_only_stop()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true };
        var store = new FakeStore { Pos = Pos(cfg, 0.5m) };
        var gw = new FuturesGateway();
        await new TrailingBotRunner(gw, store).PollAsync(store.Pos!.BotId, cfg, price: 100m, default);

        var sl = Assert.Single(gw.Sl);
        Assert.Equal(99m, sl.Trigger);
        Assert.Equal("SL1", store.Pos!.SlOrderId);
        Assert.Empty(gw.Market);
    }

    [Fact]
    public async Task Rising_price_cancels_and_replaces_the_stop_higher()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true };
        var store = new FakeStore { Pos = Pos(cfg, 0.5m) };
        var gw = new FuturesGateway();
        var runner = new TrailingBotRunner(gw, store);

        await runner.PollAsync(store.Pos!.BotId, cfg, 100m, default); // places SL1 @ 99
        await runner.PollAsync(store.Pos!.BotId, cfg, 110m, default); // trails → SL2 @ 108.9

        Assert.Contains(gw.Cancelled, id => id == "SL1");
        Assert.Contains(gw.Sl, s => s.Trigger == 108.9m);
        Assert.Equal(108.9m, store.Pos!.State.Sl);
    }

    [Fact]
    public async Task Take_profit_market_closes_and_cancels_the_stop()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m, TpEnabled = true, TpPercent = 2m };
        var store = new FakeStore { Pos = Pos(cfg, 0.5m) };
        var gw = new FuturesGateway();
        var runner = new TrailingBotRunner(gw, store);

        await runner.PollAsync(store.Pos!.BotId, cfg, 100m, default); // SL1 placed
        await runner.PollAsync(store.Pos!.BotId, cfg, 102m, default); // TP hit

        Assert.Single(gw.Market);                       // market close
        Assert.Contains(gw.Cancelled, id => id == "SL1"); // resting stop pulled
        Assert.True(store.Pos!.Closed);
    }
}
