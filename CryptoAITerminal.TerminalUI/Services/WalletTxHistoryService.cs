using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>One recent on-chain transaction for a wallet, normalised across chains.</summary>
public sealed record WalletTx(
    string Hash,
    DateTime TimeUtc,
    string Direction,   // "in" | "out" | "self"
    decimal Amount,     // native units (signed by direction handled at display)
    string AssetSymbol,
    decimal FeeNative,
    bool Failed,
    string Action);     // "Sent", "Received", "Contract call", …

/// <summary>
/// Real per-wallet transaction history. EVM chains use the Etherscan v2 multichain
/// endpoint (one key, chainid selects the chain); Tron uses TronGrid. Keys come
/// from the encrypted credential store (surfaced as process env vars by
/// <see cref="CredentialsService"/>). Returns an empty list when no key is set or
/// the address/chain isn't supported — never fabricates entries.
/// </summary>
public sealed class WalletTxHistoryService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly Dictionary<string, (int ChainId, string NativeSymbol)> EvmChains =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ethereum"] = (1, "ETH"),
            ["ETH"]      = (1, "ETH"),
            ["BSC"]      = (56, "BNB"),
            ["Arbitrum"] = (42161, "ETH"),
            ["ARB"]      = (42161, "ETH"),
            ["Base"]     = (8453, "ETH"),
            ["BASE"]     = (8453, "ETH"),
            ["Avalanche"]= (43114, "AVAX"),
            ["AVAX"]     = (43114, "AVAX"),
        };

    public async Task<IReadOnlyList<WalletTx>> GetRecentAsync(string address, string chain, int limit = 15)
    {
        if (string.IsNullOrWhiteSpace(address)) return Array.Empty<WalletTx>();
        try
        {
            if (EvmChains.TryGetValue(chain ?? "", out var evm))
                return await GetEvmAsync(address.Trim(), evm.ChainId, evm.NativeSymbol, limit);
            if (string.Equals(chain, "Tron", StringComparison.OrdinalIgnoreCase))
                return await GetTronAsync(address.Trim(), limit);
        }
        catch
        {
            // Network/parse failure → honest empty rather than a fake history.
        }
        return Array.Empty<WalletTx>();
    }

    private static async Task<IReadOnlyList<WalletTx>> GetEvmAsync(string address, int chainId, string nativeSymbol, int limit)
    {
        var key = Environment.GetEnvironmentVariable("ETHERSCAN_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<WalletTx>();

        var url = $"https://api.etherscan.io/v2/api?chainid={chainId}&module=account&action=txlist" +
                  $"&address={address}&startblock=0&endblock=99999999&page=1&offset={limit}&sort=desc&apikey={key}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = doc.RootElement;
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return Array.Empty<WalletTx>();

        var list = new List<WalletTx>();
        foreach (var tx in result.EnumerateArray())
        {
            var hash = Str(tx, "hash");
            var from = Str(tx, "from");
            var to   = Str(tx, "to");
            var valueWei = Dec(tx, "value");
            var gasUsed  = Dec(tx, "gasUsed");
            var gasPrice = Dec(tx, "gasPrice");
            var ts       = long.TryParse(Str(tx, "timeStamp"), out var t) ? t : 0;
            var failed   = Str(tx, "isError") == "1";
            var isOut    = string.Equals(from, address, StringComparison.OrdinalIgnoreCase);
            var isIn     = string.Equals(to, address, StringComparison.OrdinalIgnoreCase);
            var dir      = isOut && isIn ? "self" : isOut ? "out" : "in";
            var amount   = valueWei / 1_000_000_000_000_000_000m;
            var fee      = (gasUsed * gasPrice) / 1_000_000_000_000_000_000m;
            var action   = amount == 0 ? "Contract call" : dir == "out" ? "Sent" : "Received";
            list.Add(new WalletTx(hash, DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime,
                                  dir, amount, nativeSymbol, isOut ? fee : 0m, failed, action));
        }
        return list;
    }

    private static async Task<IReadOnlyList<WalletTx>> GetTronAsync(string address, int limit)
    {
        var url = $"https://api.trongrid.io/v1/accounts/{address}/transactions?limit={limit}&only_confirmed=true";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var key = Environment.GetEnvironmentVariable("TRONGRID_API_KEY");
        if (!string.IsNullOrWhiteSpace(key)) req.Headers.Add("TRON-PRO-API-KEY", key);
        using var resp = await Http.SendAsync(req);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<WalletTx>();

        var list = new List<WalletTx>();
        foreach (var tx in data.EnumerateArray())
        {
            var hash = Str(tx, "txID");
            var ts   = tx.TryGetProperty("block_timestamp", out var bt) && bt.TryGetInt64(out var ms) ? ms : 0;
            var failed = false;
            if (tx.TryGetProperty("ret", out var ret) && ret.ValueKind == JsonValueKind.Array && ret.GetArrayLength() > 0
                && ret[0].TryGetProperty("contractRet", out var cr))
                failed = cr.GetString() != "SUCCESS";
            // TRX contract value (best-effort): most transfers carry contract data we don't fully decode here.
            list.Add(new WalletTx(hash, DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                                  "self", 0m, "TRX", 0m, failed, "Tron tx"));
        }
        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.GetString() ?? "") : "";

    private static decimal Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
}
