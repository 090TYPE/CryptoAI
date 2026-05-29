using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.OrderRouter;

public class MarketOrderRouter
{
    private readonly IExchangeGateway _gateway;

    public MarketOrderRouter(IExchangeGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// Мгновенная покупка по рынку. Покупаем по лучшей цене ASK.
    /// </summary>
    public async Task<Order> BuyMarketAsync(string symbol, decimal quantity)
    {
        var orderBook = await _gateway.GetOrderBookAsync(symbol, depth: 10);
        if (orderBook.Asks == null || !orderBook.Asks.Any())
            throw new Exception("No asks in order book");

        // Находим лучшую цену продавца (самый низкий ASK)
        var bestAsk = orderBook.Asks.OrderBy(a => a.Price).First();
        var order = new Order
        {
            Symbol = symbol,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = quantity,
            Price = bestAsk.Price, // ориентир, для маркет ордера цена не обязательна
            Status = OrderStatus.New
        };
        return await _gateway.PlaceOrderAsync(order);
    }

    /// <summary>
    /// Мгновенная продажа по рынку. Продаём по лучшей цене BID.
    /// </summary>
    public async Task<Order> SellMarketAsync(string symbol, decimal quantity)
    {
        var orderBook = await _gateway.GetOrderBookAsync(symbol, depth: 10);
        if (orderBook.Bids == null || !orderBook.Bids.Any())
            throw new Exception("No bids in order book");

        var bestBid = orderBook.Bids.OrderByDescending(b => b.Price).First();
        var order = new Order
        {
            Symbol = symbol,
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = quantity,
            Price = bestBid.Price,
            Status = OrderStatus.New
        };
        return await _gateway.PlaceOrderAsync(order);
    }
}