using System.Globalization;
using System.Text.Json;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>One valued wallet holding on a specific chain.</summary>
public sealed record PortfolioHolding(
    string Chain,
    string Symbol,
    decimal Balance,
    decimal ValueUsd,
    decimal PriceUsd);

/// <summary>
/// Cross-chain wallet portfolio via Covalent (GoldRush) <c>balances_v2</c>: a single keyed call
/// per chain returns every token the wallet holds, already valued in USD. Aggregates the wired
/// EVM chains into one holdings list. Degrades to an empty list on any failure so the caller can
/// fall back to its keyless single-chain read. Parsing is pure and unit-tested.
/// </summary>
public sealed class PortfolioAggregatorService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Covalent chain-id → display name for the chains we aggregate.</summary>
    public static readonly IReadOnlyList<(int ChainId, string Name)> Chains = new[]
    {
        (1, "Ethereum"),
        (56, "BSC"),
        (137, "Polygon"),
        (8453, "Base"),
        (42161, "Arbitrum"),
        (10, "Optimism"),
        (43114, "Avalanche"),
    };

    /// <summary>Fetches and aggregates valued holdings across all wired chains, sorted by value desc.</summary>
    public async Task<IReadOnlyList<PortfolioHolding>> GetAllHoldingsAsync(
        string apiKey, string address, decimal dustThresholdUsd = 1m, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(address))
        {
            return Array.Empty<PortfolioHolding>();
        }

        var all = new List<PortfolioHolding>();
        foreach (var (chainId, name) in Chains)
        {
            ct.ThrowIfCancellationRequested();
            all.AddRange(await GetChainHoldingsAsync(apiKey, chainId, name, address, ct));
        }

        return all
            .Where(h => h.ValueUsd >= dustThresholdUsd)
            .OrderByDescending(h => h.ValueUsd)
            .ToList();
    }

    /// <summary>Fetches valued holdings for a single chain. Empty on failure.</summary>
    public async Task<IReadOnlyList<PortfolioHolding>> GetChainHoldingsAsync(
        string apiKey, int chainId, string chainName, string address, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.covalenthq.com/v1/{chainId}/address/{address}/balances_v2/?key={apiKey}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Array.Empty<PortfolioHolding>();
            }

            return ParseBalances(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), chainName);
        }
        catch
        {
            return Array.Empty<PortfolioHolding>();
        }
    }

    /// <summary>Pure parser for a Covalent balances_v2 response.</summary>
    public static IReadOnlyList<PortfolioHolding> ParseBalances(string json, string chainName)
    {
        var holdings = new List<PortfolioHolding>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return holdings;
            }

            foreach (var item in items.EnumerateArray())
            {
                var symbol = Str(item, "contract_ticker_symbol");
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                var decimals = item.TryGetProperty("contract_decimals", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetInt32() : 18;
                var rawBalance = Str(item, "balance") ?? "0";
                if (!System.Numerics.BigInteger.TryParse(rawBalance, out var raw) || raw.IsZero)
                {
                    continue;
                }

                var factor = Pow10(decimals);
                var balance = factor > 0m ? (decimal)raw / factor : (decimal)raw;
                var valueUsd = Dec(item, "quote");
                var price = Dec(item, "quote_rate");

                holdings.Add(new PortfolioHolding(chainName, symbol!, balance, valueUsd, price));
            }
        }
        catch
        {
            // malformed payload → return whatever parsed so far (usually empty)
        }

        return holdings;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Dec(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return 0m;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m,
        };
    }

    private static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n && i < 28; i++) r *= 10m;
        return r;
    }
}
