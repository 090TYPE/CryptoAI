using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Fetches the public recent-trades tape (everyone's anonymous fills on a symbol) from
/// Binance's keyless market-data endpoint. Identity is never exposed by the exchange —
/// this is the aggregate tape, not any single user's trades.
/// </summary>
public sealed class MarketTapeService : IDisposable
{
    private const string Endpoint = "https://api.binance.com/api/v3/trades";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

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
        var url = $"{Endpoint}?symbol={Uri.EscapeDataString(symbol.Trim().ToUpperInvariant())}&limit={clamped}";

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return MarketTapeParser.Parse(json);
    }

    public void Dispose() => _http.Dispose();
}
