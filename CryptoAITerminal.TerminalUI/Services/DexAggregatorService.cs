using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Best-route swap quote from an aggregator.</summary>
public sealed record DexAggregatorQuote(
    decimal OutTokens,
    string OutSymbol,
    string PriceImpact,
    string Route,
    bool IsLive);

/// <summary>
/// Keyless EVM swap-route aggregation via OpenOcean — returns the best expected output,
/// price impact and the DEX route across all pools, so the SWAP preview shows the real
/// best price before the user swaps. Parsing is a pure, tested method; the network call
/// degrades to null. Native token uses OpenOcean's 0xEeee… placeholder.
/// </summary>
public sealed class DexAggregatorService
{
    public const string NativePlaceholder = "0xEeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeE";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>OpenOcean chain slug for a token chain id (null = unsupported here).</summary>
    public static string? ChainSlug(string chainId) => chainId?.Trim().ToLowerInvariant() switch
    {
        "ethereum" or "eth" => "eth",
        "bsc" => "bsc",
        "base" => "base",
        "arbitrum" => "arbitrum",
        "polygon" => "polygon",
        "avalanche" or "avax" => "avax",
        "optimism" => "optimism",
        _ => null,
    };

    public async Task<DexAggregatorQuote?> GetQuoteAsync(
        string chainSlug, string inToken, string outToken, decimal amount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chainSlug) || string.IsNullOrWhiteSpace(inToken) ||
            string.IsNullOrWhiteSpace(outToken) || amount <= 0m)
        {
            return null;
        }

        try
        {
            var url = $"https://open-api.openocean.finance/v3/{chainSlug}/quote" +
                      $"?inTokenAddress={inToken}&outTokenAddress={outToken}" +
                      $"&amount={amount.ToString("0.########", CultureInfo.InvariantCulture)}&gasPrice=5";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            return Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pure parser for an OpenOcean quote response.</summary>
    public static DexAggregatorQuote? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            var outDec = data.TryGetProperty("outToken", out var ot) && ot.TryGetProperty("decimals", out var od) ? od.GetInt32() : 18;
            var outSymbol = ot.ValueKind == JsonValueKind.Object && ot.TryGetProperty("symbol", out var os) ? os.GetString() ?? "" : "";
            var outRaw = data.TryGetProperty("outAmount", out var oa) ? oa.GetString() ?? "0" : "0";
            if (!decimal.TryParse(outRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var outSmallest))
            {
                return null;
            }
            var factor = Pow10(outDec);
            var outTokens = factor > 0m ? outSmallest / factor : outSmallest;

            var impact = data.TryGetProperty("price_impact", out var pi) && pi.ValueKind == JsonValueKind.String
                ? pi.GetString() ?? "—" : "—";

            var route = ExtractRoute(data);

            return new DexAggregatorQuote(outTokens, outSymbol, impact, route, true);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractRoute(JsonElement data)
    {
        var dexes = new List<string>();
        if (data.TryGetProperty("path", out var path) && path.TryGetProperty("routes", out var routes) && routes.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in routes.EnumerateArray())
            {
                if (!r.TryGetProperty("subRoutes", out var subs) || subs.ValueKind != JsonValueKind.Array) continue;
                foreach (var s in subs.EnumerateArray())
                {
                    if (!s.TryGetProperty("dexes", out var ds) || ds.ValueKind != JsonValueKind.Array) continue;
                    foreach (var d in ds.EnumerateArray())
                    {
                        if (d.TryGetProperty("dex", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            var n = name.GetString();
                            if (!string.IsNullOrWhiteSpace(n) && !dexes.Contains(n)) dexes.Add(n!);
                        }
                    }
                }
            }
        }

        if (dexes.Count == 0 && data.TryGetProperty("dexes", out var top) && top.ValueKind == JsonValueKind.Array && top.GetArrayLength() > 0)
        {
            if (top[0].TryGetProperty("dexCode", out var code) && code.ValueKind == JsonValueKind.String)
                dexes.Add(code.GetString() ?? "");
        }

        return dexes.Count == 0 ? "—" : string.Join(" + ", dexes.Take(3));
    }

    private static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n && i < 28; i++) r *= 10m;
        return r;
    }
}
