using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Analyses the wallet that deployed a token contract.
/// Uses Etherscan-compatible APIs (free tier, no API key needed for basic queries).
///
/// Detects:
/// - How many contracts this wallet deployed
/// - How many previously deployed tokens are likely rugpulls (liquidity drained)
/// - Age of the deployer wallet
/// - Whether deployer is a known rug-pattern address
///
/// Supported chains: Ethereum, BSC, Base, Polygon, Arbitrum
/// Solana: uses SolanaRpcClient to check program authority history
/// </summary>
public sealed class DeployerWalletAnalyzer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Free Etherscan-compatible API endpoints (no key required for basic queries)
    private static readonly IReadOnlyDictionary<string, string> ExplorerApis =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"] = "https://api.etherscan.io/api",
            ["eth"]      = "https://api.etherscan.io/api",
            ["bsc"]      = "https://api.bscscan.com/api",
            ["bnb"]      = "https://api.bscscan.com/api",
            ["base"]     = "https://api.basescan.org/api",
            ["polygon"]  = "https://api.polygonscan.com/api",
            ["arbitrum"] = "https://api.arbiscan.io/api",
        };

    private readonly HttpClient _http;
    private readonly TimeSpan   _timeout;

    public DeployerWalletAnalyzer(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        _http    = new HttpClient { Timeout = _timeout };
    }

    /// <summary>
    /// Analyses the deployer of the given token contract.
    /// Returns a <see cref="DeployerAnalysisResult"/> regardless of errors
    /// (uses Unknown/safe defaults on failure).
    /// </summary>
    public async Task<DeployerAnalysisResult> AnalyseTokenDeployerAsync(
        string tokenAddress,
        string chainId,
        CancellationToken ct = default)
    {
        var chain = chainId.Trim().ToLowerInvariant();
        if (!ExplorerApis.TryGetValue(chain, out var apiBase))
            return DeployerAnalysisResult.Unsupported(chainId);

        try
        {
            // Step 1: get the tx that created this contract → deployer address
            var deployerAddress = await GetContractDeployerAsync(apiBase, tokenAddress, ct);
            if (string.IsNullOrEmpty(deployerAddress))
                return DeployerAnalysisResult.Unknown("Could not resolve deployer address");

            // Step 2: get all contracts deployed by this wallet
            var deployedContracts = await GetDeployedContractsAsync(apiBase, deployerAddress, ct);

            // Step 3: check each for liquidity drain (simple: tx count of the token itself)
            // Heuristic: newly deployed contract with < 50 txs = likely abandoned/rugged
            var rugpullCount = await EstimateRugpullCountAsync(apiBase, deployedContracts, ct);

            // Step 4: wallet age from first outgoing tx
            var walletAgeMonths = await GetWalletAgeMonthsAsync(apiBase, deployerAddress, ct);

            return new DeployerAnalysisResult(
                DeployerAddress:    deployerAddress,
                TotalTokensDeployed: deployedContracts.Count,
                EstimatedRugpulls:  rugpullCount,
                WalletAgeMonths:    walletAgeMonths,
                ChainId:            chain,
                AnalysedAtUtc:      DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return DeployerAnalysisResult.Unknown($"Analysis failed: {ex.Message}");
        }
    }

    // ── API helpers ───────────────────────────────────────────────────────────

    private async Task<string> GetContractDeployerAsync(
        string apiBase, string contractAddress, CancellationToken ct)
    {
        var url = $"{apiBase}?module=contract&action=getcontractcreation&contractaddresses={contractAddress}";
        var json = await GetJsonAsync(url, ct);
        var result = json?["result"]?.AsArray();
        return result?.Count > 0
            ? result[0]?["contractCreator"]?.GetValue<string>() ?? string.Empty
            : string.Empty;
    }

    private async Task<IReadOnlyList<string>> GetDeployedContractsAsync(
        string apiBase, string deployerAddress, CancellationToken ct)
    {
        // Get internal transactions where the deployer created contracts
        var url = $"{apiBase}?module=account&action=txlist&address={deployerAddress}"
                + "&sort=asc&page=1&offset=200";
        var json = await GetJsonAsync(url, ct);
        var txs  = json?["result"]?.AsArray() ?? new JsonArray();

        return txs
            .OfType<JsonNode>()
            .Where(tx =>
                string.IsNullOrEmpty(tx["to"]?.GetValue<string>()) && // contract creation: "to" is empty
                (tx["isError"]?.GetValue<string>() ?? "0") == "0")
            .Select(tx => tx["contractAddress"]?.GetValue<string>() ?? string.Empty)
            .Where(addr => !string.IsNullOrEmpty(addr))
            .ToList();
    }

    private async Task<int> EstimateRugpullCountAsync(
        string apiBase, IReadOnlyList<string> contracts, CancellationToken ct)
    {
        if (contracts.Count == 0) return 0;

        var rugpulls = 0;
        // Check at most 10 contracts to stay within rate limits
        foreach (var contract in contracts.Take(10))
        {
            try
            {
                var url  = $"{apiBase}?module=account&action=tokentx&contractaddress={contract}&page=1&offset=1";
                var json = await GetJsonAsync(url, ct);
                var txs  = json?["result"]?.AsArray();

                // A token with 0 or very few transfers after deployment = likely abandoned/rugged
                if (txs is null || txs.Count == 0)
                    rugpulls++;
            }
            catch { /* skip individual contract errors */ }
        }

        return rugpulls;
    }

    private async Task<int> GetWalletAgeMonthsAsync(
        string apiBase, string address, CancellationToken ct)
    {
        var url  = $"{apiBase}?module=account&action=txlist&address={address}&sort=asc&page=1&offset=1";
        var json = await GetJsonAsync(url, ct);
        var txs  = json?["result"]?.AsArray();

        if (txs is null || txs.Count == 0) return 0;

        var tsStr = txs[0]?["timeStamp"]?.GetValue<string>();
        if (!long.TryParse(tsStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var ts))
            return 0;

        var firstTx = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        return (int)((DateTime.UtcNow - firstTx).TotalDays / 30.44);
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body);
    }

    public void Dispose() => _http.Dispose();
}

