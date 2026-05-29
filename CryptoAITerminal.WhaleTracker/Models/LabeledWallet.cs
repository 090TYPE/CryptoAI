namespace CryptoAITerminal.WhaleTracker.Models;

public sealed class LabeledWallet
{
    public required string Address  { get; init; }
    public required string Label    { get; init; }
    public required string Category { get; init; }  // "Exchange", "Fund", "MarketMaker", "Whale"
    public required ChainType Chain { get; init; }
}
