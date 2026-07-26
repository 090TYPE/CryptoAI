using System.Reactive.Linq;
using System.Reflection;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests.Trading;

/// <summary>
/// The strategy bot's own money rules: how big a position it will open, and what it reports as
/// profit when it closes one. The reported PnL is not cosmetic — a loss is fed straight back into
/// the daily-loss budget that stops the bot trading.
/// </summary>
public class TradingBotTests
{
    // StartAsync only reaches these through MarketDataStream.Sample(5s); driving a test through
    // that sampler would cost five seconds per signal and would be testing Rx. The trade decisions
    // behind it are the unit under test, so the tests call them directly.
    private static readonly MethodInfo ExecuteBuyMethod = Method("ExecuteBuy");
    private static readonly MethodInfo ExecuteSellMethod = Method("ExecuteSell");

    private static MethodInfo Method(string name) =>
        typeof(TradingBot).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"TradingBot.{name}(decimal) is gone — update these tests.");

    private static Task BuyAsync(TradingBot bot, decimal price) => (Task)ExecuteBuyMethod.Invoke(bot, [price])!;
    private static Task SellAsync(TradingBot bot, decimal price) => (Task)ExecuteSellMethod.Invoke(bot, [price])!;

    private sealed class FakeGateway : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;

        public readonly List<Order> Placed = [];
        public decimal Balance = 1_000_000m;
        public List<FuturesPosition> Positions = [];

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order order)
        {
            Placed.Add(order);
            order.Status = OrderStatus.Filled;
            return Task.FromResult(order);
        }
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(Balance);
        // The spot path routes through MarketOrderRouter, which needs both sides of the book.
        public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => Task.FromResult(new OrderBook
        {
            Symbol = symbol,
            Bids = [new OrderBookLevel { Price = 99m, Quantity = 100m }],
            Asks = [new OrderBookLevel { Price = 101m, Quantity = 100m }],
        });
        public Task<IReadOnlyList<FuturesPosition>> GetOpenPositionsAsync()
            => Task.FromResult<IReadOnlyList<FuturesPosition>>(Positions);
        public Task SetLeverageAsync(string symbol, int leverage) => Task.CompletedTask;
        public Task SetMarginModeAsync(string symbol, FuturesMarginMode marginMode) => Task.CompletedTask;
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    private const string Sym = "ETHUSDT";

    private static TradingBot Spot(FakeGateway gw, decimal quantity = 1m, decimal maxRiskPerTrade = 200m,
        decimal? maxPositionSizeUsd = null)
        => new(gw, Sym, quantity, TradingMarketType.Spot, maxRiskPerTrade: maxRiskPerTrade,
               maxPositionSizeUsd: maxPositionSizeUsd);

    private static TradingBot Futures(FakeGateway gw, decimal quantity = 1m,
        FuturesTradeBias bias = FuturesTradeBias.LongShort)
        => new(gw, Sym, quantity, TradingMarketType.FuturesUsdM, leverage: 1, maxRiskPerTrade: 200m,
               futuresBias: bias);

    private sealed record Closed(string Symbol, string Direction, decimal Entry, decimal Exit, decimal Qty, decimal Pnl);

    private static List<Closed> Track(TradingBot bot)
    {
        List<Closed> closes = [];
        bot.OnTradeClosed += (s, d, entry, exit, qty, pnl) => closes.Add(new Closed(s, d, entry, exit, qty, pnl));
        return closes;
    }

    // ── position cap ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_default_position_cap_is_money_not_quantity()
    {
        // The cap used to fall back to tradeQuantity × 50 000, which the risk manager then compared
        // against quantity × price — so it only ever bit on instruments dearer than 50 000 a unit.
        // One unit of a 1 100 USD coin has to be over a 1 000 USD ceiling.
        var gw = new FakeGateway();

        await BuyAsync(Spot(gw), 900m);
        Assert.Single(gw.Placed);

        gw.Placed.Clear();
        await BuyAsync(Spot(gw), 1_100m);
        Assert.Empty(gw.Placed);
    }

    [Fact]
    public async Task The_cap_the_ui_passes_in_wins_over_the_default()
    {
        var gw = new FakeGateway();

        await BuyAsync(Spot(gw, maxPositionSizeUsd: 500m), 400m);
        Assert.Single(gw.Placed);

        gw.Placed.Clear();
        await BuyAsync(Spot(gw, maxPositionSizeUsd: 500m), 600m);
        Assert.Empty(gw.Placed);
    }

    // ── realised PnL ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_spot_long_reports_the_profit_it_actually_made()
    {
        var gw = new FakeGateway();
        var bot = Spot(gw);
        var closes = Track(bot);

        await BuyAsync(bot, 100m);
        await SellAsync(bot, 110m);

        var close = Assert.Single(closes);
        Assert.Equal(Sym, close.Symbol);
        Assert.Equal("Long", close.Direction);
        Assert.Equal(100m, close.Entry);
        Assert.Equal(110m, close.Exit);
        Assert.Equal(1m, close.Qty);
        Assert.Equal(10m, close.Pnl);
    }

    [Fact]
    public async Task A_spot_sell_without_an_open_long_is_not_a_short()
    {
        // Spot cannot go short: a strategy that emits SELL first would be selling an asset the
        // account does not hold.
        var gw = new FakeGateway();
        var bot = Spot(gw);
        var closes = Track(bot);

        await SellAsync(bot, 110m);

        Assert.Empty(gw.Placed);
        Assert.Empty(closes);
    }

    [Fact]
    public async Task A_futures_long_that_closes_higher_reports_a_profit()
    {
        var gw = new FakeGateway();
        var bot = Futures(gw);
        var closes = Track(bot);

        await BuyAsync(bot, 100m);
        gw.Positions = [new FuturesPosition { Symbol = Sym, Quantity = 1m, PositionSide = FuturesPositionSide.Long }];

        await SellAsync(bot, 110m);

        var close = Assert.Single(closes);
        Assert.Equal("Long", close.Direction);
        Assert.Equal(10m, close.Pnl);
        var exit = gw.Placed[^1];
        Assert.True(exit.ReduceOnly);
        Assert.Equal(FuturesPositionSide.Long, exit.PositionSide);
    }

    [Fact]
    public async Task A_futures_short_that_closes_lower_reports_a_profit()
    {
        // The short's PnL is (entry − exit): sign-flipping this turns a winning day into a losing
        // one on the dashboard AND feeds a phantom loss into the daily-loss budget.
        var gw = new FakeGateway();
        var bot = Futures(gw);
        var closes = Track(bot);

        await SellAsync(bot, 100m);
        gw.Positions = [new FuturesPosition { Symbol = Sym, Quantity = -1m, PositionSide = FuturesPositionSide.Short }];

        await BuyAsync(bot, 90m);

        var close = Assert.Single(closes);
        Assert.Equal("Short", close.Direction);
        Assert.Equal(100m, close.Entry);
        Assert.Equal(90m, close.Exit);
        Assert.Equal(10m, close.Pnl);
        var exit = gw.Placed[^1];
        Assert.True(exit.ReduceOnly);
        Assert.Equal(FuturesPositionSide.Short, exit.PositionSide);
    }

    [Fact]
    public async Task A_futures_short_that_closes_higher_reports_a_loss()
    {
        var gw = new FakeGateway();
        var bot = Futures(gw);
        var closes = Track(bot);

        await SellAsync(bot, 100m);
        gw.Positions = [new FuturesPosition { Symbol = Sym, Quantity = -1m, PositionSide = FuturesPositionSide.Short }];

        await BuyAsync(bot, 130m);

        Assert.Equal(-30m, Assert.Single(closes).Pnl);
    }

    [Fact]
    public async Task A_realised_loss_spends_the_daily_budget_and_stops_the_bot()
    {
        // maxRiskPerTrade 200 → a 200 USD daily-loss cap. The loss only lands in that budget if
        // ReportTradeClosed sees a negative PnL, so this pins the sign as well as the wiring.
        var gw = new FakeGateway();
        var bot = Spot(gw, maxRiskPerTrade: 200m);

        await BuyAsync(bot, 500m);
        await SellAsync(bot, 100m);     // −400 USD
        gw.Placed.Clear();

        await BuyAsync(bot, 100m);

        Assert.Empty(gw.Placed);
    }

    // ── entry guards ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_existing_futures_long_is_not_pyramided()
    {
        // AttachTpSl re-arms protection for _tradeQuantity only, so a second entry would leave most
        // of the position with no stop behind it.
        var gw = new FakeGateway
        {
            Positions = [new FuturesPosition { Symbol = Sym, Quantity = 1m, PositionSide = FuturesPositionSide.Long }],
        };

        await BuyAsync(Futures(gw), 100m);

        Assert.Empty(gw.Placed);
    }

    [Fact]
    public async Task A_long_only_bot_never_opens_a_short()
    {
        var gw = new FakeGateway();

        await SellAsync(Futures(gw, bias: FuturesTradeBias.LongOnly), 100m);

        Assert.Empty(gw.Placed);
    }

    [Fact]
    public async Task A_short_only_bot_never_opens_a_long()
    {
        var gw = new FakeGateway();

        await BuyAsync(Futures(gw, bias: FuturesTradeBias.ShortOnly), 100m);

        Assert.Empty(gw.Placed);
    }
}
