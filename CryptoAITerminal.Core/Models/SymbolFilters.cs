namespace CryptoAITerminal.Core.Models;

/// <summary>
/// The trading rules a venue enforces for one symbol. Until these were fetched the app guessed:
/// the grid bot derived its tick size from however many decimals the user happened to type into
/// the bounds, used a flat 1e-8 lot step and a hardcoded 5 USDT notional floor, so
/// <c>(Upper - Lower) / GridLevels</c> produced prices like 1428.571428571428571428571429 and the
/// venue answered -1013.
///
/// Zero in any field means "the venue does not publish that rule" — a caller must read zero as
/// "no constraint" and leave the value alone, never round it to zero.
/// </summary>
/// <param name="Symbol">Symbol in the terminal's own form (BTCUSDT), not the venue's.</param>
/// <param name="TickSize">Smallest price increment, in the quote asset.</param>
/// <param name="StepSize">Smallest quantity increment, in the BASE asset.</param>
/// <param name="MinQuantity">Smallest order quantity, in the BASE asset.</param>
/// <param name="MinNotional">Smallest order value (price × quantity), in the quote asset.</param>
public sealed record SymbolFilters(
    string  Symbol,
    decimal TickSize,
    decimal StepSize,
    decimal MinQuantity,
    decimal MinNotional)
{
    /// <summary>
    /// Builds the record from a venue's raw values. Every SDK models these as its own mix of
    /// <c>decimal</c> and <c>decimal?</c>, so take them all as nullable and normalise here:
    /// missing, zero or negative all collapse to 0 = "no constraint".
    /// </summary>
    public static SymbolFilters Create(
        string symbol,
        decimal? tickSize,
        decimal? stepSize,
        decimal? minQuantity,
        decimal? minNotional)
        => new(symbol,
               NonNegative(tickSize),
               NonNegative(stepSize),
               NonNegative(minQuantity),
               NonNegative(minNotional));

    /// <summary>True when the venue published nothing usable — the caller may as well skip rounding.</summary>
    public bool IsEmpty => TickSize <= 0m && StepSize <= 0m && MinQuantity <= 0m && MinNotional <= 0m;

    private static decimal NonNegative(decimal? value)
    {
        var v = value ?? 0m;
        return v > 0m ? v : 0m;
    }
}
