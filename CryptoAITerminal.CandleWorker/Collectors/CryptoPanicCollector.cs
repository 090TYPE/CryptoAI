using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Keyed collector: CryptoPanic aggregated news feed into the shared news table (deduped by URL).
/// Key read from ProviderKeyStore every run; no key → no-op.
/// </summary>
public sealed class CryptoPanicCollector : IDataCollector
{
    private readonly ProviderKeyStore _keys;
    private readonly NewsRepository _news;
    private readonly ILogger<CryptoPanicCollector> _log;
    private readonly HttpClient _http;

    public CryptoPanicCollector(ProviderKeyStore keys, NewsRepository news, ILogger<CryptoPanicCollector> log)
    {
        _keys = keys;
        _news = news;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "cryptopanic";
    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var key = await _keys.GetAsync("cryptopanic", ct);
        if (string.IsNullOrWhiteSpace(key)) return 0;

        var json = await _http.GetStringAsync(
            $"https://cryptopanic.com/api/v1/posts/?auth_token={key}&public=true", ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return 0;

        var inserted = 0;
        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = r.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;

            var source = r.TryGetProperty("source", out var s) && s.TryGetProperty("title", out var st)
                ? st.GetString() : "CryptoPanic";
            DateTime? published = r.TryGetProperty("published_at", out var p)
                && DateTimeOffset.TryParse(p.GetString(), out var dto) ? dto.UtcDateTime : null;

            inserted += await _news.InsertIfNewAsync(source ?? "CryptoPanic", title, url, null, published, ct);
        }
        return inserted;
    }
}
