using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Pure client-side filter + sort for the DEX Market token list.
/// <paramref name="chainId"/> null/empty = no chain filter (compared case-insensitively
/// against <see cref="DexTokenInfo.ChainId"/>). <paramref name="sortMode"/> "Liquidity"
/// or "Change" sort by those fields, anything else by 24h volume. All sorts descending.
/// </summary>
public static class DexTokenFilter
{
    public static IReadOnlyList<DexTokenInfo> Apply(
        IReadOnlyList<DexTokenInfo> tokens,
        string? chainId,
        decimal minLiquidity,
        decimal minVolume,
        string sortMode)
    {
        if (tokens is null || tokens.Count == 0)
            return Array.Empty<DexTokenInfo>();

        IEnumerable<DexTokenInfo> query = tokens;

        if (!string.IsNullOrWhiteSpace(chainId))
            query = query.Where(t => string.Equals(t.ChainId, chainId, StringComparison.OrdinalIgnoreCase));
        if (minLiquidity > 0m)
            query = query.Where(t => t.LiquidityUsd >= minLiquidity);
        if (minVolume > 0m)
            query = query.Where(t => t.Volume24h >= minVolume);

        query = sortMode switch
        {
            "Liquidity" => query.OrderByDescending(t => t.LiquidityUsd),
            "Change"    => query.OrderByDescending(t => t.PriceChange24h),
            _           => query.OrderByDescending(t => t.Volume24h),
        };

        return query.ToList();
    }
}
