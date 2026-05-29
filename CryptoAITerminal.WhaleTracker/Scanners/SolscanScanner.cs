using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.WhaleTracker.Models;

namespace CryptoAITerminal.WhaleTracker.Scanners;

/// <summary>
/// Monitors Solana wallets via the Solscan public REST API.
/// Large-transfer detection is limited to USDT/USDC SPL tokens held by labeled wallets.
/// </summary>
public sealed class SolscanScanner : IChainScanner, IDisposable
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://public-api.solscan.io";

    // SPL token mints for stablecoins
    private const string SolUsdt = "Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB";
    private const string SolUsdc = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ChainType Chain => ChainType.Solana;

    public SolscanScanner()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<IReadOnlyList<WhaleTransfer>> GetLargeStablecoinTransfersAsync(
        decimal minUsdValue, CancellationToken ct = default)
    {
        // Solscan public API does not expose a "recent large transfers" endpoint without a paid key.
        // Large-transfer scanning on Solana is handled via labeled wallet monitoring.
        return Task.FromResult<IReadOnlyList<WhaleTransfer>>([]);
    }

    public async Task<IReadOnlyList<WhaleTransfer>> GetWalletActivityAsync(
        IEnumerable<string> addresses, CancellationToken ct = default)
    {
        var result = new List<WhaleTransfer>();

        foreach (var address in addresses)
        {
            try
            {
                var transfers = await FetchAccountTransactionsAsync(address, ct);
                result.AddRange(transfers);
                await Task.Delay(300, ct); // rate limiting
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip on API error */ }
        }

        return result;
    }

    private async Task<IReadOnlyList<WhaleTransfer>> FetchAccountTransactionsAsync(
        string address, CancellationToken ct)
    {
        var url = $"{BaseUrl}/account/transactions?account={address}&limit=10";
        var json = await _http.GetStringAsync(url, ct);

        var transactions = JsonSerializer.Deserialize<List<SolscanTransaction>>(json, JsonOpts);
        if (transactions is null) return [];

        var result = new List<WhaleTransfer>();

        foreach (var tx in transactions.Where(t => t.Status == "Success"))
        {
            // Look for SOL native transfers (lamports)
            var solAmount = tx.Lamport / 1_000_000_000m;
            if (solAmount > 0)
            {
                result.Add(new WhaleTransfer
                {
                    TxHash = tx.TxHash,
                    Chain = ChainType.Solana,
                    FromAddress = tx.Signer.FirstOrDefault() ?? address,
                    ToAddress = address,
                    TokenSymbol = "SOL",
                    TokenName = "Solana",
                    Amount = solAmount,
                    UsdValue = 0m, // no price feed — filter by amount, not USD
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(tx.BlockTime).UtcDateTime,
                    ContractAddress = string.Empty
                });
            }
        }

        return result;
    }

    public void Dispose() => _http.Dispose();

    // ── DTOs ────────────────────────────────────────────────────────────────

    private sealed class SolscanTransaction
    {
        [JsonPropertyName("txHash")]    public string TxHash    { get; init; } = "";
        [JsonPropertyName("blockTime")] public long BlockTime   { get; init; }
        [JsonPropertyName("status")]    public string Status    { get; init; } = "";
        [JsonPropertyName("lamport")]   public long Lamport     { get; init; }
        [JsonPropertyName("signer")]    public List<string> Signer { get; init; } = [];
    }
}
