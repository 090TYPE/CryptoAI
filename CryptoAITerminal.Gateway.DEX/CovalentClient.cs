using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

public class CovalentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public CovalentClient(string apiKey, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri("https://api.covalenthq.com/v1/");
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        }
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetHistoricalPricesAsync(
        string chainName,
        string tokenAddress,
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        string interval,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"pricing/historical_by_addresses_v2/{chainName}/{tokenAddress}/?from={Uri.EscapeDataString(fromDate.UtcDateTime.ToString("yyyy-MM-dd"))}&to={Uri.EscapeDataString(toDate.UtcDateTime.ToString("yyyy-MM-dd"))}&prices-at-asc=true&quote-currency=USD&interval={interval}&key={_apiKey}";

        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<CovalentResponseDto>(stream, JsonOptions, cancellationToken);
        var prices = payload?.Data?.Prices ?? new List<CovalentPriceDto>();

        return prices
            .Select(static price => new DexOhlcvPoint
            {
                Timestamp = DateTime.TryParse(price.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                    ? timestamp.ToLocalTime()
                    : DateTime.MinValue,
                Open = price.Price,
                High = price.Price,
                Low = price.Price,
                Close = price.Price,
                Volume = 0
            })
            .Where(point => point.Timestamp != DateTime.MinValue)
            .OrderBy(point => point.Timestamp)
            .ToList();
    }

    private sealed class CovalentResponseDto
    {
        public CovalentDataDto? Data { get; set; }
    }

    private sealed class CovalentDataDto
    {
        public List<CovalentPriceDto>? Prices { get; set; }
    }

    private sealed class CovalentPriceDto
    {
        public string Date { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }
}
