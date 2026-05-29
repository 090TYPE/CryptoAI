using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

public class AlchemyPricesClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public AlchemyPricesClient(string apiKey, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri("https://api.g.alchemy.com/");
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        }
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetHistoricalPricesAsync(
        string network,
        string tokenAddress,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        string interval,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"prices/v1/{_apiKey}/tokens/historical");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                network,
                address = tokenAddress,
                startTime = fromDate.UtcDateTime.ToString("O"),
                endTime = toDate.UtcDateTime.ToString("O"),
                interval,
                withMarketData = true
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<AlchemyResponseDto>(stream, JsonOptions, cancellationToken);
        var prices = payload?.Data?.Data ?? new List<AlchemyPriceDto>();

        return prices
            .Select(static price => new DexOhlcvPoint
            {
                Timestamp = DateTime.TryParse(price.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                    ? timestamp.ToLocalTime()
                    : DateTime.MinValue,
                Open = ParseDecimal(price.Value),
                High = ParseDecimal(price.Value),
                Low = ParseDecimal(price.Value),
                Close = ParseDecimal(price.Value),
                Volume = ParseDecimal(price.TotalVolume)
            })
            .Where(point => point.Timestamp != DateTime.MinValue)
            .OrderBy(point => point.Timestamp)
            .ToList();
    }

    private static decimal ParseDecimal(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
    }

    private sealed class AlchemyResponseDto
    {
        public AlchemyDataWrapperDto? Data { get; set; }
    }

    private sealed class AlchemyDataWrapperDto
    {
        public List<AlchemyPriceDto>? Data { get; set; }
    }

    private sealed class AlchemyPriceDto
    {
        public string Timestamp { get; set; } = string.Empty;
        public string? Value { get; set; }
        public string? TotalVolume { get; set; }
    }
}
