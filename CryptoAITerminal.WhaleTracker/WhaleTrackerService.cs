using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.WhaleTracker.Data;
using CryptoAITerminal.WhaleTracker.Models;
using CryptoAITerminal.WhaleTracker.Scanners;

namespace CryptoAITerminal.WhaleTracker;

public sealed class WhaleTrackerService : IDisposable
{
    private readonly Subject<WhaleAlert> _alertSubject = new();
    private readonly ConcurrentDictionary<string, byte> _seenTxHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<IChainScanner> _scanners;
    private readonly IReadOnlyList<LabeledWallet> _labeledWallets;
    private readonly decimal _minUsdValue;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public IObservable<WhaleAlert> AlertStream => _alertSubject;
    public decimal MinUsdValue => _minUsdValue;
    public IReadOnlyList<LabeledWallet> LabeledWallets => _labeledWallets;

    public WhaleTrackerService(
        decimal minUsdValue = 500_000m,
        string? etherscanApiKey = null,
        string? bscscanApiKey = null)
    {
        _minUsdValue = minUsdValue;
        _labeledWallets = KnownWallets.All;

        _scanners =
        [
            new EtherscanScanner(ChainType.Ethereum, etherscanApiKey),
            new EtherscanScanner(ChainType.BSC, bscscanApiKey),
            new SolscanScanner(),
        ];
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _pollingTask = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _pollingTask = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Initial poll immediately, then every 60 seconds
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* swallow and retry next cycle */ }

            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        foreach (var scanner in _scanners)
        {
            var chainLabels = _labeledWallets
                .Where(w => w.Chain == scanner.Chain)
                .ToList();

            // 1. Large stablecoin transfers
            var largeTx = await scanner.GetLargeStablecoinTransfersAsync(_minUsdValue, ct);
            foreach (var tx in largeTx)
                TryEmit(tx);

            // 2. Labeled wallet activity
            if (chainLabels.Count > 0)
            {
                var addresses = chainLabels.Select(w => w.Address).Distinct();
                var walletTx = await scanner.GetWalletActivityAsync(addresses, ct);
                foreach (var tx in walletTx)
                    TryEmit(tx);
            }
        }
    }

    private void TryEmit(WhaleTransfer transfer)
    {
        // De-duplicate by tx hash
        if (!_seenTxHashes.TryAdd(transfer.TxHash, 0)) return;

        // Keep the seen-set bounded
        if (_seenTxHashes.Count > 10_000)
            _seenTxHashes.Clear();

        var fromLabel = _labeledWallets.FirstOrDefault(w =>
            w.Chain == transfer.Chain &&
            w.Address.Equals(transfer.FromAddress, StringComparison.OrdinalIgnoreCase));

        var toLabel = _labeledWallets.FirstOrDefault(w =>
            w.Chain == transfer.Chain &&
            w.Address.Equals(transfer.ToAddress, StringComparison.OrdinalIgnoreCase));

        var alertType = (fromLabel is not null || toLabel is not null)
            ? "WalletActivity"
            : "LargeTransfer";

        var alert = new WhaleAlert
        {
            Transfer = transfer,
            FromLabel = fromLabel,
            ToLabel = toLabel,
            AlertType = alertType
        };

        _alertSubject.OnNext(alert);
    }

    public void Dispose()
    {
        Stop();
        _alertSubject.Dispose();
        foreach (var s in _scanners.OfType<IDisposable>())
            s.Dispose();
    }
}
