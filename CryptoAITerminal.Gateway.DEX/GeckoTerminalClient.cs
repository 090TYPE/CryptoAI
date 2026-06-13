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
