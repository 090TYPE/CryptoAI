using System.Text.Json;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Crypto Fear &amp; Greed index from alternative.me (free, no key). Stored under metric 'fear_greed'.
/// </summary>
public sealed class SentimentCollector : IDataCollector
{
    private readonly HttpClient _http;
    private readonly SentimentRepository _sentiment;

    public SentimentCollector(SentimentRepository sentiment)
    {
        _sentiment = sentiment;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string Name => "sentiment";
    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync("https://api.alternative.me/fng/?limit=1", ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            return 0;

        var first = data[0];
        if (!decimal.TryParse(first.GetProperty("value").GetString(), out var value)) return 0;
        var label = first.GetProperty("value_classification").GetString();
        var ts = long.TryParse(first.GetProperty("timestamp").GetString(), out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : DateTime.UtcNow;

        await _sentiment.UpsertAsync("fear_greed", ts, value, label, ct);
        return 1;
    }
}
