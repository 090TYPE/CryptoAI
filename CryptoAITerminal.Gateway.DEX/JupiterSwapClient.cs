using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CryptoAITerminal.Gateway.DEX;

public sealed class JupiterSwapClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string? _apiKey;

    public JupiterSwapClient(HttpClient? httpClient = null, string? apiKey = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _apiKey = string.IsNullOrWhiteSpace(apiKey)
            ? Environment.GetEnvironmentVariable("JUPITER_API_KEY")
            : apiKey;

        _httpClient.BaseAddress = new Uri(HasApiKey
            ? "https://api.jup.ag/swap/v1/"
            : "https://lite-api.jup.ag/swap/v1/");

        if (HasApiKey)
        {
            _httpClient.DefaultRequestHeaders.Remove("x-api-key");
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<JupiterQuoteResult> GetQuoteAsync(
        string inputMint,
        string outputMint,
        ulong amount,
        int slippageBps,
        CancellationToken cancellationToken = default)
    {
        var url =
            $"quote?inputMint={Uri.EscapeDataString(inputMint)}&outputMint={Uri.EscapeDataString(outputMint)}&amount={amount}&slippageBps={slippageBps}&swapMode=ExactIn";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var document = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("Jupiter quote response was empty.");

        var routePlan = document["routePlan"]?.AsArray();
        var legs = new List<SolanaSwapLeg>();
        if (routePlan is not null)
        {
            foreach (var routeNode in routePlan)
            {
                var swapInfo = routeNode?["swapInfo"]?.AsObject();
                if (swapInfo is null)
                {
                    continue;
                }

                var label = swapInfo["label"]?.GetValue<string>() ?? "jupiter-route";
                var legInput = swapInfo["inputMint"]?.GetValue<string>() ?? inputMint;
                var legOutput = swapInfo["outputMint"]?.GetValue<string>() ?? outputMint;
                legs.Add(new SolanaSwapLeg("jupiter", legInput, legOutput, label));
            }
        }

        return new JupiterQuoteResult(
            document,
            document["inputMint"]?.GetValue<string>() ?? inputMint,
            document["outputMint"]?.GetValue<string>() ?? outputMint,
            ulong.TryParse(document["inAmount"]?.GetValue<string>(), out var inAmount) ? inAmount : amount,
            ulong.TryParse(document["outAmount"]?.GetValue<string>(), out var outAmount) ? outAmount : 0UL,
            ulong.TryParse(document["otherAmountThreshold"]?.GetValue<string>(), out var minAmount) ? minAmount : 0UL,
            decimal.TryParse(document["priceImpactPct"]?.GetValue<string>(), out var priceImpactPct) ? priceImpactPct : 0m,
            legs);
    }

    public async Task<JupiterSwapResult> BuildSwapTransactionAsync(
        string userPublicKey,
        JsonObject quoteResponse,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["userPublicKey"] = userPublicKey,
            ["quoteResponse"] = quoteResponse.DeepClone(),
            ["wrapAndUnwrapSol"] = true,
            ["useSharedAccounts"] = true,
            ["dynamicComputeUnitLimit"] = true,
            ["prioritizationFeeLamports"] = JsonValue.Create("auto")
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("swap", content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var swapResponse = JsonSerializer.Deserialize<JupiterSwapResponse>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Jupiter swap response could not be parsed.");

        if (string.IsNullOrWhiteSpace(swapResponse.SwapTransaction))
        {
            throw new InvalidOperationException("Jupiter swap response did not include a swap transaction.");
        }

        return new JupiterSwapResult(
            swapResponse.SwapTransaction,
            swapResponse.LastValidBlockHeight,
            swapResponse.PrioritizationFeeLamports,
            swapResponse.ComputeUnitLimit);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    public sealed record JupiterQuoteResult(
        JsonObject RawQuote,
        string InputMint,
        string OutputMint,
        ulong InAmount,
        ulong OutAmount,
        ulong MinimumOutAmount,
        decimal PriceImpactPct,
        IReadOnlyList<SolanaSwapLeg> Legs);

    public sealed record JupiterSwapResult(
        string SwapTransaction,
        long? LastValidBlockHeight,
        long? PrioritizationFeeLamports,
        int? ComputeUnitLimit);

    private sealed class JupiterSwapResponse
    {
        public string? SwapTransaction { get; set; }
        public long? LastValidBlockHeight { get; set; }
        public long? PrioritizationFeeLamports { get; set; }
        public int? ComputeUnitLimit { get; set; }
    }
}
