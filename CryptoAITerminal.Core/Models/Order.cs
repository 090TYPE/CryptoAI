using CryptoAITerminal.Core.Enums;
namespace CryptoAITerminal.Core.Models;

public class Order
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ClientOrderId { get; set; } = string.Empty;
    public string ExchangeType { get; set; } = string.Empty;
    public string TimeInForce { get; set; } = string.Empty;
    public string Symbol { get; set; } = "";
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public TradingMarketType MarketType { get; set; } = TradingMarketType.Spot;
    public FuturesPositionSide PositionSide { get; set; } = FuturesPositionSide.Both;
    public FuturesMarginMode MarginMode { get; set; } = FuturesMarginMode.Cross;
    public int? Leverage { get; set; }
    public bool ReduceOnly { get; set; }
    public decimal Quantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal Price { get; set; } // для лимитных ордеров
    public decimal? StopPrice { get; set; }
    public decimal? TakeProfitPrice { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
