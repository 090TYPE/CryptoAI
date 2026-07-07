using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>
/// Per-session risk limiter for the autonomous agent. Every autonomous trade must pass
/// <see cref="TryAuthorize"/> BEFORE the (still-gated) order command runs. Hitting a hard cap
/// stops the session so the loop halts. This sits ON TOP of RiskManager/WalletVM — it never
/// loosens them.
/// </summary>
public sealed class AgentGuardrails
{
    public bool LiveEnabled { get; set; }
    public int MaxTrades { get; set; } = 20;
    public decimal SessionBudgetUsd { get; set; } = 1000m;
    public IReadOnlyList<string> AllowedSymbols { get; set; } = Array.Empty<string>(); // empty = any

    public int TradeCount { get; private set; }
    public decimal SpentUsd { get; private set; }
    public bool Stopped { get; private set; }
    public string? StopReason { get; private set; }

    public void Reset()
    {
        TradeCount = 0; SpentUsd = 0m; Stopped = false; StopReason = null;
    }

    public void Stop(string reason)
    {
        Stopped = true;
        StopReason ??= reason;
    }

    /// <summary>Authorize one trade of ~usdEstimate on symbol. Increments counters on success;
    /// hitting a hard cap sets <see cref="Stopped"/>. Returns false with a reason otherwise.</summary>
    public bool TryAuthorize(string symbol, decimal usdEstimate, out string reason)
    {
        if (Stopped) { reason = StopReason ?? "session stopped"; return false; }

        if (AllowedSymbols.Count > 0 &&
            !AllowedSymbols.Any(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"{symbol} is not in the allowlist";
            return false;
        }

        if (TradeCount >= MaxTrades)
        {
            Stop($"max trades ({MaxTrades}) reached");
            reason = StopReason!;
            return false;
        }

        if (usdEstimate > 0m && SpentUsd + usdEstimate > SessionBudgetUsd)
        {
            Stop($"session budget ${SessionBudgetUsd} would be exceeded");
            reason = StopReason!;
            return false;
        }

        TradeCount++;
        SpentUsd += Math.Max(0m, usdEstimate);
        reason = "";
        return true;
    }
}
