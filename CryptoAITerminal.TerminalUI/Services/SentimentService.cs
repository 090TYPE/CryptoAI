using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

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

    // /api/sentiment is latest-per-metric by construction, so the day-over-day reading the arrow
    // is derived from has to be remembered here — the server has no history endpoint to ask.
    private int? _lastFearGreed;

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
            // The server already collects fear & greed for every licensed user, and the number is
            // market-wide, so a hundred terminals must not each poll alternative.me for it.
            // Inside the existing try on purpose: a surprise here still lands in the neutral catch.
            if (ServerDataClient.IsBound)
            {
                var served = await ServerDataClient.GetSentimentAsync(ct).ConfigureAwait(false);
                if (served is not null && TryReadFearGreed(served, out var sv, out var sl))
                {
                    // Only report a previous reading once one actually differs, so the VM's arrow
                    // stays blank until a rollover instead of implying a delta we never observed.
                    var prev = _lastFearGreed is { } last && last != sv ? last : sv;
                    _lastFearGreed = sv;
                    return (sv, prev, sl);
                }
                // Null body, no fear_greed row or a null value → the direct fetch below still runs.
            }

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

    /// <summary>
    /// Pulls the fear &amp; greed row out of /api/sentiment — an array of
    /// (Metric, Value, Label, Ts), PascalCase, one latest row per metric. False on anything
    /// unusable so the caller reaches its alternative.me fetch rather than the neutral default.
    /// </summary>
    private static bool TryReadFearGreed(string json, out int value, out string label)
    {
        value = 50;
        label = "Neutral";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                // The exact literal the collector writes. Anything else is a metric we cannot map.
                if (!row.TryGetProperty("Metric", out var mEl) ||
                    !string.Equals(mEl.GetString(), "fear_greed", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!row.TryGetProperty("Value", out var vEl)) return false;

                decimal raw;
                if (vEl.ValueKind == JsonValueKind.Number)
                    raw = vEl.GetDecimal();
                else if (vEl.ValueKind == JsonValueKind.String &&
                         decimal.TryParse(vEl.GetString(), NumberStyles.Number,
                                          CultureInfo.InvariantCulture, out var parsed))
                    raw = parsed;
                else
                    return false;   // value is nullable NUMERIC — a null row carries nothing usable

                value = (int)Math.Round(raw);
                if (row.TryGetProperty("Label", out var lEl) &&
                    lEl.GetString() is { } l && !string.IsNullOrWhiteSpace(l))
                    label = l;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
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
