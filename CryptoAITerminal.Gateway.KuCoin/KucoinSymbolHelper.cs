using System;

namespace CryptoAITerminal.Gateway.KuCoin;

/// <summary>
/// Преобразует символы между терминальной формой (BTCUSDT) и форматом KuCoin (BTC-USDT
/// для спота, XBTUSDTM для perpetual фьючерсов — KuCoin использует XBT вместо BTC и
/// суффикс M для perpetual contract).
/// </summary>
public static class KucoinSymbolHelper
{
    private static readonly string[] QuoteAssets =
    [
        // Длинные сначала — иначе USDT матчнется первой как USDT внутри USDTT.
        "USDT", "USDC", "BUSD", "TUSD", "DAI",
        "BTC", "ETH", "BNB", "TRX", "EUR", "USD",
    ];

    public static string ToSpotSymbol(string terminalSymbol)
    {
        if (string.IsNullOrWhiteSpace(terminalSymbol)) return string.Empty;
        if (terminalSymbol.Contains('-')) return terminalSymbol.ToUpperInvariant();

        var upper = terminalSymbol.ToUpperInvariant();
        foreach (var q in QuoteAssets)
        {
            if (upper.EndsWith(q, StringComparison.Ordinal) && upper.Length > q.Length)
            {
                var baseAsset = upper[..^q.Length];
                return $"{baseAsset}-{q}";
            }
        }
        return upper;
    }

    public static string ToFuturesSymbol(string terminalSymbol)
    {
        if (string.IsNullOrWhiteSpace(terminalSymbol)) return string.Empty;
        var upper = terminalSymbol.ToUpperInvariant().Replace("-", "");

        // BTCUSDT → XBTUSDTM (perpetual)
        if (upper.StartsWith("BTC", StringComparison.Ordinal))
            upper = "XBT" + upper[3..];

        return upper.EndsWith("M", StringComparison.Ordinal) ? upper : upper + "M";
    }

    public static string FromKucoinSymbol(string kucoinSymbol)
    {
        if (string.IsNullOrWhiteSpace(kucoinSymbol)) return string.Empty;
        var upper = kucoinSymbol.ToUpperInvariant().Replace("-", "");

        // XBTUSDTM → BTCUSDT
        if (upper.EndsWith("M", StringComparison.Ordinal) && upper.Length > 1)
            upper = upper[..^1];
        if (upper.StartsWith("XBT", StringComparison.Ordinal))
            upper = "BTC" + upper[3..];

        return upper;
    }
}
