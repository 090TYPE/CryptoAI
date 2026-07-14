using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Keyed collector: multichain 1m OHLCV from CoinGecko Pro on-chain (by token address, no pool
/// needed) into dex_candles — higher rate limits than the free GeckoTerminal path. Key read from
/// ProviderKeyStore every run; no key → no-op. Same template as <see cref="BirdeyeCollector"/>.
/// </summary>
public sealed class CoinGeckoCollector : IDataCollector
{
    private readonly ProviderKeyStore _keys;
    private readonly TrackedTokenRepository _tracked;
    private readonly CandleRepository _candles;
    private readonly ILogger<CoinGeckoCollector> _log;
    private readonly HttpClient _http;

    public CoinGeckoCollector(ProviderKeyStore keys, TrackedTokenRepository tracked, CandleRepository candles, ILogger<CoinGeckoCollector> log)
    {
        _keys = keys;
        _tracked = tracked;
        _candles = candles;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "coingecko";
    public TimeSpan Interval => TimeSpan.FromSeconds(90);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var key = await _keys.GetAsync("coingecko", ct);
        if (string.IsNullOrWhiteSpace(key)) return 0;

        var client = new CoinGeckoOnchainClient(key, _http);
        var active = await _tracked.ListActiveAsync(ct);
        var count = 0;

        foreach (var t in active)
        {
            var network = ChainMap.ToGeckoNetwork(t.Chain);
            if (network is null) continue;

            try
            {
                var points = await client.GetTokenOhlcvAsync(network, t.TokenAddress, "minute", aggregate: 1, limit: 60, ct);
                if (points.Count == 0) continue;

                var rows = points
                    .Select(p => new CandleRow(
                        DateTime.SpecifyKind(p.Timestamp.ToUniversalTime(), DateTimeKind.Utc),
                        p.Open, p.High, p.Low, p.Close, p.Volume))
                    .ToList();

                await _candles.UpsertCandlesAsync(t.Chain, t.TokenAddress, rows, ct);
                count += rows.Count;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "coingecko {Token} failed", t.TokenAddress); }
        }

        return count;
    }
}
