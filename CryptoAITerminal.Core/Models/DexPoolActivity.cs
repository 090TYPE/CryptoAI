using System;
using System.Collections.Generic;

namespace CryptoAITerminal.Core.Models;

/// <summary>One market trade on a DEX pool (from GeckoTerminal pool trades).</summary>
public sealed record DexTradeTick(string Side, decimal AmountUsd, string TxHash, DateTime TimeUtc);

/// <summary>Live pool activity for the selected token: 24h buy/sell counts + a recent
/// market-trade tape.</summary>
public sealed class DexPoolActivity
{
    public long Buys24h { get; set; }
    public long Sells24h { get; set; }
    public long Buyers24h { get; set; }
    public long Sellers24h { get; set; }
    public IReadOnlyList<DexTradeTick> Trades { get; set; } = Array.Empty<DexTradeTick>();
}
