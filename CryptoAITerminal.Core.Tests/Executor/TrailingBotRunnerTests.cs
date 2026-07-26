using System.Reactive.Linq;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.3 — the stateful trailing runner ties the pure TP/SL decision to a gateway +
/// a position store: it loads the position, evaluates the price, fires the market close (full or
/// partial) or just persists the ratcheted SL, and saves the next state (restart-safe).</summary>
public class TrailingBotRunnerTests
{
    private sealed class FakeStore : ITslPositionStore
    {
        public TrailingPosition? Pos;
        public Task<TrailingPosition?> LoadAsync(Guid botId, CancellationToken ct) => Task.FromResult(Pos);
        public Task SaveAsync(TrailingPosition pos, CancellationToken ct) { Pos = pos; return Task.CompletedTask; }
    }

    private sealed class FakeGateway : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;   // test double: behaves as a fully credentialed gateway
        public readonly List<Order> Placed = new();
        private int _seq;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order o) { o.Id = "C" + (++_seq); Placed.Add(o); return Task.FromResult(o); }
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string a) => Task.FromResult(0m);
        public Task<OrderBook> GetOrderBookAsync(string s, int d = 10) => Task.FromResult<OrderBook>(null!);
        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
            => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    private static TrailingPosition LongPos(TpSlConfig cfg, decimal qty)
        => new(Guid.NewGuid(), "BTCUSDT", IsLong: true, Entry: 100m, Futures: false,
               ServerTrailingStop.Init(true, 100m, cfg) with { RemainingQty = qty }, Closed: false);

    [Fact]
    public async Task Stop_loss_fires_a_full_market_close()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m };
        var store = new FakeStore { Pos = LongPos(cfg, 0.5m) };
        var gw = new FakeGateway();
        var runner = new TrailingBotRunner(gw, store);

        await runner.PollAsync(store.Pos!.BotId, cfg, price: 98.5m, default);

        var o = Assert.Single(gw.Placed);
        Assert.Equal(OrderSide.Sell, o.Side);
        Assert.Equal(0.5m, o.Quantity);
        Assert.True(store.Pos!.Closed);
    }

    [Fact]
    public async Task Trailing_move_persists_new_sl_without_an_order()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true };
        var store = new FakeStore { Pos = LongPos(cfg, 0.5m) };
        var gw = new FakeGateway();
        var runner = new TrailingBotRunner(gw, store);

        await runner.PollAsync(store.Pos!.BotId, cfg, price: 110m, default);

        Assert.Empty(gw.Placed);
        Assert.Equal(108.9m, store.Pos!.State.Sl); // 110 * 0.99
        Assert.False(store.Pos!.Closed);
    }

    [Fact]
    public async Task Partial_tp_closes_a_slice_and_keeps_the_position_open()
    {
        var cfg = new TpSlConfig { TpEnabled = true, TpPercent = 2m, PartialTp = true, PartialTpClosePercent = 50m, PartialTp2Percent = 4m };
        var store = new FakeStore { Pos = LongPos(cfg, 1.0m) };
        var gw = new FakeGateway();
        var runner = new TrailingBotRunner(gw, store);

        await runner.PollAsync(store.Pos!.BotId, cfg, price: 102m, default);

        var o = Assert.Single(gw.Placed);
        Assert.Equal(0.5m, o.Quantity);
        Assert.False(store.Pos!.Closed);
        Assert.Equal(0.5m, store.Pos!.State.RemainingQty);
    }

    [Fact]
    public async Task Closed_position_is_ignored()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m };
        var store = new FakeStore { Pos = LongPos(cfg, 0.5m) with { Closed = true } };
        var gw = new FakeGateway();
        var runner = new TrailingBotRunner(gw, store);

        await runner.PollAsync(store.Pos!.BotId, cfg, price: 98m, default);

        Assert.Empty(gw.Placed);
    }
}
