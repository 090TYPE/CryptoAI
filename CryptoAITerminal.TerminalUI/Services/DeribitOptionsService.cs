using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Fetches options data from Deribit public REST API (no auth needed for market data).
/// Provides:
///   - ATM implied volatility (IV) for BTC and ETH
///   - Put/Call ratio (open interest based)
///   - 25-delta risk reversal (IV skew)
///   - Fear &amp; Greed proxy from IV and skew
///
/// Data refreshes every 5 minutes by default.
/// </summary>
public sealed class DeribitOptionsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private const string BaseUrl = "https://www.deribit.com/api/v2/public/";

    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;

    public event Action<DeribitOptionsSnapshot>? SnapshotUpdated;

    public DeribitOptionsSnapshot? Latest { get; private set; }

    public DeribitOptionsService(TimeSpan? interval = null)
    {
        _http  = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval ?? TimeSpan.FromMinutes(5)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var btcSnap = await FetchAssetSnapshotAsync("BTC", ct);
            var ethSnap = await FetchAssetSnapshotAsync("ETH", ct);

            var snap = new DeribitOptionsSnapshot(btcSnap, ethSnap, DateTime.UtcNow);
            Latest = snap;
            SnapshotUpdated?.Invoke(snap);
        }
        catch { /* network errors — keep last snapshot */ }
    }

    private async Task<AssetOptionsData> FetchAssetSnapshotAsync(string asset, CancellationToken ct)
    {
        // 1. Get index price
        var indexPrice = await GetIndexPriceAsync(asset, ct);

        // 2. Get all options instruments for the nearest expiry
        var instruments = await GetInstrumentsAsync(asset, ct);
        if (instruments.Count == 0) return AssetOptionsData.Unknown(asset);

        // 3. Find nearest expiry (options expiring in 7-30 days = most liquid)
        var nearExpiry = instruments
            .Where(i => i.DaysToExpiry >= 3 && i.DaysToExpiry <= 35)
            .GroupBy(i => i.ExpiryDate)
            .OrderBy(g => g.Key)
            .FirstOrDefault();

        if (nearExpiry is null) return AssetOptionsData.Unknown(asset);

        // 4. Get tickers for ATM strikes
        var calls = nearExpiry.Where(i => i.IsCall).ToList();
        var puts  = nearExpiry.Where(i => !i.IsCall).ToList();

        var atmStrike = calls
            .OrderBy(i => Math.Abs((double)(i.Strike - indexPrice)))
            .FirstOrDefault();

        decimal atmIv     = 0m;
        decimal skew25d   = 0m;
        decimal putCallRatio = 0m;

        if (atmStrike is not null)
        {
            var ticker = await GetTickerAsync(atmStrike.InstrumentName, ct);
            atmIv = ticker?.MarkIv ?? 0m;
        }

        // P/C ratio from open interest
        var totalCallOi = calls.Sum(i => i.OpenInterest);
        var totalPutOi  = puts.Sum(i => i.OpenInterest);
        putCallRatio = totalCallOi > 0 ? totalPutOi / totalCallOi : 0m;

        // 25-delta skew: IV(25d put) - IV(25d call)
        // Approximate 25d strike as index * 0.9 (put) and index * 1.1 (call)
        var otmCall25 = calls
            .OrderBy(i => Math.Abs((double)(i.Strike - indexPrice * 1.1m)))
            .FirstOrDefault();
        var otmPut25  = puts
            .OrderBy(i => Math.Abs((double)(i.Strike - indexPrice * 0.9m)))
            .FirstOrDefault();

        if (otmCall25 is not null && otmPut25 is not null)
        {
            var callTicker = await GetTickerAsync(otmCall25.InstrumentName, ct);
            var putTicker  = await GetTickerAsync(otmPut25.InstrumentName, ct);
            if (callTicker is not null && putTicker is not null)
                skew25d = putTicker.MarkIv - callTicker.MarkIv;
        }

        return new AssetOptionsData(
            Asset:        asset,
            IndexPrice:   indexPrice,
            AtmIv:        atmIv,
            PutCallRatio: putCallRatio,
            Skew25Delta:  skew25d,
            NearExpiry:   nearExpiry.Key,
            FetchedAtUtc: DateTime.UtcNow);
    }

    private async Task<decimal> GetIndexPriceAsync(string asset, CancellationToken ct)
    {
        var url  = $"{BaseUrl}get_index_price?index_name={asset.ToLowerInvariant()}_usd";
        var json = await GetJsonAsync(url, ct);
        return json?["result"]?["index_price"]?.GetValue<decimal>() ?? 0m;
    }

    private async Task<List<OptionInstrument>> GetInstrumentsAsync(string asset, CancellationToken ct)
    {
        var url  = $"{BaseUrl}get_instruments?currency={asset}&kind=option&expired=false";
        var json = await GetJsonAsync(url, ct);
        var arr  = json?["result"]?.AsArray() ?? new JsonArray();
        var now  = DateTime.UtcNow;

        return arr.OfType<JsonNode>()
            .Select(n => new OptionInstrument(
                n["instrument_name"]?.GetValue<string>() ?? string.Empty,
                n["strike"]?.GetValue<decimal>() ?? 0m,
                n["option_type"]?.GetValue<string>() == "call",
                DateTimeOffset.FromUnixTimeMilliseconds(
                    n["expiration_timestamp"]?.GetValue<long>() ?? 0).UtcDateTime,
                n["open_interest"]?.GetValue<decimal>() ?? 0m))
            .Where(i => !string.IsNullOrEmpty(i.InstrumentName) && i.Strike > 0)
            .Select(i => i with { DaysToExpiry = (int)(i.ExpiryDate - now).TotalDays })
            .ToList();
    }

    private async Task<OptionTicker?> GetTickerAsync(string instrumentName, CancellationToken ct)
    {
        var url  = $"{BaseUrl}ticker?instrument_name={instrumentName}";
        var json = await GetJsonAsync(url, ct);
        var r    = json?["result"];
        if (r is null) return null;

        return new OptionTicker(
            r["mark_iv"]?.GetValue<decimal>() ?? 0m,
            r["mark_price"]?.GetValue<decimal>() ?? 0m,
            r["open_interest"]?.GetValue<decimal>() ?? 0m);
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body);
    }

    public void Dispose() { _timer.Stop(); _http.Dispose(); }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private sealed record OptionInstrument(
        string   InstrumentName,
        decimal  Strike,
        bool     IsCall,
        DateTime ExpiryDate,
        decimal  OpenInterest,
        int      DaysToExpiry = 0);

    private sealed record OptionTicker(decimal MarkIv, decimal MarkPrice, decimal OpenInterest);
}

