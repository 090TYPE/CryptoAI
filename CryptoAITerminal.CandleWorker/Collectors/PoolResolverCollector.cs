using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Fills in <c>tracked_tokens.pool_address</c> for tokens that do not have one.
///
/// This was the last silent dead end in the pipeline. GeckoTerminal — the free, keyless candle
/// source — keys on the POOL, not the token, so a freshly favourited token with no pool resolved
/// could never be asked for candles at all. It produced nothing, forever, unless a paid key happened
/// to be configured for one of the token-keyed sources. The worker even said so in a comment
/// ("pool auto-resolution lands in the next increment") and it never did.
///
/// DexScreener is used because it is free, needs no key, and its search returns the pair address
/// directly. Resolution is attempted once per token per run and persisted, so a token costs at most
/// one search until it succeeds.
/// </summary>
public sealed class PoolResolverCollector : IDataCollector
{
    private readonly TrackedTokenRepository _tracked;
    private readonly DexScreenerClient _dexScreener;
    private readonly ILogger<PoolResolverCollector> _log;
    private readonly int _batch;

    public PoolResolverCollector(
        TrackedTokenRepository tracked,
        DexScreenerClient dexScreener,
        IConfiguration cfg,
        ILogger<PoolResolverCollector> log)
    {
        _tracked = tracked;
        _dexScreener = dexScreener;
        _log = log;
        _batch = int.TryParse(cfg["POOL_RESOLVE_BATCH"], out var b) && b > 0 ? b : 20;
    }

    public string Name => "pool_resolver";

    /// <summary>
    /// Short, because an unresolved token produces no candles at all — the cost of being late here
    /// is a chart that stays empty, not a stale number.
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var pending = await _tracked.ListNeedingPoolAsync(_batch, ct);
        if (pending.Count == 0) return 0;

        var resolved = 0;
        foreach (var token in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var pool = await FindPoolAsync(token.Chain, token.TokenAddress, ct);
                if (pool is null)
                {
                    // Not an error: a token can genuinely have no indexed pair yet.
                    _log.LogInformation("no pool found for {Chain}/{Token}", token.Chain, token.TokenAddress);
                    continue;
                }

                await _tracked.SetPoolAsync(token.Chain, token.TokenAddress, pool, ct);
                resolved++;
                _log.LogInformation("resolved pool for {Chain}/{Token}: {Pool}", token.Chain, token.TokenAddress, pool);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One bad token must not stop the batch.
                _log.LogWarning(ex, "pool lookup failed for {Chain}/{Token}", token.Chain, token.TokenAddress);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Pick the pair for this token on this chain. Chain is matched against the DexScreener slug
    /// because stored ids use several spellings ("eth" vs "ethereum"); the deepest-liquidity pair
    /// wins, since that is the one whose candles represent the token's real price.
    /// </summary>
    private async Task<string?> FindPoolAsync(string chain, string tokenAddress, CancellationToken ct)
    {
        var results = await _dexScreener.SearchTokensAsync(tokenAddress);
        if (results.Count == 0) return null;

        var slug = ChainMap.ToDexScreener(chain);

        var match = results
            .Where(r => !string.IsNullOrWhiteSpace(r.PairAddress)
                        && string.Equals(r.TokenAddress, tokenAddress, StringComparison.OrdinalIgnoreCase)
                        && (slug is null || string.Equals(r.ChainId, slug, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.LiquidityUsd)
            .FirstOrDefault();

        return match?.PairAddress;
    }
}
