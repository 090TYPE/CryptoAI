using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Snapshot of on-chain metrics.</summary>
public sealed record OnChainSnapshot
{
    // Exchange Net Flow (positive = inflow to exchange = sell pressure)
    public decimal? BtcNetFlow      { get; init; }  // BTC / day
    public decimal? EthNetFlow      { get; init; }  // ETH / day

    // NUPL (Net Unrealised Profit/Loss): > 0.75 = euphoria, < 0 = capitulation
    public decimal? BtcNupl         { get; init; }

    // MVRV ratio: > 3.5 = overheated, < 1 = undervalued
    public decimal? BtcMvrv         { get; init; }
    public decimal? EthMvrv         { get; init; }

    // Realised Price (BTC)
    public decimal? BtcRealisedPrice { get; init; }

    public DateTime Timestamp       { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Fetches on-chain metrics from CoinMetrics Community API (100% free, no key required).
/// Falls back to Glassnode if GLASSNODE_API_KEY env var is set (better accuracy).
/// Raises <see cref="SnapshotReceived"/> on a thread-pool thread every PollInterval.
/// </summary>
public sealed class OnChainMetricsService : IDisposable
{
    // CoinMetrics Community API — completely free, no auth required
    private const string CoinMetricsBase = "https://community-api.coinmetrics.io/v4";

    private readonly HttpClient  _http;
    private readonly string?     _glassnodeKey;
    private CancellationTokenSource? _cts;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Always true — CoinMetrics Community API needs no key.</summary>
    public bool HasApiKey => true;

    public event Action<OnChainSnapshot>? SnapshotReceived;

    public OnChainMetricsService()
    {
        _glassnodeKey = Environment.GetEnvironmentVariable("GLASSNODE_API_KEY");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ── poll loop ─────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await FetchAndFireAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
            await FetchAndFireAsync(ct);
        }
    }

    private async Task FetchAndFireAsync(CancellationToken ct)
    {
        try
        {
            var snap = !string.IsNullOrWhiteSpace(_glassnodeKey)
                ? await FetchGlassnodeSnapshotAsync(ct)
                : await FetchCoinMetricsSnapshotAsync(ct);
            SnapshotReceived?.Invoke(snap);
        }
        catch { /* best-effort */ }
    }

    // ── CoinMetrics Community API (primary free source) ───────────────────────

    private async Task<OnChainSnapshot> FetchCoinMetricsSnapshotAsync(CancellationToken ct)
    {
        // PriceRealizedUSD is not available in the community tier — excluded to prevent 400
        var btcMetrics = await FetchCoinMetricsAsync(
            "btc", "CapMVRVCur,FlowInExNtv,FlowOutExNtv", ct);
        var ethMetrics = await FetchCoinMetricsAsync(
            "eth", "CapMVRVCur,FlowInExNtv,FlowOutExNtv", ct);

        var btcMvrv     = ParseCmDecimal(btcMetrics, "CapMVRVCur");
        decimal? realised = null; // not available in community tier
        var btcFlowIn   = ParseCmDecimal(btcMetrics, "FlowInExNtv");
        var btcFlowOut  = ParseCmDecimal(btcMetrics, "FlowOutExNtv");
        var ethMvrv     = ParseCmDecimal(ethMetrics, "CapMVRVCur");
        var ethFlowIn   = ParseCmDecimal(ethMetrics, "FlowInExNtv");
        var ethFlowOut  = ParseCmDecimal(ethMetrics, "FlowOutExNtv");

        // NUPL ≈ 1 − (1/MVRV)  — accurate approximation when only MVRV is available
        decimal? nupl = btcMvrv is > 0m ? Math.Round(1m - (1m / btcMvrv.Value), 4) : null;

        decimal? btcNetFlow = btcFlowIn.HasValue && btcFlowOut.HasValue
            ? btcFlowIn.Value - btcFlowOut.Value : null;
        decimal? ethNetFlow = ethFlowIn.HasValue && ethFlowOut.HasValue
            ? ethFlowIn.Value - ethFlowOut.Value : null;

        return new OnChainSnapshot
        {
            BtcMvrv          = btcMvrv,
            EthMvrv          = ethMvrv,
            BtcNupl          = nupl,
            BtcRealisedPrice = realised,
            BtcNetFlow       = btcNetFlow,
            EthNetFlow       = ethNetFlow,
        };
    }

    /// <summary>Fetches a batch of metrics for one asset; returns last data row as JsonObject.</summary>
    private async Task<JsonObject?> FetchCoinMetricsAsync(
        string asset, string metrics, CancellationToken ct)
    {
        try
        {
            var url = $"{CoinMetricsBase}/timeseries/asset-metrics" +
                      $"?assets={asset}&metrics={metrics}&frequency=1d&page_size=1";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var data = JsonNode.Parse(json)?["data"]?.AsArray();
            if (data is null || data.Count == 0) return null;
            return data[data.Count - 1]?.AsObject();
        }
        catch { return null; }
    }

    private static decimal? ParseCmDecimal(JsonObject? row, string key)
    {
        if (row is null) return null;
        var s = row[key]?.GetValue<string>();
        return s is not null && decimal.TryParse(
            s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    // ── Glassnode (optional, used when key is present) ────────────────────────

    private async Task<OnChainSnapshot> FetchGlassnodeSnapshotAsync(CancellationToken ct)
    {
        using var authClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        authClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _glassnodeKey);

        var mvrvBtc  = await FetchGlassnodeValueAsync(authClient, "market/mvrv",                              "BTC", ct);
        var mvrvEth  = await FetchGlassnodeValueAsync(authClient, "market/mvrv",                              "ETH", ct);
        var nupl     = await FetchGlassnodeValueAsync(authClient, "indicators/nupl",                          "BTC", ct);
        var realised = await FetchGlassnodeValueAsync(authClient, "market/price_realized_usd",                "BTC", ct);
        var flowBtc  = await FetchGlassnodeValueAsync(authClient, "distribution/exchange_net_position_change","BTC", ct);
        var flowEth  = await FetchGlassnodeValueAsync(authClient, "distribution/exchange_net_position_change","ETH", ct);

        return new OnChainSnapshot
        {
            BtcMvrv          = mvrvBtc,
            EthMvrv          = mvrvEth,
            BtcNupl          = nupl,
            BtcRealisedPrice = realised,
            BtcNetFlow       = flowBtc,
            EthNetFlow       = flowEth,
        };
    }

    private static async Task<decimal?> FetchGlassnodeValueAsync(
        HttpClient client, string metric, string asset, CancellationToken ct)
    {
        try
        {
            var url  = $"https://api.glassnode.com/v1/metrics/{metric}?a={asset}&i=24h&f=json&timestamp_format=humanized";
            var json = await client.GetStringAsync(url, ct).ConfigureAwait(false);
            var arr  = JsonNode.Parse(json)?.AsArray();
            if (arr is null || arr.Count == 0) return null;
            var v = arr[arr.Count - 1]?["v"]?.GetValue<double>();
            return v.HasValue ? (decimal?)Math.Round((decimal)v.Value, 6) : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
