using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Keyed collector: Solana 1m OHLCV from Birdeye into dex_candles (better Solana coverage
/// than GeckoTerminal). The API key is read from ProviderKeyStore on EVERY run, so setting or
/// changing it via the admin API/DB takes effect immediately. No key → silent no-op.
/// This is the template for the other keyed collectors (CoinGecko, Moralis, …).
/// </summary>
public sealed class BirdeyeCollector : IDataCollector
{
    private readonly ProviderKeyStore _keys;
    private readonly TrackedTokenRepository _tracked;
    private readonly CandleRepository _candles;
    private readonly ILogger<BirdeyeCollector> _log;
    private readonly HttpClient _http;

    public BirdeyeCollector(ProviderKeyStore keys, TrackedTokenRepository tracked, CandleRepository candles, ILogger<BirdeyeCollector> log)
    {
        _keys = keys;
        _tracked = tracked;
        _candles = candles;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "birdeye";
    public TimeSpan Interval => TimeSpan.FromSeconds(90);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var key = await _keys.GetAsync("birdeye", ct);
        if (string.IsNullOrWhiteSpace(key))
        {
            _log.LogDebug("birdeye: no key configured — skipping");
            return 0; // editable-key gate: stays off until a key is set
        }

        var client = new BirdeyeClient(key, _http);
        var active = await _tracked.ListActiveAsync(ct);
        var count = 0;

        foreach (var t in active)
        {
            if (ChainMap.ToGeckoNetwork(t.Chain) != "solana") continue; // Birdeye = Solana here

            try
            {
                var to = DateTimeOffset.UtcNow;
                var from = to.AddHours(-1);
                var points = await client.GetTokenOhlcvAsync("solana", t.TokenAddress, "1m", from, to, ct);
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
            catch (Exception ex) { _log.LogWarning(ex, "birdeye {Token} failed", t.TokenAddress); }
        }

        return count;
    }
}