// ── Result types ──────────────────────────────────────────────────────────────

public sealed record AssetOptionsData(
    string   Asset,
    decimal  IndexPrice,
    decimal  AtmIv,          // annualised IV% for ATM options
    decimal  PutCallRatio,   // OI-weighted P/C ratio
    decimal  Skew25Delta,    // 25δ put IV - 25δ call IV (positive = put premium = fear)
    DateTime NearExpiry,
    DateTime FetchedAtUtc,
    bool     IsValid = true)
{
    public static AssetOptionsData Unknown(string asset) =>
        new(asset, 0, 0, 0, 0, DateTime.MinValue, DateTime.UtcNow, IsValid: false);

    public string IvLabel        => IsValid ? $"{AtmIv:F1}%" : "--";
    public string PcRatioLabel   => IsValid ? $"{PutCallRatio:0.00}" : "--";
    public string SkewLabel      => IsValid ? $"{Skew25Delta:+0.0;-0.0;0}%" : "--";
    public string SkewBrush      => Skew25Delta > 2m ? "#FF6B6B" : Skew25Delta < -2m ? "#3DDC84" : "#F4B860";
    public string SentimentLabel => Skew25Delta > 5m ? "Extreme Fear" :
                                    Skew25Delta > 2m ? "Fear" :
                                    Skew25Delta < -5m ? "Extreme Greed" :
                                    Skew25Delta < -2m ? "Greed" : "Neutral";
    public string ExpiryLabel    => IsValid ? NearExpiry.ToString("dd MMM") : "--";
}

public sealed record DeribitOptionsSnapshot(
    AssetOptionsData Btc,
    AssetOptionsData Eth,
    DateTime         UpdatedUtc)
{
    public string UpdatedLabel => $"Deribit {UpdatedUtc.ToLocalTime():HH:mm}";

    /// <summary>Combined fear/greed: average of BTC and ETH skew sentiment.</summary>
    public string MarketSentiment
    {
        get
        {
            if (!Btc.IsValid && !Eth.IsValid) return "No data";
            var avgSkew = (Btc.IsValid ? Btc.Skew25Delta : 0m)
                        + (Eth.IsValid ? Eth.Skew25Delta : 0m);
            if (Btc.IsValid && Eth.IsValid) avgSkew /= 2m;

            return avgSkew > 5m ? "Extreme Fear" :
                   avgSkew > 2m ? "Fear" :
                   avgSkew < -5m ? "Extreme Greed" :
                   avgSkew < -2m ? "Greed" : "Neutral";
        }
    }

    public string MarketSentimentBrush =>
        MarketSentiment.Contains("Fear")  ? "#FF6B6B" :
        MarketSentiment.Contains("Greed") ? "#3DDC84" : "#F4B860";
}
