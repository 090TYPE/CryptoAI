using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed record SentimentSnapshot(
    int     FearGreedValue,
    int     FearGreedPrevious,
    string  FearGreedLabel,
    double  LongRatio,
    double  ShortRatio,
    decimal OpenInterest,
    decimal OpenInterestChange24h
);

public sealed class SentimentService : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    public SentimentService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<SentimentSnapshot> FetchAsync(string symbol, CancellationToken ct = default)
    {
        var fngTask = FetchFearGreedAsync(ct);
        var lsTask  = FetchLongShortAsync(symbol, ct);
        var oiTask  = FetchOpenInterestAsync(symbol, ct);

        await Task.WhenAll(fngTask, lsTask, oiTask).ConfigureAwait(false);

        var (value, previous, label) = fngTask.Result;
        var (longRatio, shortRatio)  = lsTask.Result;
        var (oi, oiChange)           = oiTask.Result;

        return new SentimentSnapshot(value, previous, label, longRatio, shortRatio, oi, oiChange);
    }

    private async Task<(int value, int previous, string label)> FetchFearGreedAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync("https://api.alternative.me/fng/?limit=2", ct)
                                  .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return (50, 50, "Neutral");

            var arr = data.EnumerateArray();
            int value = 50, previous = 50;
            string label = "Neutral";
            int idx = 0;
            foreach (var item in arr)
            {
                if (idx == 0)
                {
                    value = int.Parse(item.GetProperty("value").GetString() ?? "50",
                                      CultureInfo.InvariantCulture);
                    label = item.GetProperty("value_classification").GetString() ?? "Neutral";
                }
                else if (idx == 1)
                {
                    previous = int.Parse(item.GetProperty("value").GetString() ?? "50",
                                         CultureInfo.InvariantCulture);
                }
                idx++;
            }
            return (value, previous, label);
        }
        catch
        {
            return (50, 50, "Neutral");
        }
    }

    private async Task<(double longRatio, double shortRatio)> FetchLongShortAsync(
        string symbol, CancellationToken ct)
    {
        try
        {
            var url  = $"https://fapi.binance.com/futures/data/globalLongShortAccountRatio?symbol={symbol}&period=1h&limit=1";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.EnumerateArray();
            foreach (var item in arr)
            {
                var lng = double.Parse(item.GetProperty("longAccount").GetString()  ?? "0.5",
                                       CultureInfo.InvariantCulture);
                var sht = double.Parse(item.GetProperty("shortAccount").GetString() ?? "0.5",
                                       CultureInfo.InvariantCulture);
                return (lng, sht);
            }
            return (0.5, 0.5);
        }
        catch
        {
            return (0.5, 0.5);
        }
    }

    private async Task<(decimal oi, decimal change24h)> FetchOpenInterestAsync(
        string symbol, CancellationToken ct)
    {
        try
        {
            var url  = $"https://fapi.binance.com/futures/data/openInterestHist?symbol={symbol}&period=1h&limit=25";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.EnumerateArray().ToArray(doc);

            if (arr.Length == 0) return (0m, 0m);

            var latestVal  = ParseDecimalProp(arr[^1], "sumOpenInterestValue");
            var oldestVal  = ParseDecimalProp(arr[0],  "sumOpenInterestValue");
            var change24h  = oldestVal > 0 ? (latestVal - oldestVal) / oldestVal * 100m : 0m;
            return (latestVal, change24h);
        }
        catch
        {
            return (0m, 0m);
        }
    }

    private static decimal ParseDecimalProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return 0m;
        return prop.ValueKind == JsonValueKind.String
            ? decimal.Parse(prop.GetString() ?? "0", CultureInfo.InvariantCulture)
            : prop.GetDecimal();
    }

    public void Dispose()
    {
        if (!_disposed) { _disposed = true; _http.Dispose(); }
    }
}

// Extension helper — avoids LINQ ToArray on JsonElement enumerator (no allocation issue)
file static class JsonElementExtensions
{
    public static JsonElement[] ToArray(this JsonElement.ArrayEnumerator enumerator, JsonDocument _)
    {
        var list = new System.Collections.Generic.List<JsonElement>();
        foreach (var item in enumerator) list.Add(item);
        return list.ToArray();
    }
}
