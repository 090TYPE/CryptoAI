namespace CryptoAITerminal.Gateway.DEX;

public sealed record DexQuoteAssetOption(
    string Symbol,
    string DisplayName,
    bool IsNative = false,
    string? ContractAddress = null);

public static class DexQuoteAssetCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<DexQuoteAssetOption>> OptionsByNetwork =
        new Dictionary<string, IReadOnlyList<DexQuoteAssetOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["BSC"] =
            [
                new DexQuoteAssetOption("BNB", "BNB (native)", true),
                new DexQuoteAssetOption("USDT", "USDT", ContractAddress: "0x55d398326f99059fF775485246999027B3197955"),
                new DexQuoteAssetOption("USDC", "USDC", ContractAddress: "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d")
            ],
            ["Ethereum"] =
            [
                new DexQuoteAssetOption("ETH", "ETH (native)", true),
                new DexQuoteAssetOption("USDT", "USDT", ContractAddress: "0xdAC17F958D2ee523a2206206994597C13D831ec7"),
                new DexQuoteAssetOption("USDC", "USDC", ContractAddress: "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48")
            ],
            ["Base"] =
            [
                new DexQuoteAssetOption("ETH", "ETH (native)", true),
                new DexQuoteAssetOption("USDC", "USDC", ContractAddress: "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913")
            ],
            ["Solana"] =
            [
                new DexQuoteAssetOption("SOL", "SOL (native)", true),
                new DexQuoteAssetOption("USDT", "USDT", ContractAddress: "Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB"),
                new DexQuoteAssetOption("USDC", "USDC", ContractAddress: "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v")
            ],
            ["Tron"] =
            [
                new DexQuoteAssetOption("TRX", "TRX (native)", true),
                new DexQuoteAssetOption("USDT", "USDT", ContractAddress: TronTradeGateway.DefaultUsdtContractAddress),
                new DexQuoteAssetOption("USDC", "USDC", ContractAddress: TronTradeGateway.DefaultUsdcContractAddress)
            ],
            ["Polygon"] =
            [
                new DexQuoteAssetOption("POL", "POL (native)", true),
                new DexQuoteAssetOption("USDT", "USDT", ContractAddress: "0xc2132D05D31c914a87C6611C10748AEb04B58e8F"),
                new DexQuoteAssetOption("USDC", "USDC", ContractAddress: "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174"),
                new DexQuoteAssetOption("WETH", "WETH", ContractAddress: "0x7ceB23fD6bC0adD59E62ac25578270cFf1b9f619")
            ],
            ["Arbitrum"] =
            [
                new DexQuoteAssetOption("ETH", "ETH (native)", true),
                new DexQuoteAssetOption("USDT", "USDT", ContractAddress: "0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9"),
                new DexQuoteAssetOption("USDC", "USDC", ContractAddress: "0xFF970A61A04b1cA14834A43f5dE4533eBDDB5CC8"),
                new DexQuoteAssetOption("WBTC", "WBTC", ContractAddress: "0x2f2a2543B76A4166549F7aaB2e75Bef0aefC5B0f")
            ]
        };

    public static IReadOnlyList<DexQuoteAssetOption> GetOptions(string networkName) =>
        OptionsByNetwork.TryGetValue(networkName, out var options) ? options : [];

    public static DexQuoteAssetOption? Find(string networkName, string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        return GetOptions(networkName)
            .FirstOrDefault(option => string.Equals(option.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
    }
}
