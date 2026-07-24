using System.Globalization;
using System.Text.Json;

namespace CryptoAITerminal.Executor;

/// <summary>Top-of-book price for a symbol on a named exchange (public data, for routing).</summary>
public interface IQuoteSource
{
    Task<decimal?> GetQuoteAsync(string exchange, string asset, string quote, CancellationToken ct);
}

/// <summary>
/// Fetches public spot ticker prices per exchange so the router can compare venues (Phase 4.4).
/// Public endpoints only — no keys, no private data. A venue that errors or has no price is skipped
/// (returns null) so routing degrades to the venues that answered.
/// </summary>
public sealed class MultiExchangeQuoteSource : IQuoteSource
{
    private readonly HttpClient _http;
    public MultiExchangeQuoteSource(HttpClient http) => _http = http;

    public async Task<decimal?> GetQuoteAsync(string exchange, string asset, string quote, CancellationToken ct)
    {
        try
        {
            var (url, path) = exchange.ToLowerInvariant() switch
            {
                "binance" => ($"https://api.binance.com/api/v3/ticker/price?symbol={asset}{quote}", new[] { "price" }),
                "bybit"   => ($"https://api.bybit.com/v5/market/tickers?category=spot&symbol={asset}{quote}", new[] { "result", "list", "0", "lastPrice" }),
                "okx"     => ($"https://www.okx.com/api/v5/market/ticker?instId={asset}-{quote}", new[] { "data", "0", "last" }),
                _ => (null!, null!),
            };
            if (url is null) return null;

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

            var el = doc.RootElement;
            foreach (var seg in path)
            {
                if (int.TryParse(seg, out var idx))
                {
                    if (el.ValueKind != JsonValueKind.Array || idx >= el.GetArrayLength()) return null;
                    el = el[idx];
                }
                else if (!el.TryGetProperty(seg, out el)) return null;
            }
            return decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var px) ? px : null;
        }
        catch { return null; }
    }
}
