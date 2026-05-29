namespace CryptoAITerminal.Gateway.DEX;

public sealed record DexSellabilityProbeRequest(
    string TokenAddress,
    decimal SlippagePercent = 5m,
    string? DexId = null,
    decimal? NativeAmountToProbe = null,
    decimal? QuoteAmountToProbe = null,
    decimal? TokenAmountToSell = null,
    bool PrimeAllowance = false,
    string? QuoteAssetSymbol = null);

public sealed record DexSellabilityProbeResult(
    bool Passed,
    bool IsOnChainSimulation,
    bool UsedOwnedBalance,
    decimal TokenAmountChecked,
    decimal ExpectedNativeOutput,
    decimal? RoundTripLossPercent,
    string Narrative,
    string? QuoteAssetSymbol = null);
