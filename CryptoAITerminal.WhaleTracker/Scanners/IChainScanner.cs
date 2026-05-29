using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.WhaleTracker.Models;

namespace CryptoAITerminal.WhaleTracker.Scanners;

public interface IChainScanner
{
    ChainType Chain { get; }

    /// Returns recent stablecoin transfers >= minUsdValue
    Task<IReadOnlyList<WhaleTransfer>> GetLargeStablecoinTransfersAsync(
        decimal minUsdValue, CancellationToken ct = default);

    /// Returns recent activity for the given wallet addresses
    Task<IReadOnlyList<WhaleTransfer>> GetWalletActivityAsync(
        IEnumerable<string> addresses, CancellationToken ct = default);
}
