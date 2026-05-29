using CryptoAITerminal.Core.Interfaces;

namespace CryptoAITerminal.Gateway.DEX;

public interface IDexTradeGateway : IExchangeGateway
{
    string NetworkName { get; }
    string NativeSymbol { get; }
    string SupportedDexesLabel { get; }
    IReadOnlyList<DexQuoteAssetOption> SupportedQuoteAssets { get; }
    bool SupportsDex(string? dexId);
    Task<DexBuyExecutionResult> ExecuteConfirmedBuyAsync(DexBuyExecutionRequest request);
    Task<DexSellExecutionResult> ExecuteConfirmedSellAsync(DexSellExecutionRequest request);
    Task<decimal> GetTokenPriceInNativeAsync(string tokenAddress, decimal nativeAmount, string? dexId = null);
    Task<string> BuyTokenAsync(string tokenAddress, decimal nativeAmountToSpend, decimal slippagePercent = 5, string? dexId = null, string? spendAssetSymbol = null);
    Task<string> SellTokenAsync(string tokenAddress, decimal tokenAmountToSell, decimal slippagePercent = 5, string? dexId = null, string? receiveAssetSymbol = null);
    Task<int> GetTokenDecimalsAsync(string tokenAddress);
    Task<decimal> GetTokenBalanceAsync(string tokenAddress);
    Task<DexSellabilityProbeResult> ProbeSellabilityAsync(DexSellabilityProbeRequest request);
}
