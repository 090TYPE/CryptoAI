using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

public class MoralisClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public MoralisClient(string apiKey, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri("https://deep-index.moralis.io/api/v2.2/");
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        }
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetPairOhlcvAsync(
        string chain,
        string pairAddress,
        string timeframe,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"pairs/{pairAddress}/ohlcv?chain={chain}&timeframe={timeframe}&currency=usd&fromDate={Uri.EscapeDataString(fromDate.UtcDateTime.ToString("O"))}&toDate={Uri.EscapeDataString(toDate.UtcDateTime.ToString("O"))}");
        request.Headers.Add("X-API-Key", _apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<MoralisResponseDto>(stream, JsonOptions, cancellationToken);
        var candles = payload?.Result ?? new List<MoralisCandleDto>();

        return candles
            .Select(static candle => new DexOhlcvPoint
            {
                Timestamp = DateTime.TryParse(candle.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                    ? timestamp.ToLocalTime()
                    : DateTime.MinValue,
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close,
                Volume = candle.Volume
            })
            .Where(point => point.Timestamp != DateTime.MinValue)
            .OrderBy(point => point.Timestamp)
            .ToList();
    }

    private sealed class MoralisResponseDto
    {
        public List<MoralisCandleDto>? Result { get; set; }
    }

    private sealed class MoralisCandleDto
    {
        public string Timestamp { get; set; } = string.Empty;
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
    }
}
