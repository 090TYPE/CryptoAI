using System.Reactive.Linq;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.OrderRouter;

namespace CryptoAITerminal.Core.Tests.Trading;

/// <summary>
/// A thin wrapper — it reads the book, picks the touch and sends a market order. The only two
/// things it can get wrong are picking the wrong side of the book and firing into an empty one.
/// </summary>
public class MarketOrderRouterTests
{
    private sealed class FakeGateway(OrderBook book) : IExchangeGateway
    {
        public bool HasPrivateApiCredentials => true;
        public Order? Placed;

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order order)
        {
            Placed = order;
            order.Id = "EX-1";
            order.Status = OrderStatus.Filled;
            return Task.FromResult(order);
        }
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(0m);
        public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => Task.FromResult(book);
        public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
    }

    private static OrderBook Book(decimal[] bids, decimal[] asks) => new()
    {
        Symbol = "BTCUSDT",
        Bids = [.. bids.Select(p => new OrderBookLevel { Price = p, Quantity = 1m })],
        Asks = [.. asks.Select(p => new OrderBookLevel { Price = p, Quantity = 1m })],
    };

    [Fact]
    public async Task Buy_takes_the_lowest_ask_however_the_venue_ordered_the_book()
    {
        // Not every venue returns the book sorted; taking Asks[0] on faith would report a
        // reference price several levels away from where the fill actually happens.
        var gw = new FakeGateway(Book(bids: [99m, 98m], asks: [103m, 101m, 102m]));

        var result = await new MarketOrderRouter(gw).BuyMarketAsync("BTCUSDT", 0.25m);

        Assert.Equal(101m, result.Price);
        Assert.Equal(OrderSide.Buy, gw.Placed!.Side);
        Assert.Equal(OrderType.Market, gw.Placed!.Type);
        Assert.Equal(0.25m, gw.Placed!.Quantity);
        Assert.Equal("EX-1", result.Id);   // the router hands back what the venue answered
    }

    [Fact]
    public async Task Sell_takes_the_highest_bid_however_the_venue_ordered_the_book()
    {
        var gw = new FakeGateway(Book(bids: [97m, 99m, 98m], asks: [101m]));

        var result = await new MarketOrderRouter(gw).SellMarketAsync("BTCUSDT", 0.25m);

        Assert.Equal(99m, result.Price);
        Assert.Equal(OrderSide.Sell, gw.Placed!.Side);
    }

    [Fact]
    public async Task Buying_into_an_empty_book_is_refused_rather_than_sent_blind()
    {
        var gw = new FakeGateway(Book(bids: [99m], asks: []));

        await Assert.ThrowsAnyAsync<Exception>(() => new MarketOrderRouter(gw).BuyMarketAsync("BTCUSDT", 1m));
        Assert.Null(gw.Placed);
    }

    [Fact]
    public async Task Selling_into_an_empty_book_is_refused_rather_than_sent_blind()
    {
        var gw = new FakeGateway(Book(bids: [], asks: [101m]));

        await Assert.ThrowsAnyAsync<Exception>(() => new MarketOrderRouter(gw).SellMarketAsync("BTCUSDT", 1m));
        Assert.Null(gw.Placed);
    }
}
