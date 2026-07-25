using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.CandleWorker.Sources;

/// <summary>What to fetch. Pool address is optional: some sources key on the token instead.</summary>
public sealed record CandleRequest(string Chain, string TokenAddress, string? PoolAddress, int Limit);

/// <summary>
/// One place 1-minute candles can come from.
///
/// The worker used to call GeckoTerminal directly, so a rate-limit, an outage or a token that
/// GeckoTerminal simply does not index meant no candles at all and no way to tell which it was.
/// </summary>
public interface ICandleSource
{
    string Name { get; }

    /// <summary>
    /// The <c>provider_keys</c> row this source needs, or null when it is free and keyless.
    /// The key is read on every fetch, not cached at startup, because the admin console edits
    /// that table live and the collectors are meant to pick it up without a redeploy.
    /// </summary>
    string? RequiredKey { get; }

    /// <summary>
    /// Candles, or <c>null</c> when this source DECLINED: it is not applicable to the request and
    /// made no call (no pool address, an unmapped chain, a missing key). An empty list means the
    /// opposite - the call was made and the provider genuinely has nothing. Collapsing the two is
    /// how a log ends up claiming a source was tried when it never left the process.
    /// </summary>
    Task<IReadOnlyList<CandleRow>?> FetchAsync(CandleRequest req, string? apiKey, CancellationToken ct);
}

internal static class CandleMapper
{
    // Sorted ascending on purpose: the caller takes rows[^1] as the newest candle, and with several
    // sources in play we can no longer assume they all return the same order.
    public static List<CandleRow> Map(IEnumerable<DexOhlcvPoint> points) => points
        .Where(p => p.Timestamp != DateTime.MinValue)
        .Select(p => new CandleRow(
            DateTime.SpecifyKind(p.Timestamp.ToUniversalTime(), DateTimeKind.Utc),
            p.Open, p.High, p.Low, p.Close, p.Volume))
        .OrderBy(r => r.Ts)
        .ToList();
}

/// <summary>Free, no key, pool-based. The default first choice.</summary>
public sealed class GeckoTerminalCandleSource : ICandleSource
{
    private readonly GeckoTerminalClient _client;
    public GeckoTerminalCandleSource(GeckoTerminalClient client) => _client = client;

    public string Name => "geckoterminal";
    public string? RequiredKey => null;

    public async Task<IReadOnlyList<CandleRow>?> FetchAsync(CandleRequest req, string? apiKey, CancellationToken ct)
    {
        // Declined rather than empty: this endpoint keys on the pool, so with no pool address there
        // is nothing to ask. A freshly favourited token sits here until a collector resolves it.
        if (string.IsNullOrEmpty(req.PoolAddress)) return null;

        var network = ChainMap.ToGeckoNetwork(req.Chain);
        if (network is null) return null;

        var points = await _client.GetPoolOhlcvAsync(network, req.PoolAddress, "minute", aggregate: 1, req.Limit, ct)
            .ConfigureAwait(false);
        return CandleMapper.Map(points);
    }
}

/// <summary>
/// CoinGecko Pro on-chain. Keyed, but keys on the TOKEN rather than the pool, so it also covers
/// tokens whose primary pool has not been resolved yet, which GeckoTerminal cannot.
/// </summary>
public sealed class CoinGeckoOnchainCandleSource : ICandleSource
{
    private readonly HttpClient _http;
    public CoinGeckoOnchainCandleSource(HttpClient http) => _http = http;

    public string Name => "coingecko";
    public string? RequiredKey => "coingecko";

    public async Task<IReadOnlyList<CandleRow>?> FetchAsync(CandleRequest req, string? apiKey, CancellationToken ct)
    {
        var network = ChainMap.ToGeckoNetwork(req.Chain);
        if (network is null || string.IsNullOrWhiteSpace(apiKey)) return null;

        var client = new CoinGeckoOnchainClient(apiKey!, _http);
        var points = await client.GetTokenOhlcvAsync(network, req.TokenAddress, "minute", aggregate: 1, req.Limit, ct)
            .ConfigureAwait(false);
        return CandleMapper.Map(points);
    }
}

/// <summary>Birdeye. Keyed, strongest coverage on Solana.</summary>
public sealed class BirdeyeCandleSource : ICandleSource
{
    private readonly HttpClient _http;
    public BirdeyeCandleSource(HttpClient http) => _http = http;

    public string Name => "birdeye";
    public string? RequiredKey => "birdeye";

    public async Task<IReadOnlyList<CandleRow>?> FetchAsync(CandleRequest req, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var to = DateTimeOffset.UtcNow;
        var from = to.AddMinutes(-Math.Max(1, req.Limit));
        var client = new BirdeyeClient(apiKey!, _http);

        // Prefer the pair series when we know the pool; fall back to the token series.
        var points = string.IsNullOrEmpty(req.PoolAddress)
            ? await client.GetTokenOhlcvAsync(req.Chain, req.TokenAddress, "1m", from, to, ct).ConfigureAwait(false)
            : await client.GetPairOhlcvAsync(req.Chain, req.PoolAddress!, "1m", from, to, ct).ConfigureAwait(false);

        return CandleMapper.Map(points);
    }
}

// Deliberately NOT wired into the 1-minute chain: CovalentClient.GetHistoricalPricesAsync and
// AlchemyPricesClient.GetHistoricalPricesAsync are price-history endpoints whose finest granularity
// is far coarser than a minute. Feeding their points into dex_candles would silently write candles
// that are not candles. They belong in a backfill path with its own timeframe, not here.
