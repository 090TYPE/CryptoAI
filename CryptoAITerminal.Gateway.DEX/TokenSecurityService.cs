using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Queries GoPlus Security (EVM), Honeypot.is (ETH/BSC), and RugCheck.xyz (Solana).
/// All free-tier — no API keys required for basic usage.
/// GoPlus + Honeypot.is run in parallel for EVM chains.
/// </summary>
public sealed class TokenSecurityService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // DexScreener chainId → GoPlus numeric chainId
    private static readonly IReadOnlyDictionary<string, string> GoPlusChains =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"]  = "1",
            ["eth"]       = "1",
            ["bsc"]       = "56",
            ["bnb"]       = "56",
            ["polygon"]   = "137",
            ["matic"]     = "137",
            ["arbitrum"]  = "42161",
            ["base"]      = "8453",
            ["optimism"]  = "10",
            ["avalanche"] = "43114",
            ["avax"]      = "43114",
            ["fantom"]    = "250",
            ["ftm"]       = "250",
        };

    // Honeypot.is supports a subset of EVM chains
    private static readonly IReadOnlyDictionary<string, int> HoneypotChains =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"]  = 1,
            ["eth"]       = 1,
            ["bsc"]       = 56,
            ["bnb"]       = 56,
            ["polygon"]   = 137,
            ["matic"]     = 137,
            ["arbitrum"]  = 42161,
            ["base"]      = 8453,
        };

    // ── Public entry point ────────────────────────────────────────────────────

    public async Task<TokenSecurityResult> ScanAsync(
        string tokenAddress,
        string chainId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenAddress))
            return TokenSecurityResult.Unknown("No token address");

        var chain = chainId.Trim().ToLowerInvariant();

        TokenSecurityResult result;
        if (chain is "solana" or "sol")
            result = await ScanSolanaAsync(tokenAddress, ct);
        else if (GoPlusChains.ContainsKey(chain) || HoneypotChains.ContainsKey(chain))
            result = await ScanEvmAsync(tokenAddress, chain, ct);
        else
            return TokenSecurityResult.Unknown($"Unsupported chain: {chainId}");

        // Run deployer analysis in parallel with security scan (best-effort, non-blocking)
        _ = EnrichWithDeployerAnalysisAsync(result, tokenAddress, chain, ct);

        return result;
    }

    private async Task EnrichWithDeployerAnalysisAsync(
        TokenSecurityResult result, string tokenAddress, string chain, CancellationToken ct)
    {
        try
        {
            using var analyzer = new DeployerWalletAnalyzer();
            var analysis = await analyzer.AnalyseTokenDeployerAsync(tokenAddress, chain, ct);
            if (analysis.IsSupported && analysis.Error is null)
            {
                result.ApplyDeployerRaw(
                    analysis.DeployerAddress,
                    analysis.TotalTokensDeployed,
                    analysis.EstimatedRugpulls,
                    analysis.WalletAgeMonths,
                    analysis.RiskLabel,
                    analysis.RiskBrush,
                    (int)analysis.RiskLevel);
            }
        }
        catch { /* deployer analysis is optional — ignore failures */ }
    }

    // ── EVM: GoPlus + Honeypot.is (parallel) ─────────────────────────────────

    private async Task<TokenSecurityResult> ScanEvmAsync(
        string address, string chain, CancellationToken ct)
    {
        var flags = new List<string>();

        // Launch both in parallel
        var goPlusTask   = GoPlusChains.TryGetValue(chain, out var gpId)
            ? TryGoPlusAsync(address, gpId, ct)
            : Task.FromResult<(bool success, JsonElement token)>((false, default));

        var honeypotTask = HoneypotChains.TryGetValue(chain, out var hpId)
            ? TryHoneypotIsAsync(address, hpId, ct)
            : Task.FromResult<(bool isHoneypot, decimal buyTax, decimal sellTax)>((false, 0m, 0m));

        await Task.WhenAll(goPlusTask, honeypotTask);

        var (gpOk,  gpToken)   = await goPlusTask;
        var (hpHp,  hpBuy, hpSell) = await honeypotTask;

        var result = new TokenSecurityResult { Source = "GoPlus + Honeypot.is" };

        // ── Parse GoPlus data ─────────────────────────────────────────────────
        if (gpOk)
        {
            result.IsHoneypot              = GpFlag(gpToken, "is_honeypot");
            result.HasMintFunction          = GpFlag(gpToken, "is_mintable");
            result.HasBlacklist             = GpFlag(gpToken, "is_blacklisted") || GpFlag(gpToken, "is_blacklist_function");
            result.IsProxy                  = GpFlag(gpToken, "is_proxy");
            result.HasSelfDestruct          = GpFlag(gpToken, "selfdestruct");
            result.HiddenOwner              = GpFlag(gpToken, "hidden_owner");
            result.BuyTaxPercent            = GpTax(gpToken, "buy_tax");
            result.SellTaxPercent           = GpTax(gpToken, "sell_tax");
            result.TopHolderConcentrationPercent = GpTopHolder(gpToken);

            // Owner renounced
            if (gpToken.TryGetProperty("owner_address", out var ownerEl))
            {
                var oa = ownerEl.GetString() ?? string.Empty;
                result.IsOwnershipRenounced = string.IsNullOrWhiteSpace(oa)
                    || oa == "0x0000000000000000000000000000000000000000"
                    || oa.Equals("0x000000000000000000000000000000000000dead", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── Override with Honeypot.is if it detected a honeypot ───────────────
        if (hpHp && !result.IsHoneypot)
            result.IsHoneypot = true;

        // Use honeypot.is taxes if GoPlus didn't provide them
        if (result.BuyTaxPercent == 0m && hpBuy > 0m)
            result.BuyTaxPercent = hpBuy;
        if (result.SellTaxPercent == 0m && hpSell > 0m)
            result.SellTaxPercent = hpSell;

        if (!gpOk && !hpHp)
            result.Source = "No scanner responded";

        // ── Build flags ───────────────────────────────────────────────────────
        if (result.IsHoneypot)                 flags.Add("🛑 Honeypot");
        if (result.HasMintFunction)             flags.Add("⚠ Mintable");
        if (!result.IsOwnershipRenounced && gpOk) flags.Add("⚠ Owner not renounced");
        if (result.HasBlacklist)               flags.Add("⚠ Blacklist function");
        if (result.TopHolderConcentrationPercent > 30m)
            flags.Add($"⚠ Top holder {result.TopHolderConcentrationPercent:N0}%");
        if (result.IsProxy)                    flags.Add("⚠ Proxy contract");
        if (result.HasSelfDestruct)            flags.Add("⚠ Self-destruct");
        if (result.HiddenOwner)                flags.Add("⚠ Hidden owner");
        if (result.BuyTaxPercent > 10m)        flags.Add($"⚠ Buy tax {result.BuyTaxPercent:N0}%");
        else if (result.BuyTaxPercent > 5m)    flags.Add($"Buy tax {result.BuyTaxPercent:N0}%");
        if (result.SellTaxPercent > 15m)       flags.Add($"⚠ Sell tax {result.SellTaxPercent:N0}%");
        else if (result.SellTaxPercent > 5m)   flags.Add($"Sell tax {result.SellTaxPercent:N0}%");

        result.Flags        = [.. flags];
        result.SecurityScore = ComputeEvmScore(result);
        result.Verdict = result.SecurityScore switch
        {
            <= 30 => "Dangerous",
            <= 55 => "High Risk",
            <= 75 => "Moderate Risk",
            _     => "Likely Safe"
        };

        return result;
    }

    // ── GoPlus Security API ───────────────────────────────────────────────────
    // GET https://api.gopluslabs.io/api/v1/token_security/{chainId}?contract_addresses={addr}

    private async Task<(bool ok, JsonElement token)> TryGoPlusAsync(
        string address, string chainId, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.gopluslabs.io/api/v1/token_security/{chainId}?contract_addresses={address}";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return (false, default);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var resultEl) ||
                resultEl.ValueKind != JsonValueKind.Object)
                return (false, default);

            foreach (var prop in resultEl.EnumerateObject())
                // Clone to survive doc.Dispose()
                return (true, prop.Value.Clone());

            return (false, default);
        }
        catch
        {
            return (false, default);
        }
    }

    // ── Honeypot.is API ───────────────────────────────────────────────────────
    // GET https://api.honeypot.is/v2/IsHoneypot?address={addr}&chainID={id}

    private async Task<(bool isHoneypot, decimal buyTax, decimal sellTax)> TryHoneypotIsAsync(
        string address, int chainId, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.honeypot.is/v2/IsHoneypot?address={address}&chainID={chainId}";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return (false, 0m, 0m);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            bool isHoneypot = root.TryGetProperty("honeypotResult", out var hpRes)
                && hpRes.TryGetProperty("isHoneypot", out var hpFlag)
                && hpFlag.ValueKind == JsonValueKind.True;

            decimal buyTax = 0m, sellTax = 0m;
            if (root.TryGetProperty("simulationResult", out var sim))
            {
                if (sim.TryGetProperty("buyTax",  out var bt) && bt.ValueKind == JsonValueKind.Number)
                    buyTax  = bt.GetDecimal() * 100m;
                if (sim.TryGetProperty("sellTax", out var st) && st.ValueKind == JsonValueKind.Number)
                    sellTax = st.GetDecimal() * 100m;
            }

            return (isHoneypot, buyTax, sellTax);
        }
        catch
        {
            return (false, 0m, 0m);
        }
    }

    // ── RugCheck.xyz (Solana) ─────────────────────────────────────────────────
    // GET https://api.rugcheck.xyz/v1/tokens/{mint}/report/summary

    private async Task<TokenSecurityResult> ScanSolanaAsync(
        string mintAddress, CancellationToken ct)
    {
        var flags = new List<string>();
        var result = new TokenSecurityResult { Source = "RugCheck.xyz" };

        try
        {
            var url = $"https://api.rugcheck.xyz/v1/tokens/{mintAddress}/report/summary";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return TokenSecurityResult.Unknown($"RugCheck returned {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            // RugCheck score: 0–1000, lower = riskier
            int rawScore = 500;
            if (root.TryGetProperty("score", out var scoreEl) &&
                scoreEl.ValueKind == JsonValueKind.Number)
                rawScore = scoreEl.GetInt32();

            result.SecurityScore = Math.Clamp(rawScore / 10, 0, 100);

            if (root.TryGetProperty("risks", out var risksEl) &&
                risksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in risksEl.EnumerateArray())
                {
                    string name  = r.TryGetProperty("name",  out var n) ? n.GetString() ?? "" : "";
                    string level = r.TryGetProperty("level", out var l) ? l.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string pfx = level switch { "danger" => "🛑 ", "warn" => "⚠ ", _ => "" };
                    flags.Add(pfx + name);

                    // Map known risk names to structured flags
                    if (name.Contains("honeypot", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("freeze",   StringComparison.OrdinalIgnoreCase))
                        result.IsHoneypot = true;

                    if (name.Contains("mint", StringComparison.OrdinalIgnoreCase))
                        result.HasMintFunction = true;
                }
            }

            result.Flags   = [.. flags];
            result.Verdict = result.SecurityScore switch
            {
                <= 30 => "Dangerous",
                <= 55 => "High Risk",
                <= 75 => "Moderate Risk",
                _     => "Likely Safe"
            };

            return result;
        }
        catch (Exception ex)
        {
            return TokenSecurityResult.Unknown($"RugCheck.xyz: {ex.Message}");
        }
    }

    // ── Score computation ─────────────────────────────────────────────────────

    private static int ComputeEvmScore(TokenSecurityResult r)
    {
        int score = 100;
        if (r.IsHoneypot)             score -= 50;
        if (r.HasMintFunction)         score -= 25;
        if (!r.IsOwnershipRenounced)   score -= 20;
        if (r.HasBlacklist)            score -= 20;
        if (r.TopHolderConcentrationPercent > 30m) score -= 15;
        if (r.IsProxy)                 score -= 10;
        if (r.HasSelfDestruct)         score -= 10;
        if (r.HiddenOwner)             score -= 10;
        if (r.BuyTaxPercent > 10m)     score -= 10;
        else if (r.BuyTaxPercent > 5m) score -=  5;
        if (r.SellTaxPercent > 15m)    score -= 15;
        else if (r.SellTaxPercent > 5m) score -= 5;
        return Math.Max(0, score);
    }

    // ── GoPlus JSON helpers ───────────────────────────────────────────────────

    private static bool GpFlag(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.GetString() == "1";

    private static decimal GpTax(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v)) return 0m;
        var s = v.GetString();
        if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return 0m;
        return d > 1m ? d : d * 100m;   // GoPlus returns fraction (0.05 → 5%)
    }

    private static decimal GpTopHolder(JsonElement token)
    {
        if (!token.TryGetProperty("holders", out var holdersEl) ||
            holdersEl.ValueKind != JsonValueKind.Array)
            return 0m;

        decimal top = 0m;
        foreach (var h in holdersEl.EnumerateArray())
        {
            if (!h.TryGetProperty("percent", out var pctEl)) continue;
            var s = pctEl.GetString();
            if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct)) continue;
            var pct100 = pct > 1m ? pct : pct * 100m;
            if (pct100 > top) top = pct100;
        }
        return top;
    }

    public void Dispose() => _http.Dispose();
}
