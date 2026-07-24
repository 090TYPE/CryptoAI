namespace CryptoAITerminal.Executor;

/// <summary>Pure kill-switch decision: whether a live order is blocked. Paper is never blocked.</summary>
public static class LiveGate
{
    /// <param name="mode">Bot mode ("paper" | "live").</param>
    /// <param name="halted">Whether the user (or global switch) has live trading halted.</param>
    public static (bool Blocked, string? Reason) Check(string mode, bool halted)
    {
        if (!string.Equals(mode, "live", StringComparison.OrdinalIgnoreCase))
            return (false, null); // paper always allowed
        return halted ? (true, "kill-switch: live trading halted") : (false, null);
    }
}
