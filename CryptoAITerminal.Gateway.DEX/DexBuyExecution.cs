namespace CryptoAITerminal.Gateway.DEX;

public sealed record DexBuyExecutionRequest(
    string TokenAddress,
    decimal NativeAmountToSpend,
    decimal SlippagePercent = 5m,
    string? DexId = null,
    string? SpendAssetSymbol = null);

public sealed record DexBuyExecutionResult(
    string TransactionHash,
    bool Confirmed,
    bool BalanceVerified,
    bool HasUnexpectedOutput,
    bool SuspectedPartialFill,
    int TokenDecimals,
    decimal NativeAmountSpent,
    decimal NativeBalanceBefore,
    decimal NativeBalanceAfter,
    decimal ExpectedTokenAmount,
    decimal MinimumTokenAmount,
    decimal ActualTokenAmountReceived,
    decimal TokenBalanceBefore,
    decimal TokenBalanceAfter,
    string Narrative,
    string? DexId = null,
    decimal NetworkFeeNative = 0m,
    long? GasUsed = null,
    string? EffectiveGasPriceWei = null,
    bool ReceiptParsed = false,
    bool DecimalsVerified = false,
    bool SlippageProtected = false,
    bool BalanceSynchronized = false,
    string? SpendAssetSymbol = null,
    decimal SpendAssetAmount = 0m,
    bool UsedQuoteAsset = false);
