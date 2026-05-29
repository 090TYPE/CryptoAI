using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

public class BirdeyeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public BirdeyeClient(string apiKey, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri("https://public-api.birdeye.so/");
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        }
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetTokenOhlcvAsync(
        string chain,
        string tokenAddress,
        string type,
        DateTimeOffset timeFrom,
        DateTimeOffset timeTo,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"defi/v3/ohlcv?address={tokenAddress}&type={type}&time_from={timeFrom.ToUnixTimeSeconds()}&time_to={timeTo.ToUnixTimeSeconds()}&currency=usd");
        request.Headers.Add("X-API-KEY", _apiKey);
        request.Headers.Add("x-chain", chain);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<BirdeyeResponseDto>(stream, JsonOptions, cancellationToken);
        var candles = payload?.Data?.Items ?? new List<BirdeyeCandleDto>();

        return candles
            .Select(MapCandle)
            .Where(static point => point.Timestamp != DateTime.MinValue)
            .OrderBy(point => point.Timestamp)
            .ToList();
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetPairOhlcvAsync(
        string chain,
        string pairAddress,
        string type,
        DateTimeOffset timeFrom,
        DateTimeOffset timeTo,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"defi/v3/ohlcv/pair?address={pairAddress}&type={type}&time_from={timeFrom.ToUnixTimeSeconds()}&time_to={timeTo.ToUnixTimeSeconds()}");
        request.Headers.Add("X-API-KEY", _apiKey);
        request.Headers.Add("x-chain", chain);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<BirdeyeResponseDto>(stream, JsonOptions, cancellationToken);
        var candles = payload?.Data?.Items ?? new List<BirdeyeCandleDto>();

        return candles
            .Select(MapCandle)
            .Where(static point => point.Timestamp != DateTime.MinValue)
            .OrderBy(point => point.Timestamp)
            .ToList();
    }

    private static decimal ParseDecimal(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var parsed) => parsed,
            JsonValueKind.String => ParseDecimal(value.GetString()),
            _ => 0m
        };
    }

    private static DexOhlcvPoint MapCandle(BirdeyeCandleDto candle)
    {
        var unixTime = candle.UnixTime != 0 ? candle.UnixTime : candle.ShortUnixTime;
        if (unixTime == 0)
        {
            return new DexOhlcvPoint { Timestamp = DateTime.MinValue };
        }

        return new DexOhlcvPoint
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime,
            Open = candle.Open is not null ? ParseDecimal(candle.Open) : ParseDecimal(candle.OpenShort),
            High = candle.High is not null ? ParseDecimal(candle.High) : ParseDecimal(candle.HighShort),
            Low = candle.Low is not null ? ParseDecimal(candle.Low) : ParseDecimal(candle.LowShort),
            Close = candle.Close is not null ? ParseDecimal(candle.Close) : ParseDecimal(candle.CloseShort),
            Volume = candle.Volume is not null ? ParseDecimal(candle.Volume) : ParseDecimal(candle.VolumeShort)
        };
    }

    private sealed class BirdeyeResponseDto
    {
        public BirdeyeDataDto? Data { get; set; }
    }

    private sealed class BirdeyeDataDto
    {
        public List<BirdeyeCandleDto>? Items { get; set; }
    }

    private sealed class BirdeyeCandleDto
    {
        [JsonPropertyName("unixTime")]
        public long UnixTime { get; set; }

        [JsonPropertyName("time")]
        public long ShortUnixTime { get; set; }

        [JsonPropertyName("open")]
        public string? Open { get; set; }

        [JsonPropertyName("high")]
        public string? High { get; set; }

        [JsonPropertyName("low")]
        public string? Low { get; set; }

        [JsonPropertyName("close")]
        public string? Close { get; set; }

        [JsonPropertyName("volume")]
        public string? Volume { get; set; }

        [JsonPropertyName("o")]
        public JsonElement OpenShort { get; set; }

        [JsonPropertyName("h")]
        public JsonElement HighShort { get; set; }

        [JsonPropertyName("l")]
        public JsonElement LowShort { get; set; }

        [JsonPropertyName("c")]
        public JsonElement CloseShort { get; set; }

        [JsonPropertyName("v")]
        public JsonElement VolumeShort { get; set; }
    }
}
