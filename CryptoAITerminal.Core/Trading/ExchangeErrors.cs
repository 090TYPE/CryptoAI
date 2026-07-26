using System;

namespace CryptoAITerminal.Core.Trading;

/// <summary>
/// Exchange error classification shared by every money path. The same substring heuristic used to
/// live as five private copies (TradingBot, GridBot, FundingArbitrageService, AiTraderAgentService,
/// MainWindowViewModel); a copy that missed a new marker turned into one bot silently refusing to
/// trade while the others recovered.
/// </summary>
public static class ExchangeErrors
{
    // Lower-case markers, matched against the lower-cased exception message.
    // Binance: "Order's position side does not match user's setting." (-4061 / -4059)
    // Bybit:   "position idx not match position mode" (10001)
    // OKX:     "Position side mismatch" (51124)
    private static readonly string[] PositionSideMismatchMarkers =
    [
        "position side",
        "position mode",
        "position idx",
        "51124",
        "-4061",
    ];

    /// <summary>
    /// True when the exchange rejected the order because the account is in the other
    /// position mode (one-way vs hedge) — the caller can flip the mode and retry once.
    /// </summary>
    public static bool IsPositionSideMismatch(Exception? ex)
        => IsPositionSideMismatch(ex?.Message);

    /// <summary>Same check against a raw error message.</summary>
    public static bool IsPositionSideMismatch(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        var msg = message.ToLowerInvariant();
        foreach (var marker in PositionSideMismatchMarkers)
        {
            if (msg.Contains(marker, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
