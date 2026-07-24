using System.Reactive.Linq;
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Executor;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Core.Tests.Trading;

/// <summary>
/// P0 §5 — server-side manual futures trading. The service is the single place where a manual
/// order is risk-gated, journaled idempotently, and pushed through a trade-only CEX key.
/// </summary>
public class TradingServiceTests
{
    // ── fakes ────────────────────────────────────────────────────────────────
    private sealed class FakePriceSource(decimal px) : IPriceSource
    {
        public Task<decimal> GetPriceAsync(string exchange, string symbol, CancellationToken ct) => Task.FromResult(px);
    }

    private sealed class FakeKeyProvider(CexKeyMaterial? m) : ICexKeyProvider
    {
        public Task<CexKeyMaterial?> FindAsync(Guid userId, string exchange, CancellationToken ct) => Task.FromResult(m);
    }

    private sealed class FakeCipher : IEnvelopeCipher
    {
        public Task<(byte[] Ciphertext, byte[] WrappedDek)> EncryptAsync(string p, CancellationToken ct = default)
            => Task.FromResult((Array.Empty<byte>(), Array.Empty<byte>()));
        public Task<string> DecryptAsync(byte[] c, byte[] w, CancellationToken ct = default)
            => Task.FromResult("{\"key\":\"k\",\"secret\":\"s\"}");
    }

    private sealed class FakeGateway : IExchangeGateway
    {
        public Order? Placed;
        public int PlaceCalls;
        public bool Throw;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order o)
        {
            PlaceCalls++;
            if (Throw) throw new Exception("exchange down");
            Placed = o;
            o.Id = "EX-777";
            o.Status = OrderStatus.Filled;
            o.FilledQuantity = o.Quantity;
            return Task.FromResult(o);
        }
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string a) => Task.FromResult(1000m);
        public Task<OrderBook> GetOrderBookAsync(string s, int d = 10) => Task.FromResult<OrderBook>(null!);
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    private sealed class FakeGatewayFactory(FakeGateway gw) : IGatewayFactory
    {
        public IExchangeGateway Create(string exchange, string market, string creds) => gw;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static readonly Guid Uid = Guid.NewGuid();
    private const decimal Price = 50_000m;

    private static PlaceMarketCommand Cmd(
        decimal qty = 0.01m, bool reduceOnly = false,
        OrderSide side = OrderSide.Buy,
        FuturesPositionSide posSide = FuturesPositionSide.Long,
        string cid = "cid-1")
        => new("binance", "BTCUSDT", side, qty, reduceOnly, 5, FuturesMarginMode.Isolated, posSide, cid);

    private static TradingService Make(FakeGateway gw, InMemoryOrderJournal journal, decimal cap = 1_000_000m) =>
        new(new FakeKeyProvider(new CexKeyMaterial(new byte[1], new byte[1], "trade")),
            new FakeCipher(),
            new FakeGatewayFactory(gw),
            new FakePriceSource(Price),
            new PerOrderCapManualRiskGate(cap),
            journal);

    // ── tests ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Place_market_happy_path_places_futures_order_and_journals_placed()
    {
        var gw = new FakeGateway();
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var res = await svc.PlaceMarketAsync(Uid, Cmd(), default);

        Assert.True(res.Accepted);
        Assert.Equal("EX-777", res.OrderId);
        Assert.Null(res.RejectReason);

        Assert.NotNull(gw.Placed);
        Assert.Equal(TradingMarketType.FuturesUsdM, gw.Placed!.MarketType);
        Assert.Equal(OrderType.Market, gw.Placed!.Type);
        Assert.Equal("BTCUSDT", gw.Placed!.Symbol);
        Assert.Equal("cid-1", gw.Placed!.ClientOrderId);

        var row = Assert.Single(journal.Rows).Value;
        Assert.Equal("placed", row.Status);
        Assert.Equal("EX-777", row.ExchangeOrderId);
    }

    [Fact]
    public async Task Risk_gate_block_rejects_before_journal_and_gateway()
    {
        var gw = new FakeGateway();
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal, cap: 10m); // 0.01 × 50 000 = 500 USD ≫ 10 USD cap

        var res = await svc.PlaceMarketAsync(Uid, Cmd(), default);

        Assert.False(res.Accepted);
        Assert.Null(res.OrderId);
        Assert.Contains("cap", res.RejectReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, gw.PlaceCalls);
        Assert.Null(gw.Placed);
        Assert.Empty(journal.Rows);
    }

    [Fact]
    public async Task Gateway_throw_is_swallowed_and_journaled_rejected()
    {
        var gw = new FakeGateway { Throw = true };
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var res = await svc.PlaceMarketAsync(Uid, Cmd(), default);

        Assert.False(res.Accepted);
        Assert.Null(res.OrderId);
        Assert.Contains("exchange down", res.RejectReason!, StringComparison.OrdinalIgnoreCase);

        var row = Assert.Single(journal.Rows).Value;
        Assert.Equal("rejected", row.Status);
        Assert.Contains("exchange down", row.RejectReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_client_order_id_is_idempotent_and_places_nothing()
    {
        var gw = new FakeGateway();
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var first = await svc.PlaceMarketAsync(Uid, Cmd(), default);
        Assert.True(first.Accepted);
        Assert.Equal(1, gw.PlaceCalls);

        var second = await svc.PlaceMarketAsync(Uid, Cmd(), default);

        Assert.True(second.Accepted);
        Assert.Equal(1, gw.PlaceCalls); // no second placement
        Assert.Single(journal.Rows);
    }

    [Fact]
    public async Task Close_order_carries_reduce_only_and_position_side()
    {
        var gw = new FakeGateway();
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var res = await svc.PlaceMarketAsync(
            Uid,
            Cmd(reduceOnly: true, side: OrderSide.Buy, posSide: FuturesPositionSide.Short, cid: "close-1"),
            default);

        Assert.True(res.Accepted);
        Assert.NotNull(gw.Placed);
        Assert.True(gw.Placed!.ReduceOnly);
        Assert.Equal(FuturesPositionSide.Short, gw.Placed!.PositionSide);
    }
}
