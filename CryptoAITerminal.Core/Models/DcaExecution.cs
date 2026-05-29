using System;

namespace CryptoAITerminal.Core.Models;

public class DcaExecution
{
    public string Symbol { get; set; } = "";
    public DateTime ExecutedAt { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal TotalUsdt { get; set; }
    public bool Executed { get; set; }
    public string Reason { get; set; } = "";
}
