using CryptoAITerminal.Core.Enums;

namespace CryptoAITerminal.Core.Models;

public class TradeExecution
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Symbol { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string ClientOrderId { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal Fee { get; set; }
    public string FeeAsset { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
