using System.Globalization;
using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Keyed collector: derivatives liquidations from CoinGlass into the liquidations table.
/// Key read from ProviderKeyStore every run; no key → no-op. Defensive JSON parsing — the
/// exact CoinGlass response shape is finalized once a real key is wired.
/// </summary>
public sealed class LiquidationsCollector : IDataCollector
{
    private readonly ProviderKeyStore _keys;
    private readonly LiquidationsRepository _repo;
    private readonly ILogger<LiquidationsCollector> _log;
    private readonly HttpClient _http;

    public LiquidationsCollector(ProviderKeyStore keys, LiquidationsRepository repo, ILogger<LiquidationsCollector> log)
    {
        _keys = keys;
        _repo = repo;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "liquidations";
    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var key = await _keys.GetAsync("coinglass", ct);
        if (string.IsNullOrWhiteSpace(key)) return 0;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://open-api-v3.coinglass.com/api/futures/liquidation/history?symbol=BTC&interval=1h");
        req.Headers.Add("CG-API-KEY", key);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var row in data.EnumerateArray())
        {
            try
            {
                var ts = row.TryGetProperty("t", out var tEl) && tEl.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : DateTime.UtcNow;
                if (TryDec(row, "longLiquidationUsd", out var lng))
                    count += await _repo.InsertIfNewAsync("BTC", "long", lng, ts, ct);
                if (TryDec(row, "shortLiquidationUsd", out var sht))
                    count += await _repo.InsertIfNewAsync("BTC", "short", sht, ts, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "liquidation row skipped"); }
        }
        return count;
    }

    private static bool TryDec(JsonElement row, string prop, out decimal value)
    {
        value = 0m;
        if (!row.TryGetProperty(prop, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }
}
