namespace CryptoAITerminal.Core.Models;

public class OrderBookLevel
{
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
}

public class OrderBook
{
    public string Symbol { get; set; } = "";
    public List<OrderBookLevel> Bids { get; set; } = new(); // покупатели
    public List<OrderBookLevel> Asks { get; set; } = new(); // продавцы
    public DateTime Timestamp { get; set; }
}