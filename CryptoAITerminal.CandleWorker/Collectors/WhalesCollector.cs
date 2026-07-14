using CryptoAITerminal.Server.Data;
using CryptoAITerminal.WhaleTracker.Models;
using CryptoAITerminal.WhaleTracker.Scanners;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Large ("whale") stablecoin transfers on Ethereum (≥ $1M) via Etherscan. The 'etherscan'
/// key is read from ProviderKeyStore each run (optional — works on the keyless tier too, just
/// rate-limited), so it upgrades automatically once a key is set.
/// </summary>
public sealed class WhalesCollector : IDataCollector
{
    private const decimal MinUsd = 1_000_000m;

    private readonly ProviderKeyStore _keys;
    private readonly WhaleRepository _repo;
    private readonly ILogger<WhalesCollector> _log;

    public WhalesCollector(ProviderKeyStore keys, WhaleRepository repo, ILogger<WhalesCollector> log)
    {
        _keys = keys;
        _repo = repo;
        _log = log;
    }

    public string Name => "whales";
    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var key = await _keys.GetAsync("etherscan", ct); // optional
        var scanner = new EtherscanScanner(ChainType.Ethereum, key);
        try
        {
            var transfers = await scanner.GetLargeStablecoinTransfersAsync(MinUsd, ct);
            var inserted = 0;
            foreach (var w in transfers)
            {
                inserted += await _repo.InsertIfNewAsync(
                    w.TxHash, w.Chain.ToString(), w.TokenSymbol, w.FromAddress, w.ToAddress,
                    w.Amount, w.UsdValue,
                    DateTime.SpecifyKind(w.Timestamp.ToUniversalTime(), DateTimeKind.Utc), ct);
            }
            return inserted;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "whales scan failed"); return 0; }
        finally { (scanner as IDisposable)?.Dispose(); }
    }
}