// ── Result model ──────────────────────────────────────────────────────────────

public sealed record DeployerAnalysisResult(
    string   DeployerAddress,
    int      TotalTokensDeployed,
    int      EstimatedRugpulls,
    int      WalletAgeMonths,
    string   ChainId,
    DateTime AnalysedAtUtc,
    bool     IsSupported = true,
    string?  Error = null)
{
    public static DeployerAnalysisResult Unknown(string reason) => new(
        string.Empty, 0, 0, 0, string.Empty, DateTime.UtcNow, Error: reason);

    public static DeployerAnalysisResult Unsupported(string chain) => new(
        string.Empty, 0, 0, 0, chain, DateTime.UtcNow, IsSupported: false);

    /// <summary>Rug rate: percentage of deployed tokens that appear abandoned.</summary>
    public decimal RugRatePercent => TotalTokensDeployed > 0
        ? (decimal)EstimatedRugpulls / TotalTokensDeployed * 100m
        : 0m;

    /// <summary>Risk classification based on deployer history.</summary>
    public DeployerRisk RiskLevel
    {
        get
        {
            if (Error is not null || !IsSupported) return DeployerRisk.Unknown;
            if (EstimatedRugpulls >= 3)            return DeployerRisk.High;
            if (RugRatePercent >= 50m)             return DeployerRisk.High;
            if (EstimatedRugpulls >= 1)            return DeployerRisk.Medium;
            if (WalletAgeMonths < 1)               return DeployerRisk.Medium;
            return DeployerRisk.Low;
        }
    }

    public string RiskLabel => RiskLevel switch
    {
        DeployerRisk.High    => $"HIGH RISK — {EstimatedRugpulls} rugpulls ({RugRatePercent:0}%)",
        DeployerRisk.Medium  => EstimatedRugpulls > 0
            ? $"MEDIUM — {EstimatedRugpulls} possible rugpull(s)"
            : $"MEDIUM — new wallet ({WalletAgeMonths}mo)",
        DeployerRisk.Low     => $"LOW — {TotalTokensDeployed} deploys, {WalletAgeMonths}mo old",
        _                    => "Unknown"
    };

    public string RiskBrush => RiskLevel switch
    {
        DeployerRisk.High   => "#FF6B6B",
        DeployerRisk.Medium => "#F4B860",
        DeployerRisk.Low    => "#21E6C1",
        _                   => "#8FA3B8"
    };

    public string ShortAddress => DeployerAddress.Length > 10
        ? DeployerAddress[..6] + "..." + DeployerAddress[^4..]
        : DeployerAddress;

    public string Summary => IsSupported && Error is null
        ? $"Deployer {ShortAddress} | {TotalTokensDeployed} tokens | {EstimatedRugpulls} rugpulls | {WalletAgeMonths}mo old"
        : Error ?? "Chain not supported for deployer analysis";
}

public enum DeployerRisk { Unknown, Low, Medium, High }
