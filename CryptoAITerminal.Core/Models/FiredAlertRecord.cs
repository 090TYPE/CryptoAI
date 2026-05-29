using System;

namespace CryptoAITerminal.Core.Models;

public class FiredAlertRecord
{
    public string AlertId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string ConditionLabel { get; set; } = string.Empty;
    public decimal TriggerValue { get; set; }
    public DateTime FiredAt { get; set; }
}
