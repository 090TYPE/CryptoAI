using System.Collections.Generic;
using System.Globalization;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Flattens an on-chain <see cref="TokenSecurityResult"/> into a keyword-rich
/// one-line summary fed back into <see cref="TokenSecurityAiService"/>. Its
/// offline heuristic looks for "honeypot"/"mintable"/"blacklist"; the live
/// model reads the whole sentence.
/// </summary>
public static class DexSecuritySummary
{
    public static string Build(TokenSecurityResult? result)
    {
        if (result is null)
            return string.Empty;

        var parts = new List<string>();
        if (result.IsHoneypot)            parts.Add("honeypot");
        if (result.HasMintFunction)       parts.Add("mintable");
        if (result.HasBlacklist)          parts.Add("blacklist");
        if (result.HasSelfDestruct)       parts.Add("self-destruct");
        if (result.HiddenOwner)           parts.Add("hidden owner");
        if (!result.IsOwnershipRenounced) parts.Add("ownership not renounced");
        if (result.BuyTaxPercent > 0m)
            parts.Add($"buy tax {result.BuyTaxPercent.ToString("0.#", CultureInfo.InvariantCulture)}%");
        if (result.SellTaxPercent > 0m)
            parts.Add($"sell tax {result.SellTaxPercent.ToString("0.#", CultureInfo.InvariantCulture)}%");
        if (result.TopHolderConcentrated)
            parts.Add($"top holder {result.TopHolderConcentrationPercent.ToString("0.#", CultureInfo.InvariantCulture)}%");
        if (result.DeployerRugpullCount > 0)
            parts.Add($"deployer {result.DeployerRugpullCount} prior rugpull(s)");

        foreach (var flag in result.Flags)
            if (!string.IsNullOrWhiteSpace(flag))
                parts.Add(flag);

        var source = string.IsNullOrWhiteSpace(result.Source) ? "on-chain scan" : result.Source;
        return parts.Count == 0
            ? $"On-chain scan clean (score {result.SecurityScore}/100, {source})."
            : $"On-chain scan ({source}): {string.Join(", ", parts)}.";
    }
}
