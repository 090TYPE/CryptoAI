namespace CryptoAITerminal.CandleWorker;

/// <summary>Maps stored chain ids (DexScreener-style or short) to GeckoTerminal network slugs.</summary>
public static class ChainMap
{
    public static string? ToGeckoNetwork(string? chain) => chain?.Trim().ToLowerInvariant() switch
    {
        "eth" or "ethereum"                    => "eth",
        "bsc" or "bnb" or "binance-smart-chain" => "bsc",
        "base"                                 => "base",
        "arbitrum" or "arb"                    => "arbitrum",
        "polygon" or "polygon_pos" or "matic"  => "polygon_pos",
        "avalanche" or "avax"                  => "avax",
        "optimism" or "op"                     => "optimism",
        "solana" or "sol"                      => "solana",
        _ => null
    };

    /// <summary>Maps stored chain ids to DexScreener chain slugs.</summary>
    public static string? ToDexScreener(string? chain) => chain?.Trim().ToLowerInvariant() switch
    {
        "eth" or "ethereum"                     => "ethereum",
        "bsc" or "bnb" or "binance-smart-chain" => "bsc",
        "base"                                  => "base",
        "arbitrum" or "arb"                     => "arbitrum",
        "polygon" or "polygon_pos" or "matic"   => "polygon",
        "avalanche" or "avax"                   => "avalanche",
        "optimism" or "op"                      => "optimism",
        "solana" or "sol"                       => "solana",
        _ => null
    };
}
