namespace CryptoAITerminal.Core.Models;

public class TpSlConfig
{
    public bool TpEnabled { get; set; }
    public decimal TpPercent { get; set; } = 2.0m;

    public bool SlEnabled { get; set; }
    public decimal SlPercent { get; set; } = 1.0m;

    // Trailing Stop: SL follows price with SlPercent offset
    public bool TrailingStop { get; set; }

    // Partial TP: close PartialTpClosePercent% at TP1, hold rest until TP2
    public bool PartialTp { get; set; }
    public decimal PartialTpClosePercent { get; set; } = 50m;
    public decimal PartialTp2Percent { get; set; } = 4.0m;
}
