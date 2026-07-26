using System.Collections.Concurrent;
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

    /// <summary>Releases every caller only once <paramref name="parties"/> callers have arrived,
    /// so concurrent PlaceMarketAsync calls hit the journal claim at the same time instead of
    /// running one after the other.</summary>
    private sealed class RendezvousPriceSource(decimal px, int parties) : IPriceSource
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task<decimal> GetPriceAsync(string exchange, string symbol, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) >= parties) _gate.TrySetResult();
            await _gate.Task;
            return px;
        }
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
        public bool HasPrivateApiCredentials => true;   // test double: behaves as a fully credentialed gateway
        public Order? Placed;
        private int _placeCalls;
        public int PlaceCalls => Volatile.Read(ref _placeCalls);
        public bool Throw;
        public bool ThrowOnSetLeverage;
        public bool ThrowOnSetMarginMode;
        public (string Symbol, int Leverage)? LeverageSet;
        public (string Symbol, FuturesMarginMode Mode)? MarginModeSet;
        /// <summary>Ordered log of the calls that matter for ordering assertions
        /// ("leverage", "marginMode", "place"). Guarded because the race tests share one gateway.</summary>
        public readonly List<string> CallLog = [];
        private readonly object _logLock = new();
        private void Log(string call) { lock (_logLock) CallLog.Add(call); }
        public readonly List<(string Symbol, string OrderId)> Cancelled = [];
        /// <summary>When set, the call stays in flight (as a real exchange round-trip would)
        /// until this task completes.</summary>
        public Task? InFlightGate;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task SetLeverageAsync(string symbol, int leverage)
        {
            Log("leverage");
            LeverageSet = (symbol, leverage);
            if (ThrowOnSetLeverage) throw new Exception("leverage not modified");
            return Task.CompletedTask;
        }
        public Task SetMarginModeAsync(string symbol, FuturesMarginMode marginMode)
        {
            Log("marginMode");
            MarginModeSet = (symbol, marginMode);
            if (ThrowOnSetMarginMode) throw new Exception("margin mode not modified");
            return Task.CompletedTask;
        }
        public async Task<Order> PlaceOrderAsync(Order o)
        {
            Log("place");
            Interlocked.Increment(ref _placeCalls);
            if (Throw) throw new Exception("exchange down");
            if (InFlightGate is not null) await Task.WhenAny(InFlightGate, Task.Delay(TimeSpan.FromSeconds(10)));
            await Task.Yield(); // a real round-trip suspends; keep the race window open
            Placed = o;
            o.Id = "EX-777";
            o.Status = OrderStatus.Filled;
            o.FilledQuantity = o.Quantity;
            return o;
        }
        /// <summary>The 1-arg overload every futures gateway resolves from an (always empty)
        /// per-instance symbol cache — a silent no-op. Recording no symbol makes that visible.</summary>
        public Task CancelOrderAsync(string id) { Cancelled.Add(("", id)); return Task.CompletedTask; }
        public Task CancelOrderAsync(string symbol, string id) { Cancelled.Add((symbol, id)); return Task.CompletedTask; }
        public Task<decimal> GetBalanceAsync(string a) => Task.FromResult(1000m);
        public Task<OrderBook> GetOrderBookAsync(string s, int d = 10) => Task.FromResult<OrderBook>(null!);
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    /// <summary>Journal whose MarkPlacedAsync always fails — stands in for a DB blip or a
    /// cancelled request token AFTER the exchange has already accepted the order.</summary>
    private sealed class MarkPlacedThrowsJournal : IOrderJournal
    {
        private readonly InMemoryOrderJournal _inner = new();
        public int MarkPlacedCalls;
        public Task<bool> TryClaimAsync(TradeOrderRow row, CancellationToken ct) => _inner.TryClaimAsync(row, ct);
        public Task<TradeOrderRow?> GetAsync(Guid u, string cid, CancellationToken ct) => _inner.GetAsync(u, cid, ct);
        public Task MarkPlacedAsync(Guid u, string cid, string exId, CancellationToken ct)
        {
            MarkPlacedCalls++;
            throw new InvalidOperationException("journal unavailable");
        }
        public Task MarkRejectedAsync(Guid u, string cid, string reason, CancellationToken ct)
            => _inner.MarkRejectedAsync(u, cid, reason, ct);
    }

    /// <summary>Journal that honours the CancellationToken it is handed — like a real Npgsql
    /// command does. Passing the caller's (already cancelled) request token to a post-placement
    /// write therefore fails here, exactly as it would against Postgres.</summary>
    private sealed class CtHonouringJournal : IOrderJournal
    {
        private readonly InMemoryOrderJournal _inner = new();
        public ConcurrentDictionary<string, TradeOrderRow> Rows => _inner.Rows;
        public Task<bool> TryClaimAsync(TradeOrderRow row, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return _inner.TryClaimAsync(row, ct); }
        public Task<TradeOrderRow?> GetAsync(Guid u, string cid, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return _inner.GetAsync(u, cid, ct); }
        public Task MarkPlacedAsync(Guid u, string cid, string exId, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return _inner.MarkPlacedAsync(u, cid, exId, ct); }
        public Task MarkRejectedAsync(Guid u, string cid, string reason, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return _inner.MarkRejectedAsync(u, cid, reason, ct); }
    }

    /// <summary>Journal that reports when a claim LOSER looks the row up. Only the loser calls
    /// GetAsync, so the winner can be held inside the exchange call until the loser has answered —
    /// which pins the race to the interesting interleaving instead of leaving it to the scheduler.</summary>
    private sealed class RaceJournal : IOrderJournal
    {
        private readonly InMemoryOrderJournal _inner = new();
        public readonly TaskCompletionSource LoserLookedUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, TradeOrderRow> Rows => _inner.Rows;
        public Task<bool> TryClaimAsync(TradeOrderRow row, CancellationToken ct) => _inner.TryClaimAsync(row, ct);
        public async Task<TradeOrderRow?> GetAsync(Guid u, string cid, CancellationToken ct)
        {
            var row = await _inner.GetAsync(u, cid, ct);
            LoserLookedUp.TrySetResult();
            return row;
        }
        public Task MarkPlacedAsync(Guid u, string cid, string exId, CancellationToken ct) => _inner.MarkPlacedAsync(u, cid, exId, ct);
        public Task MarkRejectedAsync(Guid u, string cid, string reason, CancellationToken ct) => _inner.MarkRejectedAsync(u, cid, reason, ct);
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

    private static TradingService Make(
        FakeGateway gw, IOrderJournal journal, decimal cap = 1_000_000m, IPriceSource? price = null) =>
        new(new FakeKeyProvider(new CexKeyMaterial(new byte[1], new byte[1], "trade")),
            new FakeCipher(),
            new FakeGatewayFactory(gw),
            price ?? new FakePriceSource(Price),
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
        Assert.Equal("EX-777", second.OrderId); // the replay reports the recorded exchange id
        Assert.Equal(1, gw.PlaceCalls);         // no second placement
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

    // ── C1: a live order must never be reported as rejected ──────────────────
    [Fact]
    public async Task Journal_failure_after_a_successful_place_still_reports_accepted()
    {
        var gw = new FakeGateway();
        var journal = new MarkPlacedThrowsJournal();
        var svc = Make(gw, journal);

        var res = await svc.PlaceMarketAsync(Uid, Cmd(), default); // must not throw

        Assert.True(res.Accepted);           // the exchange has the order — never say "rejected"
        Assert.Equal("EX-777", res.OrderId);
        Assert.Null(res.RejectReason);
        Assert.Equal(1, gw.PlaceCalls);
        Assert.Equal(1, journal.MarkPlacedCalls);
    }

    [Fact]
    public async Task A_cancelled_request_token_does_not_turn_a_placed_order_into_a_rejection()
    {
        var gw = new FakeGateway();
        var journal = new CtHonouringJournal();
        var svc = Make(gw, journal);
        using var cts = new CancellationTokenSource();
        var exchangeAccepts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gw.InFlightGate = exchangeAccepts.Task;

        var call = Task.Run(() => svc.PlaceMarketAsync(Uid, Cmd(cid: "cid-cancel"), cts.Token));
        for (var i = 0; gw.PlaceCalls == 0 && i < 5_000; i++) await Task.Delay(1); // order is in flight
        cts.Cancel();                 // the caller walks away mid-flight
        exchangeAccepts.SetResult();  // ...and only then does the exchange accept it

        var res = await call;         // must not throw

        Assert.True(res.Accepted);
        Assert.Equal("EX-777", res.OrderId);
        // Journaling the placement must not ride on the caller's token, or a live order is
        // recorded (and reported) as a rejection.
        Assert.Equal("placed", Assert.Single(journal.Rows).Value.Status);
    }

    // ── C2: idempotency must survive two truly concurrent requests ───────────
    [Fact]
    public async Task Two_concurrent_calls_with_the_same_client_order_id_place_exactly_once()
    {
        var gw = new FakeGateway();
        var journal = new RaceJournal();
        // Both calls are released from the price gate at the same instant, so they race
        // into the journal claim rather than running one after the other. The claim winner
        // then stays inside the exchange call until the loser has resolved its own outcome,
        // so both attempts really are in flight at the same time.
        var price = new RendezvousPriceSource(Price, parties: 2);
        gw.InFlightGate = journal.LoserLookedUp.Task;
        var svc = Make(gw, journal, price: price);

        var a = Task.Run(() => svc.PlaceMarketAsync(Uid, Cmd(cid: "race-1"), default));
        var b = Task.Run(() => svc.PlaceMarketAsync(Uid, Cmd(cid: "race-1"), default));
        var results = await Task.WhenAll(a, b);

        Assert.Equal(1, gw.PlaceCalls);                       // exactly one real order
        Assert.Equal(1, results.Count(r => r.Accepted));      // exactly one caller told "accepted"
        Assert.Single(journal.Rows);
        Assert.Equal("placed", Assert.Single(journal.Rows).Value.Status);

        var loser = Assert.Single(results, r => !r.Accepted);
        Assert.Contains("in flight", loser.RejectReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(loser.OrderId);
    }

    // ── C3: 'placed' is terminal ─────────────────────────────────────────────
    [Fact]
    public async Task Mark_rejected_never_downgrades_a_placed_row()
    {
        var gw = new FakeGateway();
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var res = await svc.PlaceMarketAsync(Uid, Cmd(cid: "terminal-1"), default);
        Assert.True(res.Accepted);

        await journal.MarkRejectedAsync(Uid, "terminal-1", "late failure", default);

        var row = Assert.Single(journal.Rows).Value;
        Assert.Equal("placed", row.Status);
        Assert.Equal("EX-777", row.ExchangeOrderId);
        Assert.Null(row.RejectReason);
    }

    // ── replay reports the recorded outcome, not a blanket success ───────────
    [Fact]
    public async Task Replaying_a_rejected_client_order_id_reports_the_stored_rejection()
    {
        var gw = new FakeGateway { Throw = true };
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var first = await svc.PlaceMarketAsync(Uid, Cmd(cid: "replay-1"), default);
        Assert.False(first.Accepted);

        var replay = await svc.PlaceMarketAsync(Uid, Cmd(cid: "replay-1"), default);

        Assert.False(replay.Accepted);
        Assert.Null(replay.OrderId);
        Assert.Contains("exchange down", replay.RejectReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, gw.PlaceCalls); // the replay placed nothing
    }

    [Fact]
    public async Task An_in_flight_duplicate_is_refused_rather_than_reported_as_accepted()
    {
        var gw = new FakeGateway();
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        // Someone else already claimed the key and has not finished yet.
        Assert.True(await journal.TryClaimAsync(new TradeOrderRow(
            Uid, "binance", "inflight-1", null, "BTCUSDT", "buy", 0.01m, false, "accepted", null), default));

        var res = await svc.PlaceMarketAsync(Uid, Cmd(cid: "inflight-1"), default);

        Assert.False(res.Accepted);
        Assert.Contains("in flight", res.RejectReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, gw.PlaceCalls);
    }

    // ── I4: leverage/margin mode must actually reach the venue ───────────────
    // OKX and Bybit ignore the leverage/marginMode carried on the order object, so without an
    // explicit call the user's 3× silently becomes whatever the account was last left on.
    [Fact]
    public async Task Place_market_applies_leverage_and_margin_mode_before_placing_the_order()
    {
        var gw = new FakeGateway();
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var res = await svc.PlaceMarketAsync(Uid, Cmd(cid: "lev-1"), default);

        Assert.True(res.Accepted);
        Assert.Equal(("BTCUSDT", 5), gw.LeverageSet);
        Assert.Equal(("BTCUSDT", FuturesMarginMode.Isolated), gw.MarginModeSet);
        // Both settings must land BEFORE the order, or the fill uses the stale account setting.
        Assert.Equal(new[] { "leverage", "marginMode", "place" }, gw.CallLog);
    }

    [Fact]
    public async Task A_failing_set_leverage_does_not_block_the_order()
    {
        // Venues routinely reject re-setting the SAME leverage ("leverage not modified"), and a
        // gateway with no futures support throws NotSupportedException. Neither may kill a
        // legitimate order — the desktop's EnsureManualFuturesSetupAsync logs and proceeds.
        var gw = new FakeGateway { ThrowOnSetLeverage = true, ThrowOnSetMarginMode = true };
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var res = await svc.PlaceMarketAsync(Uid, Cmd(cid: "lev-2"), default);

        Assert.True(res.Accepted);
        Assert.Equal("EX-777", res.OrderId);
        Assert.Equal(1, gw.PlaceCalls);
        Assert.Equal("placed", Assert.Single(journal.Rows).Value.Status);
    }

    // ── C4: cancel must carry the symbol or it is a silent no-op ─────────────
    [Fact]
    public async Task Cancel_passes_the_symbol_to_the_gateway()
    {
        var gw = new FakeGateway();
        var journal = new InMemoryOrderJournal();
        var svc = Make(gw, journal);

        var res = await svc.CancelAsync(Uid, "binance", "BTCUSDT", "EX-777", default);

        Assert.True(res.Ok);
        Assert.Null(res.Error);
        var (symbol, orderId) = Assert.Single(gw.Cancelled);
        Assert.Equal("BTCUSDT", symbol); // the 1-arg overload would record ""
        Assert.Equal("EX-777", orderId);
    }
}
