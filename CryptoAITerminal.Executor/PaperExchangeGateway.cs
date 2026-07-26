using System.Reactive.Linq;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Executor;

/// <summary>
/// Deterministic paper exchange for driving a grid bot without a venue. Seeded each tick with the
/// currently-resting orders and the current price; a limit order counts as filled (dropped from the
/// open set) once price has crossed it — buy fills when price ≤ its price, sell when price ≥ it.
/// Placed orders get a paper id and rest for the current tick. Moves no money.
/// </summary>
public sealed class PaperExchangeGateway : IExchangeGateway
{
    private readonly decimal _price;
    private readonly List<Order> _orders;
    private int _seq;

    public PaperExchangeGateway(decimal price, IEnumerable<Order> resting)
    {
        _price = price;
        _orders = resting.ToList();
    }

    // Nothing to authenticate against — the paper gateway never calls an exchange, so it is always
    // ready and the pre-trade guard should not hold it back.
    public bool HasPrivateApiCredentials => true;

    // A resting order is still open unless the price has crossed it.
    private bool Crossed(Order o) => o.Side == OrderSide.Buy ? _price <= o.Price : _price >= o.Price;

    public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(string? symbol = null)
        => Task.FromResult<IReadOnlyList<Order>>(_orders.Where(o => !Crossed(o)).ToList());

    public Task<Order> PlaceOrderAsync(Order order)
    {
        order.Id = "paper-" + (++_seq) + "-" + Guid.NewGuid().ToString("N")[..6];
        _orders.Add(order);
        return Task.FromResult(order);
    }

    public Task ConnectAsync() => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task CancelOrderAsync(string id) { _orders.RemoveAll(o => o.Id == id); return Task.CompletedTask; }
    public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(0m);
    public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) => Task.FromResult<OrderBook>(null!);
    public IObservable<MarketData> MarketDataStream => Observable.Empty<MarketData>();
}
