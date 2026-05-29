namespace CryptoAITerminal.Core.Models;

/// <summary>
/// Persisted target allocation entry for one asset in the portfolio rebalancer.
/// Stored as JSON in %LOCALAPPDATA%\CryptoAITerminal\portfolio-allocations.json.
/// </summary>
public sealed class PortfolioAllocation
{
    /// <summary>Asset symbol, e.g. "BTC", "ETH", "SOL", "USDT".</summary>
    public string Symbol { get; set; } = "";

    /// <summary>Desired portfolio weight in percent (0-100).</summary>
    public double TargetPct { get; set; }

    /// <summary>Include balances from connected CEX gateways (Binance / Bybit / OKX).</summary>
    public bool IncludeCex { get; set; } = true;

    /// <summary>Include native balance from the connected DEX wallet.</summary>
    public bool IncludeDex { get; set; } = true;
}
