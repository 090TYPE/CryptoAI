using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests.Trading;

/// <summary>
/// The one component standing between a triggered stop and an unprotected position. Every test
/// here pins a failure mode that costs money when it regresses: a rejected close that is never
/// retried, a native stop cancelled before its replacement exists, a trailing level the software
/// believes in while the venue holds another one.
/// </summary>
public class TpSlManagerTests
{
    // ── driving prices ───────────────────────────────────────────────────────
    // AttachAsync wraps the price stream in .Sample(3s), so feeding a Subject would cost three
    // seconds per tick and would be exercising Rx rather than the manager. The handler behind the
    // sampler is the actual unit under test, so these tests call it directly.
    private static readonly MethodInfo HandlePrice =
        typeof(TpSlManager).GetMethod("HandlePrice", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TpSlManager.HandlePrice(decimal) is gone — update these tests.");

    private static void Tick(TpSlManager mgr, decimal price) => HandlePrice.Invoke(mgr, [price]);

    // ── fakes ────────────────────────────────────────────────────────────────
    private readonly record struct Trigger(string Symbol, OrderSide Side, decimal Quantity, decimal Price, bool ReduceOnly);

    private sealed class FakeGateway : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;

        private readonly object _log = new();
        private readonly List<Order> _marketOrders = [];
        private readonly List<Trigger> _stopLosses = [];
        private readonly List<Trigger> _takeProfits = [];
        private readonly List<(string Symbol, string OrderId)> _cancelled = [];
        private readonly List<string> _callLog = [];
        private int _slCalls;
        private int _tpCalls;

        /// <summary>Every close/entry order the manager sent, including the ones that were rejected.</summary>
        public IReadOnlyList<Order> MarketOrders { get { lock (_log) return _marketOrders.ToArray(); } }
        /// <summary>Every native stop-loss placement attempt, in order.</summary>
        public IReadOnlyList<Trigger> StopLosses { get { lock (_log) return _stopLosses.ToArray(); } }
        public IReadOnlyList<Trigger> TakeProfits { get { lock (_log) return _takeProfits.ToArray(); } }
        public IReadOnlyList<(string Symbol, string OrderId)> Cancelled { get { lock (_log) return _cancelled.ToArray(); } }
        /// <summary>Every call in the order it arrived — the only way to assert place-before-cancel.</summary>
        public IReadOnlyList<string> CallLog { get { lock (_log) return _callLog.ToArray(); } }

        /// <summary>Venue with no native TP/SL endpoint at all — the software-simulation path.</summary>
        public bool StopLossNotSupported;
        public bool TakeProfitNotSupported;
        /// <summary>1-based placement numbers that must be rejected (rate limit, bad trigger price).</summary>
        public readonly HashSet<int> FailStopLossCalls = [];
        public readonly HashSet<int> FailTakeProfitCalls = [];
        public bool MarketOrderThrows;
        /// <summary>False models a venue that accepts the stop but echoes no order id back.</summary>
        public bool ReportOrderIds = true;
        /// <summary>1-based stop-loss placement that stays in flight until <see cref="Gate"/> completes.</summary>
        public int GateStopLossCall;
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;

        // Every placement completes synchronously unless a test explicitly gates it, so a
        // fire-and-forget close or trailing update has finished by the time a Tick call returns.
        public Task<Order> PlaceOrderAsync(Order order)
        {
            lock (_log) { _marketOrders.Add(order); _callLog.Add("market"); }
            if (MarketOrderThrows)
                return Task.FromException<Order>(new InvalidOperationException("exchange rejected the close"));
            order.Status = OrderStatus.Filled;
            return Task.FromResult(order);
        }

        public async Task<Order> PlaceStopLossOrderAsync(string symbol, OrderSide side, decimal quantity,
            decimal triggerPrice, FuturesPositionSide positionSide, bool reduceOnly = true)
        {
            if (StopLossNotSupported) throw new NotSupportedException("no native stop-loss here");
            int call;
            lock (_log)
            {
                call = ++_slCalls;
                _stopLosses.Add(new Trigger(symbol, side, quantity, triggerPrice, reduceOnly));
                _callLog.Add("sl");
            }
            if (GateStopLossCall == call) await Gate.Task;
            if (FailStopLossCalls.Contains(call)) throw new InvalidOperationException("stop-loss rejected");
            return new Order { Id = ReportOrderIds ? "SL" + call : null!, Symbol = symbol, Side = side, Quantity = quantity };
        }

        public Task<Order> PlaceTakeProfitOrderAsync(string symbol, OrderSide side, decimal quantity,
            decimal triggerPrice, FuturesPositionSide positionSide, bool reduceOnly = true)
        {
            if (TakeProfitNotSupported) throw new NotSupportedException("no native take-profit here");
            int call;
            lock (_log)
            {
                call = ++_tpCalls;
                _takeProfits.Add(new Trigger(symbol, side, quantity, triggerPrice, reduceOnly));
                _callLog.Add("tp");
            }
            if (FailTakeProfitCalls.Contains(call)) throw new InvalidOperationException("take-profit rejected");
            return Task.FromResult(new Order { Id = ReportOrderIds ? "TP" + call : null!, Symbol = symbol });
        }

        /// <summary>The 1-arg overload cannot address a Binance futures order — recording no symbol makes that visible.</summary>
        public Task CancelOrderAsync(string orderId)
        { lock (_log) { _cancelled.Add(("", orderId)); _callLog.Add("cancel"); } return Task.CompletedTask; }
        public Task CancelOrderAsync(string symbol, string orderId)
        { lock (_log) { _cancelled.Add((symbol, orderId)); _callLog.Add("cancel"); } return Task.CompletedTask; }

        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(10_000m);
        public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => Task.FromResult<OrderBook>(null!);
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private const string Sym = "BTCUSDT";
    private const decimal Entry = 100m;

    private sealed record Harness(TpSlManager Manager, FakeGateway Gateway, Subject<MarketData> Stream,
        List<string> Events, List<string> ProtectionLost);

    private static async Task<Harness> AttachAsync(
        TpSlConfig cfg,
        FakeGateway gw,
        TradingMarketType market = TradingMarketType.FuturesUsdM,
        OrderSide side = OrderSide.Buy,
        decimal quantity = 1m)
    {
        var stream = new Subject<MarketData>();
        var mgr = new TpSlManager(cfg);
        List<string> events = [];
        List<string> lost = [];
        mgr.OnEvent += events.Add;
        mgr.OnProtectionLost += lost.Add;

        await mgr.AttachAsync(Sym, side, Entry, quantity,
            side == OrderSide.Buy ? FuturesPositionSide.Long : FuturesPositionSide.Short,
            market, gw, stream);

        return new Harness(mgr, gw, stream, events, lost);
    }

    // ── attach: native orders and the fall-back to software ──────────────────

    [Fact]
    public async Task Native_futures_tp_and_sl_are_placed_and_no_price_stream_is_needed()
    {
        var gw = new FakeGateway();
        var h = await AttachAsync(new TpSlConfig { TpEnabled = true, TpPercent = 2m, SlEnabled = true, SlPercent = 1m }, gw);

        var sl = Assert.Single(gw.StopLosses);
        Assert.Equal(99m, sl.Price);
        Assert.Equal(OrderSide.Sell, sl.Side);   // closing side of a long
        Assert.True(sl.ReduceOnly);

        var tp = Assert.Single(gw.TakeProfits);
        Assert.Equal(102m, tp.Price);

        // The venue holds both legs — subscribing anyway would run a second, redundant simulation.
        Assert.False(h.Stream.HasObservers);
    }

    [Fact]
    public async Task A_venue_without_native_tp_sl_falls_back_to_the_software_stream()
    {
        var gw = new FakeGateway { StopLossNotSupported = true, TakeProfitNotSupported = true };
        var h = await AttachAsync(new TpSlConfig { TpEnabled = true, SlEnabled = true, SlPercent = 1m }, gw);

        Assert.True(h.Stream.HasObservers);
        Assert.Contains(h.Events, e => e.Contains("software simulation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_rejected_take_profit_cancels_the_stop_that_is_already_live()
    {
        // Only ONE leg reached the venue. Leaving it there while switching to simulation gives the
        // position two stops that do not know about each other; leaving the native flag on instead
        // would suppress the simulation and run the position with half its protection.
        var gw = new FakeGateway();
        gw.FailTakeProfitCalls.Add(1);
        var h = await AttachAsync(new TpSlConfig { TpEnabled = true, TpPercent = 2m, SlEnabled = true, SlPercent = 1m }, gw);

        Assert.Equal((Sym, "SL1"), Assert.Single(gw.Cancelled));
        Assert.True(h.Stream.HasObservers);

        // ...and the simulation really is armed: a price under the stop closes the position.
        Tick(h.Manager, 98m);
        Assert.Single(gw.MarketOrders);
    }

    [Fact]
    public async Task A_rejected_second_partial_take_profit_cancels_the_first_one_and_the_stop()
    {
        var gw = new FakeGateway();
        gw.FailTakeProfitCalls.Add(2);
        var cfg = new TpSlConfig
        {
            TpEnabled = true, TpPercent = 2m, SlEnabled = true, SlPercent = 1m,
            PartialTp = true, PartialTpClosePercent = 50m, PartialTp2Percent = 4m,
        };

        await AttachAsync(cfg, gw, quantity: 0.5m);

        var cancelled = gw.Cancelled;
        Assert.Contains((Sym, "SL1"), cancelled);
        Assert.Contains((Sym, "TP1"), cancelled);   // the half that DID reach the venue
        Assert.Equal(2, cancelled.Count);
    }

    [Fact]
    public async Task Detach_cancels_the_native_stop_with_its_symbol()
    {
        var gw = new FakeGateway();
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m }, gw);

        await h.Manager.DetachAsync();

        var (symbol, orderId) = Assert.Single(gw.Cancelled);
        Assert.Equal(Sym, symbol);   // the 1-arg overload would record ""
        Assert.Equal("SL1", orderId);
    }

    // ── a triggered stop that could not be executed ──────────────────────────

    [Fact]
    public async Task A_rejected_close_re_arms_the_trigger_instead_of_latching_closed()
    {
        // The position is still open after the rejection. Latching on the first attempt (the old
        // _closed flag) left it sitting with no protection at the exact moment its stop had fired.
        var gw = new FakeGateway { StopLossNotSupported = true, MarketOrderThrows = true };
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m }, gw);

        Tick(h.Manager, 98m);
        Tick(h.Manager, 97m);

        Assert.Equal(2, gw.MarketOrders.Count);
        Assert.Equal(2, h.ProtectionLost.Count);
        Assert.All(h.ProtectionLost, m => Assert.Contains("still open", m, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_close_that_went_through_is_final_and_later_ticks_place_nothing()
    {
        var gw = new FakeGateway { StopLossNotSupported = true };
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m }, gw);

        Tick(h.Manager, 98m);
        Tick(h.Manager, 97m);

        var close = Assert.Single(gw.MarketOrders);
        Assert.Equal(OrderSide.Sell, close.Side);
        Assert.True(close.ReduceOnly);
        Assert.Equal(FuturesPositionSide.Long, close.PositionSide);
        Assert.Empty(h.ProtectionLost);
    }

    [Fact]
    public async Task A_short_closes_when_price_rises_through_its_stop()
    {
        var gw = new FakeGateway { StopLossNotSupported = true };
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m }, gw, side: OrderSide.Sell);

