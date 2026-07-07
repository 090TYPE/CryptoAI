using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>One active ERC-20 token approval (allowance) granted by a wallet.</summary>
public sealed record TokenApproval(
    string TokenSymbol,
    string TokenAddress,
    string SpenderLabel,
    string SpenderAddress,
    string AllowanceLabel,   // "Unlimited" or a formatted amount
    string RiskLevel);       // "high" | "medium" | "low"

/// <summary>
/// Real ERC-20 approval scan via Covalent's approvals endpoint. Uses the Covalent
/// key from the encrypted credential store (env var). Returns an empty list when
/// no key is set or the chain/address isn't an EVM target — never fabricates rows.
/// Revoking is done out-of-band via the explorer/revoke.cash deep link (no in-app
/// key signing), so the scan itself is read-only and safe.
/// </summary>
public sealed class WalletApprovalsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly Dictionary<string, int> EvmChainIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ethereum"] = 1, ["ETH"] = 1,
        ["BSC"] = 56,
        ["Arbitrum"] = 42161, ["ARB"] = 42161,
        ["Base"] = 8453, ["BASE"] = 8453,
        ["Avalanche"] = 43114, ["AVAX"] = 43114,
    };

    public static int? ChainId(string chain) => EvmChainIds.TryGetValue(chain ?? "", out var id) ? id : null;

    public async Task<IReadOnlyList<TokenApproval>> GetApprovalsAsync(string address, string chain)
    {
        if (string.IsNullOrWhiteSpace(address)) return Array.Empty<TokenApproval>();
        var key = Environment.GetEnvironmentVariable("COVALENT_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<TokenApproval>();
        if (ChainId(chain) is not int id) return Array.Empty<TokenApproval>();

        try
        {
            var url = $"https://api.covalenthq.com/v1/{id}/approvals/{address.Trim()}/?key={key}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Array.Empty<TokenApproval>();

            var list = new List<TokenApproval>();
            foreach (var it in items.EnumerateArray())
            {
                var symbol = Str(it, "ticker_symbol");
                var token  = Str(it, "token_address");
                if (!it.TryGetProperty("spenders", out var spenders) || spenders.ValueKind != JsonValueKind.Array) continue;
                foreach (var sp in spenders.EnumerateArray())
                {
                    var spender     = Str(sp, "spender_address");
                    var spenderName = Str(sp, "spender_address_label");
                    var allowance   = Str(sp, "allowance");
                    var risk        = Str(sp, "risk_factor");   // e.g. "CONSIDER REVOKING" / "LOW RISK"

                    var unlimited = allowance is "UNLIMITED" or "" ||
                                    allowance.Length > 40; // very large raw allowance ≈ unlimited
                    var riskLevel = risk.Contains("REVOK", StringComparison.OrdinalIgnoreCase) ? "high"
                                   : unlimited ? "medium" : "low";

                    list.Add(new TokenApproval(
                        string.IsNullOrWhiteSpace(symbol) ? "TOKEN" : symbol,
                        token,
                        string.IsNullOrWhiteSpace(spenderName) ? Short(spender) : spenderName,
                        spender,
                        unlimited ? "Unlimited" : FormatAllowance(allowance),
                        riskLevel));
                }
            }
            return list;
        }
        catch
        {
            return Array.Empty<TokenApproval>();
        }
    }

    /// <summary>Safe out-of-band revoke: the wallet owner completes it on revoke.cash.</summary>
    public static string RevokeUrl(string address, string chain)
    {
        var id = ChainId(chain) ?? 1;
        return $"https://revoke.cash/address/{address}?chainId={id}";
    }

    private static string FormatAllowance(string raw)
    {
        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d >= 1_000_000 ? d.ToString("N0", CultureInfo.InvariantCulture) : d.ToString("0.####", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(raw) ? "Unlimited" : raw;
    }

    private static string Short(string a) => a.Length > 12 ? $"{a[..6]}…{a[^4..]}" : a;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
}
