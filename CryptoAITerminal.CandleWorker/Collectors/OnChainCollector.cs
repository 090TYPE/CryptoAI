using System.Globalization;
using System.Text.Json;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// On-chain market metrics (active addresses, tx count) for BTC/ETH from CoinMetrics'
/// community API — free, no key. Stored as time series in onchain_metrics.
/// </summary>
public sealed class OnChainCollector : IDataCollector
{
    private static readonly string[] Metrics = { "AdrActCnt", "TxCnt" };

    private readonly HttpClient _http;
    private readonly OnChainRepository _repo;

    public OnChainCollector(OnChainRepository repo)
    {
        _repo = repo;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "onchain";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var start = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd");
        var url = $"https://community-api.coinmetrics.io/v4/timeseries/asset-metrics" +
                  $"?assets=btc,eth&metrics={string.Join(",", Metrics)}&frequency=1d&start_time={start}&page_size=100";

        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var row in data.EnumerateArray())
        {
            var asset = row.TryGetProperty("asset", out var a) ? a.GetString() : null;
            if (asset is null || !row.TryGetProperty("time", out var tEl)
                || !DateTimeOffset.TryParse(tEl.GetString(), out var dto)) continue;

            foreach (var metric in Metrics)
            {
                if (row.TryGetProperty(metric, out var v) && v.ValueKind == JsonValueKind.String
                    && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                {
                    await _repo.UpsertAsync(asset, metric, dto.UtcDateTime, val, ct);
                    count++;
                }
            }
        }
        return count;
    }
}
