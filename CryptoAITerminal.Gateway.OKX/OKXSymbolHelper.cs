namespace CryptoAITerminal.Gateway.OKX;

/// <summary>
/// Converts between internal Binance-style symbols (BTCUSDT) and OKX-style symbols (BTC-USDT / BTC-USDT-SWAP).
/// </summary>
internal static class OKXSymbolHelper
{
    /// <summary>"BTCUSDT" → "BTC-USDT"</summary>
    internal static string ToSpotSymbol(string s)
    {
        if (s.Contains('-')) return s; // already OKX format
        if (s.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
            return s[..^4] + "-USDT";
        if (s.EndsWith("USDC", StringComparison.OrdinalIgnoreCase))
            return s[..^4] + "-USDC";
        if (s.EndsWith("BTC", StringComparison.OrdinalIgnoreCase))
            return s[..^3] + "-BTC";
        if (s.EndsWith("ETH", StringComparison.OrdinalIgnoreCase))
            return s[..^3] + "-ETH";
        return s;
    }

    /// <summary>"BTCUSDT" → "BTC-USDT-SWAP"</summary>
    internal static string ToSwapSymbol(string s)
    {
        var spot = ToSpotSymbol(s);
        return spot.EndsWith("-SWAP", StringComparison.OrdinalIgnoreCase) ? spot : spot + "-SWAP";
    }

    /// <summary>"BTC-USDT" or "BTC-USDT-SWAP" → "BTCUSDT"</summary>
    internal static string FromOkxSymbol(string s) =>
        s.Replace("-USDT-SWAP", "USDT")
         .Replace("-USDC-SWAP", "USDC")
         .Replace("-BTC-SWAP", "BTC")
         .Replace("-ETH-SWAP", "ETH")
         .Replace("-USDT", "USDT")
         .Replace("-USDC", "USDC")
         .Replace("-BTC", "BTC")
         .Replace("-ETH", "ETH");
}
