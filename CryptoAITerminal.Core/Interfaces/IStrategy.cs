using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Core.Interfaces;

public interface IStrategy
{
    string Name { get; }
    (string Signal, decimal Confidence) Analyze(MarketData data);
    void Reset();
}
