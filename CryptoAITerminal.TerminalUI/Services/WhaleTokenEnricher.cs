using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.WhaleTracker.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Enriches whale alerts with token 1-hour price-change data via Alchemy Prices API.
/// Requires ALCHEMY_API_KEY env var; silently returns <c>null</c> without it.
/// </summary>
public sealed class WhaleTokenEnricher
{
    private readonly AlchemyPricesClient? _alchemy;

    public bool IsAvailable => _alchemy is not null;

    public WhaleTokenEnricher(AlchemyPricesClient? alchemy = null)
    {
        _alchemy = alchemy;
    }

    /// <summary>
    /// Returns the approximate 1-hour price change % for a token by contract address,
    /// or <c>null</c> if the enricher is unavailable or the fetch fails.
    /// </summary>
    public async Task<decimal?> GetPriceChange1hAsync(
        ChainType chain,
        string contractAddress,
        CancellationToken ct = default)
    {
        if (_alchemy is null || string.IsNullOrWhiteSpace(contractAddress))
            return null;

        var network = chain switch
        {
            ChainType.Ethereum => "eth-mainnet",
            ChainType.BSC      => "bnb-mainnet",
            _                  => null   // Solana not supported by Alchemy Prices
        };

        if (network is null) return null;

        try
        {
            var to   = DateTimeOffset.UtcNow;
            var from = to.AddHours(-3);

            var pts = await _alchemy
                .GetHistoricalPricesAsync(network, contractAddress, from, to, "1h", ct)
                .ConfigureAwait(false);

            if (pts.Count < 2) return null;

            var firstClose = pts[0].Close;
            var lastClose  = pts[^1].Close;
            if (firstClose == 0m) return null;

            return (lastClose - firstClose) / firstClose * 100m;
        }
        catch
        {
            return null;   // best-effort; never throw into the caller
        }
    }
}
