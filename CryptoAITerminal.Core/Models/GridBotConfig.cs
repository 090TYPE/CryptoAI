using CryptoAITerminal.Core.Enums;

namespace CryptoAITerminal.Core.Models;

public class GridBotConfig
{
    public string Symbol { get; set; } = "BTCUSDT";
    public decimal LowerPrice { get; set; }
    public decimal UpperPrice { get; set; }
    public int GridLevels { get; set; } = 10;
    public decimal QuantityPerGrid { get; set; } = 0.001m;
    public TradingMarketType MarketType { get; set; } = TradingMarketType.Spot;
    public int Leverage { get; set; } = 1;
    public FuturesMarginMode MarginMode { get; set; } = FuturesMarginMode.Cross;
}
