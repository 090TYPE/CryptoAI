using System;
using System.Threading.Tasks;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

public sealed class SniperLiveExecutionService
{
    public const decimal TokenDustThreshold = 0.00000001m;

    private const int LiveExitRetryCount = 3;
    private const int LiveExitRetryDelayMilliseconds = 1500;

    public void ApplyConfirmedEntry(SniperCandidateViewModel candidate, DexBuyExecutionResult buyResult)
    {
        candidate.UsesLiveAccounting = true;
        candidate.LiveEntryCostNative = buyResult.SpendAssetAmount > 0m ? buyResult.SpendAssetAmount : buyResult.NativeAmountSpent;
        candidate.LiveRealizedProceedsNative = 0m;
        candidate.LiveEntryTokenAmount = candidate.TrackedTokenAmount;
        candidate.EntryAmountBnb = candidate.LiveEntryCostNative > 0m ? candidate.LiveEntryCostNative : candidate.EntryAmountBnb;
        candidate.EntryTxHash = buyResult.TransactionHash;
        candidate.EntryDexId = buyResult.DexId ?? candidate.TokenInfo.DexId;

        if (candidate.TrackedTokenAmount > TokenDustThreshold)
        {
            var nativeUsd = candidate.TokenInfo.PriceNative > 0m
                ? candidate.TokenInfo.PriceUsd / candidate.TokenInfo.PriceNative
                : 0m;
            var actualEntryPriceUsd = buyResult.UsedQuoteAsset && candidate.LiveEntryCostNative > 0m
                ? candidate.LiveEntryCostNative / candidate.TrackedTokenAmount
                : nativeUsd > 0m && candidate.LiveEntryCostNative > 0m
                ? (candidate.LiveEntryCostNative / candidate.TrackedTokenAmount) * nativeUsd
                : candidate.TokenInfo.PriceUsd;
            candidate.EntryPriceUsd = actualEntryPriceUsd > 0m ? actualEntryPriceUsd : candidate.TokenInfo.PriceUsd;
        }
        else
        {
            candidate.EntryPriceUsd = candidate.TokenInfo.PriceUsd;
        }

        RecalculateLivePositionSize(candidate);
    }

    public async Task<SniperLiveExitExecutionResult> ExecuteSellWithRetriesAsync(
        SniperCandidateViewModel position,
        IDexTradeGateway gateway,
        decimal sellFraction,
        string exitLabel,
        decimal slippagePercent = 3m)
    {
        Exception? lastException = null;
        string? lastFailureReason = null;

        for (var attempt = 1; attempt <= LiveExitRetryCount; attempt++)
        {
            var onChainTokenBalance = await TryReadTokenBalanceAsync(gateway, position.TokenInfo.TokenAddress);
            if (onChainTokenBalance is > 0m)
            {
                position.TrackedTokenAmount = onChainTokenBalance.Value;
            }

            var availableTokens = position.TrackedTokenAmount;
            if (availableTokens <= TokenDustThreshold)
            {
                return new SniperLiveExitExecutionResult(
                    false,
                    string.Empty,
                    0m,
                    0m,
                    0m,
                    "On-chain token balance is zero or unavailable. Re-arm the wallet and verify the token balance before selling.");
            }

            var amountToSell = sellFraction >= 0.999m
                ? availableTokens
                : Math.Max(TokenDustThreshold, availableTokens * Math.Clamp(sellFraction, 0.01m, 0.99m));

            try
            {
                position.Status = $"Sell attempt {attempt}/{LiveExitRetryCount} on {exitLabel}...";
                var sellResult = await gateway.ExecuteConfirmedSellAsync(new DexSellExecutionRequest(
                    position.TokenInfo.TokenAddress,
                    amountToSell,
                    slippagePercent,
                    DexId: position.TokenInfo.DexId));

                position.LiveRealizedProceedsNative += sellResult.ActualReceiveAmount > 0m
                    ? sellResult.ActualReceiveAmount
                    : sellResult.ActualNativeAmountReceived;
                position.TrackedTokenAmount = sellResult.TokenBalanceAfter;
                position.LastExitTxHash = sellResult.TransactionHash;
                RecalculateLivePositionSize(position);

                return new SniperLiveExitExecutionResult(
                    true,
                    sellResult.TransactionHash,
                    sellResult.ActualTokenAmountSold,
                    sellResult.ActualReceiveAmount > 0m ? sellResult.ActualReceiveAmount : sellResult.ActualNativeAmountReceived,
                    sellResult.TokenBalanceAfter,
                    null,
                    sellResult);
            }
            catch (Exception ex)
            {
                lastException = ex;
                lastFailureReason = $"Attempt {attempt}/{LiveExitRetryCount} failed: {ex.Message}";
                position.Status = $"Retrying sell {attempt}/{LiveExitRetryCount}...";
                if (attempt < LiveExitRetryCount)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(LiveExitRetryDelayMilliseconds * attempt));
                }
            }
        }

        return new SniperLiveExitExecutionResult(
            false,
            string.Empty,
            0m,
            0m,
            position.TrackedTokenAmount,
            lastFailureReason ?? lastException?.Message ?? "Sell retries exhausted.");
    }

    public async Task<decimal?> TryReadTokenBalanceAsync(IDexTradeGateway gateway, string tokenAddress)
    {
        try
        {
            return await gateway.GetTokenBalanceAsync(tokenAddress);
        }
        catch
        {
            return null;
        }
    }

    public void RecalculateLivePositionSize(SniperCandidateViewModel position)
    {
        if (!position.UsesLiveAccounting || position.LiveEntryTokenAmount <= TokenDustThreshold)
        {
            return;
        }

        position.PositionSizePercent = Math.Max(
            0m,
            Math.Min(100m, (position.TrackedTokenAmount / position.LiveEntryTokenAmount) * 100m));
    }
}

public sealed record SniperLiveExitExecutionResult(
    bool Success,
    string TransactionHash,
    decimal SoldTokenAmount,
    decimal RealizedNativeDelta,
    decimal RemainingTokenBalance,
    string? FailureReason,
    DexSellExecutionResult? SellExecution = null);
