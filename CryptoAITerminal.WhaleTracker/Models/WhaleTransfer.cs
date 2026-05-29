using System;

namespace CryptoAITerminal.WhaleTracker.Models;

public sealed class WhaleTransfer
{
    public required string TxHash      { get; init; }
    public required ChainType Chain    { get; init; }
    public required string FromAddress { get; init; }
    public required string ToAddress   { get; init; }
    public required string TokenSymbol { get; init; }
    public required string TokenName   { get; init; }
    public required decimal Amount     { get; init; }   // token units
    public required decimal UsdValue   { get; init; }   // USD equivalent
    public required DateTime Timestamp { get; init; }
    public string ContractAddress      { get; init; } = string.Empty;
}
