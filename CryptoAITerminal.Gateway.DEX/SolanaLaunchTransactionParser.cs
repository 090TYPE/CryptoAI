using System.Globalization;

namespace CryptoAITerminal.Gateway.DEX;

public sealed class SolanaLaunchTransactionParser
{
    private static readonly HashSet<string> IgnoredMintAddresses = new(StringComparer.OrdinalIgnoreCase)
    {
        "So11111111111111111111111111111111111111112",
        "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v",
        "Es9vMFrzaCER3xMNoZqRkNvztNFVQVw1Gc7YDxjx3sG",
        "DezXAZ8z7PnrnRJjz3wXBoRgixCa6vH2Aq6P4vQ2Enm"
    };

    public IReadOnlyList<string> ExtractTokenCandidates(
        SolanaParsedTransaction transaction,
        SolanaProgramMonitorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(definition);

        var directCandidates = transaction.InitializedMints
            .Where(IsInterestingMint)
            .ToList();
        if (directCandidates.Count > 0)
        {
            return directCandidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var postBalanceCandidates = transaction.PostTokenBalances
            .Where(static balance => balance.UiAmount >= 0m)
            .Select(static balance => balance.Mint)
            .Where(IsInterestingMint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (postBalanceCandidates.Count > 0)
        {
            return postBalanceCandidates;
        }

        var fallbackFromLogs = transaction.LogMessages
            .Where(static log => log.Contains("initialize", StringComparison.OrdinalIgnoreCase) ||
                                 log.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                                 log.Contains("launch", StringComparison.OrdinalIgnoreCase) ||
                                 log.Contains("migrate", StringComparison.OrdinalIgnoreCase))
            .Any();

        if (!fallbackFromLogs)
        {
            return [];
        }

        return transaction.PostTokenBalances
            .Select(static balance => balance.Mint)
            .Where(IsInterestingMint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsInterestingMint(string? mintAddress)
    {
        return !string.IsNullOrWhiteSpace(mintAddress) &&
               SolanaRpcClient.IsValidAddress(mintAddress) &&
               !IgnoredMintAddresses.Contains(mintAddress);
    }
}

public sealed record SolanaParsedTransaction(
    string Signature,
    long Slot,
    long? BlockTimeUnixSeconds,
    long FeeLamports,
    IReadOnlyList<string> AccountKeys,
    IReadOnlyList<string> LogMessages,
    IReadOnlyList<string> InitializedMints,
    IReadOnlyList<SolanaParsedTokenBalance> PostTokenBalances);

public sealed record SolanaParsedTokenBalance(
    string Mint,
    string? Owner,
    string? ProgramId,
    decimal UiAmount);
