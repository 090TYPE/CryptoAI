namespace CryptoAITerminal.Executor;

/// <summary>
/// Per-user daily live-spend cap (Track 4 §4). Blocks a live order once today's already-placed live
/// notional plus this order would exceed the cap — a ceiling on capital committed per day, per user.
/// Cap 0 means unlimited. (A realized-loss cap needs PnL accounting; this is the spend guardrail.)
/// </summary>
public static class DailyCapGate
{
    public static (bool Blocked, string? Reason) Check(decimal spentToday, decimal orderNotional, decimal capUsd)
    {
        if (capUsd <= 0m) return (false, null); // unlimited
        return spentToday + orderNotional > capUsd
            ? (true, $"daily spend cap reached ({spentToday}+{orderNotional} > {capUsd} USD)")
            : (false, null);
    }
}
