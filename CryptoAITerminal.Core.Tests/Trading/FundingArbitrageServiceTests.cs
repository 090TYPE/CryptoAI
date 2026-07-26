using System.Reactive.Linq;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests.Trading;

/// <summary>
/// The arb is only market-neutral while BOTH legs exist. Every test here is about the moment one
/// leg fails: a spot buy left behind by a rejected perp short is a naked long, and a spot sale made
/// before the perp short is flat is a naked short — either one turns a funding trade into a
/// directional bet nobody placed.
/// </summary>
public class FundingArbitrageServiceTests
{
    /// <summary>Both legs share one <paramref name="log"/> so the order of the two round-trips is visible.</summary>
    private sealed class FakeGateway(string name, List<string> log) : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;

        public readonly List<Order> Placed = [];
        public (string Symbol, int Leverage)? LeverageSet;
        /// <summary>Message the next placement fails with; cleared after it fires when <see cref="FailOnce"/>.</summary>
        public string? PlaceError;
        public bool FailOnce;

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order order)
        {
            log.Add($"{name}:{order.Side}");
            if (PlaceError is { } error)
            {
                if (FailOnce) PlaceError = null;
                return Task.FromException<Order>(new InvalidOperationException(error));
            }
            Placed.Add(order);
            order.Status = OrderStatus.Filled;
            return Task.FromResult(order);
        }
        public Task SetLeverageAsync(string symbol, int leverage) { LeverageSet = (symbol, leverage); return Task.CompletedTask; }
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(100_000m);
        public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => Task.FromResult<OrderBook>(null!);
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    private sealed record Legs(FakeGateway Spot, FakeGateway Fut, List<string> Log, FundingArbitrageService Service);

    private static Legs MakeLegs()
    {
        List<string> log = [];
        var spot = new FakeGateway("spot", log);
        var fut = new FakeGateway("fut", log);
        return new Legs(spot, fut, log, new FundingArbitrageService(spot, fut, null, null, null, null));
    }

    private static FundingArbitrageOpportunity Opportunity(string exchange = "Binance") => new()
    {
        Exchange = exchange, Symbol = "BTCUSDT", FundingRatePct = 0.05m,
        AnnualizedPct = 54.75m, MarkPrice = 25_000m, NextFundingTime = DateTime.UtcNow.AddHours(8),
    };

    // ── opening ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_buys_spot_and_shorts_the_same_quantity_of_perp()
    {
        var legs = MakeLegs();

        var (ok, error) = await legs.Service.OpenPositionAsync(Opportunity());

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal(("BTCUSDT", 3), legs.Fut.LeverageSet);

        var buy = Assert.Single(legs.Spot.Placed);
        var sell = Assert.Single(legs.Fut.Placed);
        Assert.Equal(OrderSide.Buy, buy.Side);
        Assert.Equal(OrderSide.Sell, sell.Side);
        Assert.Equal(0.02m, buy.Quantity);            // 500 USD notional / 25 000 mark
        Assert.Equal(buy.Quantity, sell.Quantity);    // any mismatch is naked delta
        Assert.False(sell.ReduceOnly);

        var pos = Assert.Single(legs.Service.OpenPositions);
        Assert.Equal(500m, pos.NotionalUsd);
        Assert.Equal(0.02m, pos.SpotQty);
    }

    [Fact]
    public async Task A_rejected_perp_leg_unwinds_the_spot_buy_and_tracks_nothing()
    {
        var legs = MakeLegs();
        legs.Fut.PlaceError = "margin is insufficient";

        var (ok, error) = await legs.Service.OpenPositionAsync(Opportunity());

        Assert.False(ok);
        Assert.Contains("rolled back", error, StringComparison.OrdinalIgnoreCase);
        // The spot leg must be sold back out, or the account is left holding an untracked long
        // that this strategy will never close.
        Assert.Equal(new[] { OrderSide.Buy, OrderSide.Sell }, legs.Spot.Placed.Select(o => o.Side));
        Assert.Equal(0.02m, legs.Spot.Placed[1].Quantity);
        Assert.Empty(legs.Service.AllPositions);
    }

    [Fact]
    public async Task A_position_side_mismatch_flips_the_account_mode_and_retries_the_perp_leg()
    {
        // A one-way account rejects PositionSide.Short and a hedge account rejects Both. Giving up
        // here would abort a good arb and roll the spot leg straight back out again.
        var legs = MakeLegs();
        legs.Fut.PlaceError = "Order's position side does not match user's setting.";
        legs.Fut.FailOnce = true;

        var (ok, _) = await legs.Service.OpenPositionAsync(Opportunity());

        Assert.True(ok);
        Assert.Equal(new[] { "spot:Buy", "fut:Sell", "fut:Sell" }, legs.Log);
        Assert.Equal(FuturesPositionSide.Short, Assert.Single(legs.Fut.Placed).PositionSide);
        Assert.Single(legs.Spot.Placed);   // no rollback happened
    }

    [Fact]
    public async Task An_exchange_without_configured_keys_places_nothing()
    {
        var svc = new FundingArbitrageService(null, null, null, null, null, null);

        var (ok, error) = await svc.OpenPositionAsync(Opportunity("Bybit"));

        Assert.False(ok);
        Assert.Contains("No API credentials", error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(svc.AllPositions);
    }

    // ── closing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Closing_flattens_the_perp_before_selling_the_spot_hedge()
    {
        var legs = MakeLegs();
        Assert.True((await legs.Service.OpenPositionAsync(Opportunity())).Ok);
        var pos = Assert.Single(legs.Service.OpenPositions);

        var (ok, error) = await legs.Service.ClosePositionAsync(pos);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal(FundingArbPositionState.Closed, pos.State);
        Assert.Empty(legs.Service.OpenPositions);

        var perpClose = legs.Fut.Placed[^1];
        Assert.Equal(OrderSide.Buy, perpClose.Side);
        Assert.True(perpClose.ReduceOnly);
        Assert.Equal(0.02m, legs.Spot.Placed[^1].Quantity);
        // Selling the hedge first would leave the account short the perp with nothing against it.
        Assert.Equal(new[] { "spot:Buy", "fut:Sell", "fut:Buy", "spot:Sell" }, legs.Log);
    }

    [Fact]
    public async Task A_perp_close_that_fails_keeps_the_spot_hedge_and_reopens_the_position()
    {
        var legs = MakeLegs();
        Assert.True((await legs.Service.OpenPositionAsync(Opportunity())).Ok);
        var pos = Assert.Single(legs.Service.OpenPositions);
        legs.Fut.PlaceError = "exchange unavailable";

        var (ok, error) = await legs.Service.ClosePositionAsync(pos);

        Assert.False(ok);
        Assert.Contains("naked short", error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(legs.Spot.Placed);   // still only the opening buy — the hedge stays on
        // Back to Open, not stranded in Closing: the user has to be able to see it and retry,
        // or a live exchange position disappears from the UI.
        Assert.Equal(FundingArbPositionState.Open, pos.State);
        Assert.Single(legs.Service.OpenPositions);
    }

    [Fact]
    public async Task A_position_that_is_already_closed_cannot_be_closed_again()
    {
        var legs = MakeLegs();
        Assert.True((await legs.Service.OpenPositionAsync(Opportunity())).Ok);
        var pos = Assert.Single(legs.Service.OpenPositions);
        Assert.True((await legs.Service.ClosePositionAsync(pos)).Ok);

        var (ok, error) = await legs.Service.ClosePositionAsync(pos);

        Assert.False(ok);
        Assert.Equal("Position is not open", error);
        Assert.Equal(2, legs.Fut.Placed.Count);    // no third perp order
        Assert.Equal(2, legs.Spot.Placed.Count);
    }
}
