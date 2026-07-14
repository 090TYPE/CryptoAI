using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Deployer reputation per token (Etherscan-family): who deployed the contract, how many
/// tokens they've launched, estimated rugpulls, wallet age. Runs rarely (deployer is fixed).
/// </summary>
public sealed class DeployerCollector : IDataCollector
{
    private readonly TrackedTokenRepository _tracked;
    private readonly DeployerRepository _repo;
    private readonly DeployerWalletAnalyzer _analyzer;
    private readonly ILogger<DeployerCollector> _log;

    public DeployerCollector(TrackedTokenRepository tracked, DeployerRepository repo, DeployerWalletAnalyzer analyzer, ILogger<DeployerCollector> log)
    {
        _tracked = tracked;
        _repo = repo;
        _analyzer = analyzer;
        _log = log;
    }

    public string Name => "deployer";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var active = await _tracked.ListActiveAsync(ct);
        var count = 0;

        foreach (var t in active)
        {
            try
            {
                var r = await _analyzer.AnalyseTokenDeployerAsync(t.TokenAddress, t.Chain, ct);
                if (!r.IsSupported || string.IsNullOrEmpty(r.DeployerAddress)) continue;

                await _repo.UpsertAsync(t.Chain, t.TokenAddress, r.DeployerAddress,
                    r.TotalTokensDeployed, r.EstimatedRugpulls, r.WalletAgeMonths, ct);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "deployer {Token} failed", t.TokenAddress); }
        }

        return count;
    }
}
