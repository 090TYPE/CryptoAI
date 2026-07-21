using System.Reactive.Linq;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Executor;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>
/// Phase 4.1 — the real server-side bot order executor. Paper moves no money; live requires a
/// trade-only key (withdraw-scoped keys are refused), decrypts it, and places through the gateway.
/// </summary>
public class ExchangeBotOrderExecutorTests
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
        public bool Throw;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order o)
        {
            if (Throw) throw new Exception("exchange down");
            Placed = o;
            o.Id = "EX-123";
            o.Status = OrderStatus.Filled;
            o.FilledQuantity = o.Quantity;
            o.Price = 100m;
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

    private static BotIntent Intent(string mode, decimal notional = 100m) => new(
        Guid.NewGuid(), Guid.NewGuid(), "binance", "spot", mode, "buy", "BTC", "USDT", notional, null, "cid-1");

    private static ExchangeBotOrderExecutor Make(FakeGateway gw, CexKeyMaterial? key, decimal price = 100m) =>
        new(new FakeKeyProvider(key), new FakeCipher(), new FakeGatewayFactory(gw), new FakePriceSource(price));

    // ── tests ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Paper_mode_fills_at_price_and_moves_no_money()
    {
        var gw = new FakeGateway();
        var ex = Make(gw, key: null, price: 50m);

        var fill = await ex.PlaceAsync(Intent("paper", notional: 100m), default);

        Assert.Equal("paper", fill.Status);
        Assert.Equal(2m, fill.FilledQty);   // 100 / 50
        Assert.Equal(50m, fill.AvgPrice);
        Assert.Null(gw.Placed);             // no real order placed
    }

    [Fact]
    public async Task Live_without_key_fails()
    {
        var gw = new FakeGateway();
        var ex = Make(gw, key: null);

        var fill = await ex.PlaceAsync(Intent("live"), default);

        Assert.Equal("failed", fill.Status);
        Assert.Contains("key", fill.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(gw.Placed);
    }

    [Fact]
    public async Task Live_with_withdraw_permission_is_refused()
    {
        var gw = new FakeGateway();
        var ex = Make(gw, new CexKeyMaterial(new byte[1], new byte[1], "trade,withdraw"));

        var fill = await ex.PlaceAsync(Intent("live"), default);

        Assert.Equal("failed", fill.Status);
        Assert.Contains("withdraw", fill.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(gw.Placed);
    }

    [Fact]
    public async Task Live_with_trade_key_places_market_order()
    {
        var gw = new FakeGateway();
        var ex = Make(gw, new CexKeyMaterial(new byte[1], new byte[1], "trade"), price: 100m);

        var fill = await ex.PlaceAsync(Intent("live", notional: 100m), default);

        Assert.Equal("placed", fill.Status);
        Assert.Equal("EX-123", fill.ExtRef);
        Assert.NotNull(gw.Placed);
        Assert.Equal(OrderSide.Buy, gw.Placed!.Side);
        Assert.Equal(OrderType.Market, gw.Placed!.Type);
        Assert.Equal(1m, gw.Placed!.Quantity);      // 100 / 100
        Assert.Equal("cid-1", gw.Placed!.ClientOrderId);
    }

    [Fact]
    public async Task Live_gateway_error_returns_failed()
    {
        var gw = new FakeGateway { Throw = true };
        var ex = Make(gw, new CexKeyMaterial(new byte[1], new byte[1], "trade"));

        var fill = await ex.PlaceAsync(Intent("live"), default);

        Assert.Equal("failed", fill.Status);
        Assert.Contains("down", fill.Error!, StringComparison.OrdinalIgnoreCase);
    }
}
