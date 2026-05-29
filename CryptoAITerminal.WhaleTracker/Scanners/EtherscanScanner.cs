using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.WhaleTracker.Models;

namespace CryptoAITerminal.WhaleTracker.Scanners;

/// <summary>
/// Works with Etherscan-compatible APIs (Etherscan for ETH, BSCscan for BSC).
/// </summary>
public sealed class EtherscanScanner : IChainScanner, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ChainType _chain;

    // Stablecoin contracts per chain
    private static readonly IReadOnlyList<(string Contract, string Symbol, int Decimals)> EthContracts =
    [
        ("0xdac17f958d2ee523a2206206994597c13d831ec7", "USDT", 6),
        ("0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48", "USDC", 6),
    ];

    private static readonly IReadOnlyList<(string Contract, string Symbol, int Decimals)> BscContracts =
    [
        ("0x55d398326f99059ff775485246999027b3197955", "USDT", 18),
        ("0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d", "USDC", 18),
    ];

    private readonly IReadOnlyList<(string Contract, string Symbol, int Decimals)> _stableContracts;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ChainType Chain => _chain;

    public EtherscanScanner(ChainType chain, string? apiKey = null)
    {
        _chain = chain;
        _apiKey = apiKey ?? "YourApiKeyToken"; // Etherscan accepts this as no-key fallback
        _baseUrl = chain == ChainType.BSC
            ? "https://api.bscscan.com/api"
            : "https://api.etherscan.io/api";
        _stableContracts = chain == ChainType.BSC ? BscContracts : EthContracts;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IReadOnlyList<WhaleTransfer>> GetLargeStablecoinTransfersAsync(
        decimal minUsdValue, CancellationToken ct = default)
    {
        var result = new List<WhaleTransfer>();

        foreach (var (contract, symbol, decimals) in _stableContracts)
        {
            try
            {
                var transfers = await FetchTokenTransfersAsync(contract, symbol, decimals, ct);
                result.AddRange(transfers.Where(t => t.UsdValue >= minUsdValue));
                await Task.Delay(250, ct); // rate limiting
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip on API error */ }
        }

        return result;
    }

    public async Task<IReadOnlyList<WhaleTransfer>> GetWalletActivityAsync(
        IEnumerable<string> addresses, CancellationToken ct = default)
    {
        var result = new List<WhaleTransfer>();

        foreach (var address in addresses)
        {
            try
            {
                var transfers = await FetchWalletTokenTxAsync(address, ct);
                result.AddRange(transfers);
                await Task.Delay(250, ct); // rate limiting
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip on API error */ }
        }

        return result;
    }

    private async Task<IReadOnlyList<WhaleTransfer>> FetchTokenTransfersAsync(
        string contract, string symbol, int decimals, CancellationToken ct)
    {
        var url = $"{_baseUrl}?module=account&action=tokentx" +
                  $"&contractaddress={contract}" +
                  $"&page=1&offset=50&sort=desc" +
                  $"&apikey={_apiKey}";

        var json = await _http.GetStringAsync(url, ct);
        var response = JsonSerializer.Deserialize<EtherscanResponse<List<EtherscanTokenTx>>>(json, JsonOpts);

        if (response?.Status != "1" || response.Result is null)
            return [];

        var divisor = (decimal)Math.Pow(10, decimals);
        return response.Result
            .Where(tx => tx.IsError != "1")
            .Select(tx => new WhaleTransfer
            {
                TxHash = tx.Hash,
                Chain = _chain,
                FromAddress = tx.From.ToLowerInvariant(),
                ToAddress = tx.To.ToLowerInvariant(),
                TokenSymbol = symbol,
                TokenName = tx.TokenName,
                Amount = ParseDecimal(tx.Value) / divisor,
                UsdValue = ParseDecimal(tx.Value) / divisor, // stablecoins ≈ $1
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(ParseLong(tx.TimeStamp)).UtcDateTime,
                ContractAddress = contract
            })
            .ToList();
    }

    private async Task<IReadOnlyList<WhaleTransfer>> FetchWalletTokenTxAsync(
        string address, CancellationToken ct)
    {
        var url = $"{_baseUrl}?module=account&action=tokentx" +
                  $"&address={address}" +
                  $"&page=1&offset=20&sort=desc" +
                  $"&apikey={_apiKey}";

        var json = await _http.GetStringAsync(url, ct);
        var response = JsonSerializer.Deserialize<EtherscanResponse<List<EtherscanTokenTx>>>(json, JsonOpts);

        if (response?.Status != "1" || response.Result is null)
            return [];

        return response.Result
            .Where(tx => tx.IsError != "1")
            .Select(tx =>
            {
                int decimals = int.TryParse(tx.TokenDecimal, out var d) ? d : 18;
                var divisor = (decimal)Math.Pow(10, decimals);
                var amount = ParseDecimal(tx.Value) / divisor;
                // USD value: stable if USDT/USDC, else token amount (no price feed here)
                var isStable = tx.TokenSymbol.Equals("USDT", StringComparison.OrdinalIgnoreCase)
                            || tx.TokenSymbol.Equals("USDC", StringComparison.OrdinalIgnoreCase);
                return new WhaleTransfer
                {
                    TxHash = tx.Hash,
                    Chain = _chain,
                    FromAddress = tx.From.ToLowerInvariant(),
                    ToAddress = tx.To.ToLowerInvariant(),
                    TokenSymbol = tx.TokenSymbol,
                    TokenName = tx.TokenName,
                    Amount = amount,
                    UsdValue = isStable ? amount : 0m,
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(ParseLong(tx.TimeStamp)).UtcDateTime,
                    ContractAddress = tx.ContractAddress
                };
            })
            .ToList();
    }

    private static decimal ParseDecimal(string s) =>
        decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static long ParseLong(string s) =>
        long.TryParse(s, out var v) ? v : 0L;

    public void Dispose() => _http.Dispose();

    // ── DTOs ────────────────────────────────────────────────────────────────

    private sealed class EtherscanResponse<T>
    {
        [JsonPropertyName("status")]  public string Status  { get; init; } = "";
        [JsonPropertyName("message")] public string Message { get; init; } = "";
        [JsonPropertyName("result")]  public T?     Result  { get; init; }
    }

    private sealed class EtherscanTokenTx
    {
        [JsonPropertyName("hash")]          public string Hash          { get; init; } = "";
        [JsonPropertyName("timeStamp")]     public string TimeStamp     { get; init; } = "";
        [JsonPropertyName("from")]          public string From          { get; init; } = "";
        [JsonPropertyName("to")]            public string To            { get; init; } = "";
        [JsonPropertyName("value")]         public string Value         { get; init; } = "";
        [JsonPropertyName("tokenName")]     public string TokenName     { get; init; } = "";
        [JsonPropertyName("tokenSymbol")]   public string TokenSymbol   { get; init; } = "";
        [JsonPropertyName("tokenDecimal")]  public string TokenDecimal  { get; init; } = "18";
        [JsonPropertyName("contractAddress")] public string ContractAddress { get; init; } = "";
        [JsonPropertyName("isError")]       public string IsError       { get; init; } = "0";
    }
}
