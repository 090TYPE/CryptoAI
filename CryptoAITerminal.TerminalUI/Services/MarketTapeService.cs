using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Fetches public trade tapes from two venues:
/// <list type="bullet">
/// <item>CEX — Binance keyless recent-trades (anonymous aggregate fills on a symbol);</item>
/// <item>DEX — GeckoTerminal pool trades (on-chain swaps, including the originating wallet).</item>
/// </list>
/// Neither needs an API key.
/// </summary>
public sealed class MarketTapeService : IDisposable
{
    private const string CexEndpoint = "https://api.binance.com/api/v3/trades";
    private const string DexBase = "https://api.geckoterminal.com/api/v2";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public MarketTapeService()
    {
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("CryptoAITerminal/1.0"))
        {
            // header rejection is non-fatal; GeckoTerminal works without it too
        }
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> most-recent public trades for the symbol.
    /// Throws on transport/HTTP failure so the caller can surface an honest status.
    /// </summary>
    public async Task<IReadOnlyList<TapeTrade>> GetRecentTradesAsync(
        string symbol, int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Array.Empty<TapeTrade>();
        }

        var clamped = Math.Clamp(limit, 1, 1000);
        var url = $"{CexEndpoint}?symbol={Uri.EscapeDataString(symbol.Trim().ToUpperInvariant())}&limit={clamped}";

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return MarketTapeParser.Parse(json);
    }

    /// <summary>
    /// Returns recent on-chain swaps for a DEX pool. <paramref name="network"/> is a
    /// GeckoTerminal network id (e.g. "eth", "bsc", "solana"); <paramref name="poolAddress"/>
    /// is the pool/pair address. Throws on transport/HTTP failure.
    /// </summary>
    public async Task<IReadOnlyList<TapeTrade>> GetDexPoolTradesAsync(
        string network, string poolAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(network) || string.IsNullOrWhiteSpace(poolAddress))
        {
            return Array.Empty<TapeTrade>();
        }

        var url = $"{DexBase}/networks/{Uri.EscapeDataString(network.Trim().ToLowerInvariant())}" +
                  $"/pools/{Uri.EscapeDataString(poolAddress.Trim())}/trades";

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return DexTapeParser.Parse(json);
    }

    public void Dispose() => _http.Dispose();
}
