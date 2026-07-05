using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

public class GeckoTerminalClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public GeckoTerminalClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri("https://api.geckoterminal.com/api/v2/");
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        }
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetPoolOhlcvAsync(
        string network,
        string poolAddress,
        string timeframe,
        int aggregate,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"networks/{network}/pools/{poolAddress}/ohlcv/{timeframe}?aggregate={aggregate}&limit={limit}&currency=usd";

        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<OhlcvResponseDto>(stream, JsonOptions, cancellationToken);
        var ohlcvList = payload?.Data?.Attributes?.OhlcvList ?? new List<List<JsonElement>>();

        return ohlcvList
            .Where(static row => row.Count >= 6)
            .Select(static row => new DexOhlcvPoint
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64()).LocalDateTime,
                Open = ParseDecimal(row[1]),
                High = ParseDecimal(row[2]),
                Low = ParseDecimal(row[3]),
                Close = ParseDecimal(row[4]),
                Volume = ParseDecimal(row[5])
            })
            .OrderBy(point => point.Timestamp)
            .ToList();
    }

    /// <summary>Fetch rich token profile info (image, banner, socials, description,
    /// holders, security signals). Returns null when unavailable.</summary>
    public async Task<DexTokenMetadata?> GetTokenInfoAsync(
        string network, string tokenAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(network) || string.IsNullOrWhiteSpace(tokenAddress))
        {
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(
                $"networks/{network}/tokens/{tokenAddress}/info", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, default, cancellationToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("attributes", out var a))
            {
                return null;
            }

            var meta = new DexTokenMetadata
            {
                Address = Str(a, "address"),
                Name = Str(a, "name"),
                Symbol = Str(a, "symbol"),
                ImageUrl = ValidImage(Str(a, "image_url")),
                BannerImageUrl = ValidImage(Str(a, "banner_image_url")),
                Description = Str(a, "description"),
                Discord = Str(a, "discord_url"),
                CoingeckoId = Str(a, "coingecko_coin_id"),
                GtScore = a.TryGetProperty("gt_score", out var gs) && gs.ValueKind == JsonValueKind.Number ? gs.GetDecimal() : 0m,
                GtVerified = a.TryGetProperty("gt_verified", out var gv) && gv.ValueKind == JsonValueKind.True,
                IsHoneypot = a.TryGetProperty("is_honeypot", out var hp) ? hp.ValueKind == JsonValueKind.True : null,
                MintAuthority = Str(a, "mint_authority"),
                FreezeAuthority = Str(a, "freeze_authority"),
                DeveloperAddress = Str(a, "developer_address"),
                Decimals = a.TryGetProperty("decimals", out var dc) && dc.ValueKind == JsonValueKind.Number ? dc.GetInt32() : 0,
                DeveloperHoldingPercentage = a.TryGetProperty("developer_holding_percentage", out var dh)
                    ? (dh.ValueKind == JsonValueKind.Number ? dh.GetDecimal()
                        : dh.ValueKind == JsonValueKind.String && decimal.TryParse(dh.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dv) ? dv : 0m)
                    : 0m,
            };

            var twitter = Str(a, "twitter_handle");
            if (twitter.Length > 0) meta.Twitter = $"https://twitter.com/{twitter.TrimStart('@')}";
            var telegram = Str(a, "telegram_handle");
            if (telegram.Length > 0) meta.Telegram = $"https://t.me/{telegram.TrimStart('@')}";

            if (a.TryGetProperty("websites", out var ws) && ws.ValueKind == JsonValueKind.Array && ws.GetArrayLength() > 0)
            {
                meta.Website = ws[0].GetString() ?? string.Empty;
            }

            if (a.TryGetProperty("holders", out var h))
            {
                if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("count", out var hc) && hc.ValueKind == JsonValueKind.Number)
                    meta.Holders = hc.GetInt64();
                else if (h.ValueKind == JsonValueKind.Number)
                    meta.Holders = h.GetInt64();

                if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("distribution_percentage", out var dist) && dist.ValueKind == JsonValueKind.Object)
                {
                    meta.HoldersTop10Pct = NumProp(dist, "top_10");
                    meta.Holders11To30Pct = NumProp(dist, "11_30");
                    meta.Holders31To50Pct = NumProp(dist, "31_50");
                    meta.HoldersRestPct = NumProp(dist, "rest");
                }
            }

            if (a.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            {
                meta.Categories = string.Join(", ", cats.EnumerateArray().Select(c => c.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            // The main token endpoint carries supply / fdv / market cap (the /info one does not).
            try
            {
                using var tokResp = await _httpClient.GetAsync($"networks/{network}/tokens/{tokenAddress}", cancellationToken);
                if (tokResp.IsSuccessStatusCode)
                {
                    await using var ts = await tokResp.Content.ReadAsStreamAsync(cancellationToken);
                    using var tdoc = await JsonDocument.ParseAsync(ts, default, cancellationToken);
                    if (tdoc.RootElement.TryGetProperty("data", out var td) && td.TryGetProperty("attributes", out var ta))
                    {
                        meta.TotalSupply = NumProp(ta, "normalized_total_supply");
                        meta.Fdv = NumProp(ta, "fdv_usd");
                        meta.MarketCap = NumProp(ta, "market_cap_usd");
                        if (meta.Decimals == 0 && ta.TryGetProperty("decimals", out var td2) && td2.ValueKind == JsonValueKind.Number)
                            meta.Decimals = td2.GetInt32();
                    }
                }
            }
            catch { /* supply/fdv optional */ }

            return meta;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Live pool activity — 24h buy/sell counts and the recent market-trade tape.</summary>
    public async Task<DexPoolActivity?> GetPoolActivityAsync(
        string network, string poolAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(network) || string.IsNullOrWhiteSpace(poolAddress))
        {
            return null;
        }

        var activity = new DexPoolActivity();
        try
        {
            using var poolResp = await _httpClient.GetAsync($"networks/{network}/pools/{poolAddress}", cancellationToken);
            if (poolResp.IsSuccessStatusCode)
            {
                await using var ps = await poolResp.Content.ReadAsStreamAsync(cancellationToken);
                using var pdoc = await JsonDocument.ParseAsync(ps, default, cancellationToken);
                if (pdoc.RootElement.TryGetProperty("data", out var pd) && pd.TryGetProperty("attributes", out var pa)
                    && pa.TryGetProperty("transactions", out var tx) && tx.TryGetProperty("h24", out var h24))
                {
                    activity.Buys24h = LongProp(h24, "buys");
                    activity.Sells24h = LongProp(h24, "sells");
                    activity.Buyers24h = LongProp(h24, "buyers");
                    activity.Sellers24h = LongProp(h24, "sellers");
                }
            }
        }
        catch { /* counts optional */ }

        try
        {
            using var tradesResp = await _httpClient.GetAsync($"networks/{network}/pools/{poolAddress}/trades", cancellationToken);
            if (tradesResp.IsSuccessStatusCode)
            {
                await using var ts = await tradesResp.Content.ReadAsStreamAsync(cancellationToken);
                using var tdoc = await JsonDocument.ParseAsync(ts, default, cancellationToken);
                if (tdoc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    var trades = new List<DexTradeTick>();
                    foreach (var t in data.EnumerateArray())
                    {
                        if (!t.TryGetProperty("attributes", out var a)) continue;
                        var side = Str(a, "kind");
                        var amount = NumProp(a, "volume_in_usd");
                        var tsUtc = DateTime.TryParse(Str(a, "block_timestamp"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTime.UtcNow;
                        trades.Add(new DexTradeTick(side, amount, Str(a, "tx_hash"), tsUtc));
                    }
                    activity.Trades = trades;
                }
            }
        }
        catch { /* trades optional */ }

        return activity.Trades.Count == 0 && activity.Buys24h == 0 && activity.Sells24h == 0 ? null : activity;
    }

    private static long LongProp(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var l) ? l : 0L;

    private static decimal NumProp(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var p)) return 0m;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var n)) return n;
        return p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m;
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

    private static string ValidImage(string url) =>
        string.IsNullOrWhiteSpace(url) || url.Contains("missing", StringComparison.OrdinalIgnoreCase) ? string.Empty : url;

    private static decimal ParseDecimal(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0m
        };
    }

    private sealed class OhlcvResponseDto
    {
        public OhlcvDataDto? Data { get; set; }
    }

    private sealed class OhlcvDataDto
    {
        public OhlcvAttributesDto? Attributes { get; set; }
    }

    private sealed class OhlcvAttributesDto
    {
        // GeckoTerminal returns this under the snake_case key "ohlcv_list".
        // PropertyNameCaseInsensitive does not bridge the underscore, so the
        // mapping must be explicit — otherwise the list is always null and the
        // chart silently falls back to synthetic data.
        [JsonPropertyName("ohlcv_list")]
        public List<List<JsonElement>>? OhlcvList { get; set; }
    }
}
