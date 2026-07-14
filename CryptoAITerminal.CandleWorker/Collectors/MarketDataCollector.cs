using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Enriches every active token with live market data from DexScreener (price, liquidity,
/// volume, 5m/1h/24h changes, market cap, name/symbol, primary pool). Free — no key.
/// Also auto-resolves <c>pool_address</c> for the candle worker (via DexScreener pairAddress).
/// </summary>
public sealed class MarketDataCollector : IDataCollector
{
    private readonly TrackedTokenRepository _tracked;
    private readonly CandleRepository _candles;
    private readonly MetadataRepository _meta;
    private readonly DexScreenerClient _dex;

    public MarketDataCollector(TrackedTokenRepository tracked, CandleRepository candles, MetadataRepository meta, DexScreenerClient dex)
    {
        _tracked = tracked;
        _candles = candles;
        _meta = meta;
        _dex = dex;
    }

    public string Name => "market";
    public TimeSpan Interval => TimeSpan.FromSeconds(60);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var active = await _tracked.ListActiveAsync(ct);
        var count = 0;

        foreach (var group in active.GroupBy(a => a.Chain))
        {
            var dsChain = ChainMap.ToDexScreener(group.Key);
            if (dsChain is null) continue;

            // DexScreener returns checksummed EVM addresses; our identity is the stored
            // address (lowercase for EVM, exact for Solana). Match case-insensitively and
            // always write back under the STORED address so keys line up.
            var byAddress = group.ToDictionary(a => a.TokenAddress, a => a, StringComparer.OrdinalIgnoreCase);
            var infos = await _dex.GetTokensByAddressesAsync(dsChain, group.Select(a => a.TokenAddress), ct);

            foreach (var info in infos)
            {
                if (!byAddress.TryGetValue(info.TokenAddress, out var stored)) continue;
                var token = stored.TokenAddress;

                await _candles.UpdateSnapshotAsync(group.Key, token,
                    info.PriceUsd, info.LiquidityUsd, info.Volume24h,
                    info.PriceChange5m, info.PriceChange1h, info.PriceChange24h, ct);

                await _meta.UpsertAsync(group.Key, token, info.Name, info.Symbol,
                    imageUrl: null, description: null, website: null, twitter: null, telegram: null,
                    pairAddress: info.PairAddress, marketCap: info.MarketCap, ct);

                // Fill the candle worker's pool_address the first time we see a pair for it.
                if (!string.IsNullOrWhiteSpace(info.PairAddress) && string.IsNullOrWhiteSpace(stored.PoolAddress))
                {
                    await _tracked.SetPoolAsync(group.Key, token, info.PairAddress, ct);
                }

                count++;
            }
        }

        return count;
    }
}
