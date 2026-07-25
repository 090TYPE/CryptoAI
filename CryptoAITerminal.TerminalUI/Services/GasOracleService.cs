using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>A single gas-priority tier (gwei) derived from the live base fee.</summary>
public sealed record GasTier(string Key, string Label, decimal Gwei);

/// <summary>
/// Live gas oracle for the DEX ticket. Fetches the chain's current gas price via a
/// public RPC (<c>eth_gasPrice</c>) and derives four priority tiers; the tier math is a
/// pure, unit-tested function and the network call degrades to a sensible default per
/// chain when the RPC is unreachable.
/// </summary>
public sealed class GasOracleService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly IReadOnlyDictionary<string, string> Rpc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ethereum"] = "https://ethereum-rpc.publicnode.com",
        ["eth"] = "https://ethereum-rpc.publicnode.com",
        ["bsc"] = "https://bsc-rpc.publicnode.com",
        ["base"] = "https://base-rpc.publicnode.com",
        ["arbitrum"] = "https://arbitrum-one-rpc.publicnode.com",
        ["polygon"] = "https://polygon-bor-rpc.publicnode.com",
    };

    /// <summary>Reasonable fallback base gwei per chain when the RPC can't be reached.</summary>
    public static decimal DefaultBaseGwei(string chain) => chain?.Trim().ToLowerInvariant() switch
    {
        "bsc" => 3m,
        "base" => 0.05m,
        "arbitrum" => 0.1m,
        "polygon" => 40m,
        _ => 8m, // ethereum-ish
    };

    /// <summary>Pure tier ladder from a base gwei. Always ascending and positive.</summary>
    public static IReadOnlyList<GasTier> BuildTiers(decimal baseGwei)
    {
        var b = Math.Max(0.001m, baseGwei);
        return new[]
        {
            new GasTier("Slow", "SLOW", RoundGwei(b * 0.85m)),
            new GasTier("Standard", "STANDARD", RoundGwei(b)),
            new GasTier("Fast", "FAST", RoundGwei(b * 1.25m)),
            new GasTier("Instant", "INSTANT", RoundGwei(b * 1.6m)),
        };
    }

    /// <summary>Convert a hex wei quantity ("0x..") to gwei. Returns null on bad input.</summary>
    public static decimal? HexWeiToGwei(string? hexWei)
    {
        if (string.IsNullOrWhiteSpace(hexWei))
        {
            return null;
        }

        var s = hexWei.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        if (s.Length == 0 || !ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var wei))
        {
            return null;
        }

        return wei / 1_000_000_000m;
    }

    /// <summary>Best-effort live base gwei for a chain. Null when unavailable.</summary>
    public async Task<decimal?> FetchBaseGweiAsync(string chain, CancellationToken ct = default)
    {
        if (chain is null || !Rpc.TryGetValue(chain.Trim(), out var url))
        {
            return null;
        }

        // The server already samples eth_gasPrice from these very RPCs for everyone, so one
        // shared read spares a hundred terminals from each hammering publicnode. Null (unbound,
        // chain absent or stale) falls through to the direct RPC below.
        if (ServerDataClient.IsBound)
        {
            var served = await ServerDataClient.GetGasAsync(ct).ConfigureAwait(false);
            if (ServerGwei(served, chain) is { } gwei)
            {
                return gwei;
            }
        }

        try
        {
            const string body = "{\"jsonrpc\":\"2.0\",\"method\":\"eth_gasPrice\",\"params\":[],\"id\":1}";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var result))
            {
                return HexWeiToGwei(result.GetString());
            }
        }
        catch
        {
            // degrade to default
        }

        return null;
    }

    private static decimal RoundGwei(decimal gwei) => gwei >= 1m
        ? Math.Round(gwei, 1, MidpointRounding.AwayFromZero)
        : Math.Round(gwei, 3, MidpointRounding.AwayFromZero);

    /// <summary>Base gwei for a chain from an /api/gas body. Null when absent, unusable or stale.</summary>
    private static decimal? ServerGwei(string? json, string chain)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // The collector stores Ethereum as "eth", while this service also accepts the
        // "ethereum" alias — without folding it the row would never be found.
        var key = chain.Trim();
        if (key.Equals("ethereum", StringComparison.OrdinalIgnoreCase))
        {
            key = "eth";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("Chain", out var c) ||
                    c.ValueKind != JsonValueKind.String ||
                    !string.Equals(c.GetString(), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Standard is nullable server-side; a missing price must reach the caller as
                // null so DefaultBaseGwei still applies, never as a zero that floors the ladder.
                if (!row.TryGetProperty("Standard", out var s) ||
                    s.ValueKind != JsonValueKind.Number ||
                    !s.TryGetDecimal(out var gwei) ||
                    gwei <= 0m)
                {
                    return null;
                }

                // A collector that stopped writing must not pin the ticket to an old gwei forever.
                if (row.TryGetProperty("Ts", out var ts) &&
                    ts.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp) &&
                    DateTime.UtcNow - stamp > TimeSpan.FromMinutes(10))
                {
                    return null;
                }

                return gwei;
            }
        }
        catch (JsonException)
        {
            // malformed payload — degrade to the direct RPC
        }

        return null;
    }
}
