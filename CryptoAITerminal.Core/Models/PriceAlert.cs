namespace CryptoAITerminal.Core.Models;

public enum AlertCondition
{
    PriceAbove,
    PriceBelow,
    ChangePercent5mAbove,
    ChangePercent1hAbove,
    ChangePercent24hAbove,
    VolumeSpike
}

public class PriceAlert
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Symbol { get; set; } = string.Empty;
    public AlertCondition Condition { get; set; }
    public decimal Threshold { get; set; }
    public bool IsActive { get; set; } = true;
    public bool HasFired { get; set; }
    public DateTime? FiredAt { get; set; }
    public bool RepeatAfterFire { get; set; }
    public bool SendTelegram { get; set; }
    public bool SendDiscord { get; set; }
    public bool SendNtfy { get; set; }
    public bool SendEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string ConditionLabel => Condition switch
    {
        AlertCondition.PriceAbove => $"Price > {Threshold:N4}",
        AlertCondition.PriceBelow => $"Price < {Threshold:N4}",
        AlertCondition.ChangePercent5mAbove => $"5m change > {Threshold:+0.##;-0.##}%",
        AlertCondition.ChangePercent1hAbove => $"1h change > {Threshold:+0.##;-0.##}%",
        AlertCondition.ChangePercent24hAbove => $"24h change > {Threshold:+0.##;-0.##}%",
        AlertCondition.VolumeSpike => $"Volume spike ×{Threshold:0.#}",
        _ => Condition.ToString()
    };
}
