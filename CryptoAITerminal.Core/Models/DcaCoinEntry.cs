namespace CryptoAITerminal.Core.Models;

public class DcaCoinEntry
{
    public string Symbol { get; set; } = "";
    public int WeightPercent { get; set; } = 100;
    public bool ConditionalBuyEnabled { get; set; }
    public int MaPeriod { get; set; } = 200;
}
