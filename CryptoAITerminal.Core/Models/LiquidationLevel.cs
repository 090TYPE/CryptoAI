namespace CryptoAITerminal.Core.Models;

public readonly record struct LiquidationLevel(
    decimal Price,
    decimal LongLiqUsd,   // bulls liquidated here (price dropped to this)
    decimal ShortLiqUsd); // bears liquidated here (price rose to this)
