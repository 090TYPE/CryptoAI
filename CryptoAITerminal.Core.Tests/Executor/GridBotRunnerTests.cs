using System.Reactive.Linq;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.2 — the stateful grid runner ties pure grid logic to a gateway + order store:
/// start places initial orders (persisted), poll reacts to fills by placing the opposite order.</summary>
public class GridBotRunnerTests
{
    // in-memory store
    private sealed class FakeStore : IGridOrderStore
    {
        public readonly List<TrackedGridOrder> Orders = new();
        private int _seq;
        public Task AddOpenAsync(Guid botId, Guid userId, int level, string side, decimal price, decimal qty, string? extRef, CancellationToken ct)
        { Orders.Add(new TrackedGridOrder(Guid.NewGuid(), botId, level, side, price, qty, extRef, "open")); return Task.CompletedTask; }
        public Task<IReadOnlyList<TrackedGridOrder>> GetOpenAsync(Guid botId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TrackedGridOrder>>(Orders.Where(o => o.Status == "open").ToList());
        public Task MarkFilledAsync(Guid id, CancellationToken ct)
        { var i = Orders.FindIndex(o => o.Id == id); if (i >= 0) Orders[i] = Orders[i] with { Status = "filled" }; return Task.CompletedTask; }
        public int NextExtRef() => ++_seq;
    }

    private sealed class FakeGateway : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;   // test double: behaves as a fully credentialed gateway
        public readonly List<Order> Placed = new();
        public HashSet<string> OpenIds = new();
        private int _seq;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order o) { o.Id = "G" + (++_seq); Placed.Add(o); OpenIds.Add(o.Id); return Task.FromResult(o); }
        public Task CancelOrderAsync(string id) { OpenIds.Remove(id); return Task.CompletedTask; }
        public Task<decimal> GetBalanceAsync(string a) => Task.FromResult(0m);
        public Task<OrderBook> GetOrderBookAsync(string s, int d = 10) => Task.FromResult<OrderBook>(null!);
        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
            => Task.FromResult<IReadOnlyList<Order>>(Placed.Where(o => OpenIds.Contains(o.Id)).ToList());
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    private static GridConfig Cfg() => new(Guid.NewGuid(), Guid.NewGuid(), "BTCUSDT", 100m, 200m, 4, 0.01m, Futures: false);

    [Fact]
    public async Task Start_places_and_persists_initial_buy_orders()
    {
        var gw = new FakeGateway(); var store = new FakeStore();
        var runner = new GridBotRunner(gw, store);

        await runner.StartAsync(Cfg(), currentPrice: 150m, default);

        // spot @150 over 100..200/4 → buys at 100 and 125
        Assert.Equal(2, gw.Placed.Count);
        Assert.All(gw.Placed, o => Assert.Equal(OrderSide.Buy, o.Side));
        Assert.Equal(2, store.Orders.Count(o => o.Status == "open"));
    }

    [Fact]
    public async Task Poll_reacts_to_a_filled_buy_by_placing_a_sell()
    {
        var gw = new FakeGateway(); var store = new FakeStore();
        var runner = new GridBotRunner(gw, store);
        await runner.StartAsync(Cfg(), 150m, default);

        // simulate the buy at level 1 (price 125) filling: drop it from open ids
        var buy125 = gw.Placed.Single(o => o.Price == 125m);
        gw.OpenIds.Remove(buy125.Id);

        await runner.PollAsync(Cfg(), default);

        // a sell at level 2 (150) should now be placed
        Assert.Contains(gw.Placed, o => o.Side == OrderSide.Sell && o.Price == 150m);
        Assert.Contains(store.Orders, o => o.Side == "sell" && o.Price == 150m && o.Status == "open");
    }

    /// <summary>Шлюз, который на сбой запроса отвечает пустым списком вместо ошибки.</summary>
    private sealed class BlindGateway : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;
        public readonly List<Order> Placed = new();
        private int _seq;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order o) { o.Id = "G" + (++_seq); Placed.Add(o); return Task.FromResult(o); }
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string a) => Task.FromResult(0m);
        public Task<OrderBook> GetOrderBookAsync(string s, int d = 10) => Task.FromResult<OrderBook>(null!);
        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null) =>
            throw new InvalidOperationException("Failed to get open spot orders: -1021 timestamp");
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    /// <summary>
    /// Раннер не защищается от пустого снимка — исчез ордер, значит исполнился, встречный сразу в
    /// биржу. Это верно только пока пустой список означает буквально «живых ордеров нет», поэтому
    /// биржевые шлюзы на сбое обязаны БРОСАТЬ. Ошибку раннер должен пропустить наверх, где
    /// BotExecutorService пропустит тик, а не проглотить её и не решить, что сетка исполнилась.
    ///
    /// Раньше проглатывали: спотовые Binance и KuCoin вообще не реализовывали метод, работала
    /// заглушка интерфейса с пустым списком — и живой грид на сервере каждый опрос помечал ВСЮ
    /// сетку исполненной и выставлял встречные ордера настоящим ключом пользователя.
    /// </summary>
    [Fact]
    public async Task A_failed_snapshot_stops_the_tick_instead_of_filling_the_whole_grid()
    {
        var gw = new BlindGateway(); var store = new FakeStore();
        var runner = new GridBotRunner(gw, store);
        await runner.StartAsync(Cfg(), 150m, default);
        int placedAtStart = gw.Placed.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PollAsync(Cfg(), default));

        Assert.Equal(placedAtStart, gw.Placed.Count);                       // встречных не появилось
        Assert.All(store.Orders, o => Assert.Equal("open", o.Status));      // исполненным не помечен никто
    }
}
