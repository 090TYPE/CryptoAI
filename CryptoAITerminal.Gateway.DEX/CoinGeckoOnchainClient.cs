using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

public class CoinGeckoOnchainClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public CoinGeckoOnchainClient(string apiKey, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri("https://pro-api.coingecko.com/api/v3/");
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        }
    }

    public async Task<IReadOnlyList<DexOhlcvPoint>> GetTokenOhlcvAsync(
        string network,
        string tokenAddress,
        string timeframe,
        int aggregate,
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"onchain/networks/{network}/tokens/{tokenAddress}/ohlcv/{timeframe}?aggregate={aggregate}&limit={limit}&currency=usd&include_empty_intervals=true&include_inactive_source=true");
        request.Headers.Add("x-cg-pro-api-key", _apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
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
            .Take(limit)
            .ToList();
    }

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
        public List<List<JsonElement>>? OhlcvList { get; set; }
    }
}