        Tick(h.Manager, 100.5m);          // above entry but under the 101 stop
        Assert.Empty(gw.MarketOrders);

        Tick(h.Manager, 101.5m);
        var close = Assert.Single(gw.MarketOrders);
        Assert.Equal(OrderSide.Buy, close.Side);
        Assert.Equal(FuturesPositionSide.Short, close.PositionSide);
    }

    // ── trailing stop ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rejected_trailing_move_rolls_the_software_level_back_to_what_the_venue_holds()
    {
        var gw = new FakeGateway();
        gw.FailStopLossCalls.Add(2);   // the first trailing move is refused
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true }, gw);

        Tick(h.Manager, 110m);   // would move the stop 99 → 108.90, but the venue says no

        Assert.Equal(2, gw.StopLosses.Count);
        Assert.Contains(h.Events, e => e.Contains("keeping stop at", StringComparison.OrdinalIgnoreCase));

        // The venue still holds 99, so a move to 108.9495 is a genuine improvement and must be
        // attempted. Had the software kept believing in the rejected 108.90, this move would sit
        // under the 0.1% spam filter and never be sent — the stop would freeze for good.
        Tick(h.Manager, 110.05m);

        Assert.Equal(3, gw.StopLosses.Count);
        Assert.Equal(108.9495m, gw.StopLosses[2].Price);
        Assert.Contains((Sym, "SL1"), gw.Cancelled);   // only replaced once the new one exists
    }

    [Fact]
    public async Task The_new_trailing_stop_is_placed_before_the_old_one_is_cancelled()
    {
        var gw = new FakeGateway();
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true }, gw);

        Tick(h.Manager, 110m);

        Assert.Equal(2, gw.StopLosses.Count);
        Assert.Equal(108.9m, gw.StopLosses[1].Price);
        Assert.Equal((Sym, "SL1"), Assert.Single(gw.Cancelled));
        // Cancel-then-place would leave the position naked for the length of one round-trip —
        // and with nothing on the venue at all if the placement then failed.
        Assert.Equal(new[] { "sl", "sl", "cancel" }, gw.CallLog);
    }

    [Fact]
    public async Task Losing_the_trailing_stop_entirely_drops_to_the_in_app_stop_and_says_so()
    {
        // A venue that accepts the stop but echoes no id back: there is nothing to fall back on
        // when the replacement is refused, so the position is genuinely unprotected on the exchange.
        var gw = new FakeGateway { ReportOrderIds = false };
        gw.FailStopLossCalls.Add(2);
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true }, gw);

        Tick(h.Manager, 110m);

        var lost = Assert.Single(h.ProtectionLost);
        Assert.Contains("in-app stop", lost, StringComparison.OrdinalIgnoreCase);

        // Simulation must now be live, or the "in-app stop" the message promises does not exist.
        Tick(h.Manager, 98m);
        Assert.Single(gw.MarketOrders);
    }

    [Fact]
    public async Task A_trailing_move_that_arrived_during_an_update_is_replayed_not_dropped()
    {
        // The tick that finds the update semaphore taken has already moved the software level.
        // Dropping it would leave the venue's stop permanently behind what the app believes in.
        var gw = new FakeGateway { GateStopLossCall = 2 };
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true }, gw);

        Tick(h.Manager, 110m);   // update to 108.90 goes in flight and stays there
        Assert.Equal(2, gw.StopLosses.Count);

        Tick(h.Manager, 120m);   // 118.80 arrives while the semaphore is held
        Assert.Equal(2, gw.StopLosses.Count);

        gw.Gate.SetResult();
        for (var i = 0; gw.StopLosses.Count < 3 && i < 5_000; i++) await Task.Delay(1);

        Assert.Equal(3, gw.StopLosses.Count);
        Assert.Equal(118.8m, gw.StopLosses[2].Price);
    }

    [Fact]
    public async Task The_trailing_stop_never_moves_against_the_position()
    {
        var gw = new FakeGateway();
        var h = await AttachAsync(new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true }, gw);

        Tick(h.Manager, 110m);   // stop → 108.90
        Tick(h.Manager, 105m);   // pull-back: the stop stays where it is
        Tick(h.Manager, 109m);

        Assert.Equal(2, gw.StopLosses.Count);
        Assert.Equal(108.9m, gw.StopLosses[1].Price);
    }
}
