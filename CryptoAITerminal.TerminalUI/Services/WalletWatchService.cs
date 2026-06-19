using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Watches external EVM wallets on-chain (via <see cref="WalletCopyTradeMonitor"/>)
/// and raises <see cref="SignalDetected"/> on the UI thread whenever a watched wallet
/// executes a DEX swap. Read-only: never moves funds. One monitor per chain.
/// </summary>
public sealed class WalletWatchService
{
    private static readonly Dictionary<string, string> ChainRpc = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eth"]      = "https://ethereum-rpc.publicnode.com",
        ["bsc"]      = "https://bsc-dataseed.binance.org/",
        ["base"]     = "https://mainnet.base.org",
        ["polygon"]  = "https://polygon-rpc.com",
        ["arbitrum"] = "https://arb1.arbitrum.io/rpc",
    };

    public static IReadOnlyList<string> SupportedChains { get; } = ChainRpc.Keys.ToList();

    public static bool IsChainSupported(string? chain) =>
        !string.IsNullOrWhiteSpace(chain) && ChainRpc.ContainsKey(chain);

    private CancellationTokenSource? _cts;

    /// <summary>Raised on the UI thread for each detected swap of a watched wallet.</summary>
    public event Action<CopyTradeSignal>? SignalDetected;

    public bool IsRunning => _cts is not null;

    public void Start(IEnumerable<(string Address, string Chain)> wallets)
    {
        Stop();

        var byChain = wallets
            .Where(w => !string.IsNullOrWhiteSpace(w.Address) && ChainRpc.ContainsKey(w.Chain))
            .GroupBy(w => w.Chain.ToLowerInvariant())
            .ToList();

        if (byChain.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;

        foreach (var group in byChain)
        {
            var rpc = ChainRpc[group.Key];
            var addresses = group.Select(g => g.Address).ToList();
            var monitor = new WalletCopyTradeMonitor(group.Key, rpc, addresses, Array.Empty<string>());
            var channel = Channel.CreateUnbounded<CopyTradeSignal>();
            _ = Task.Run(() => monitor.RunAsync(channel.Writer, cts.Token));
            _ = Task.Run(() => DrainAsync(channel.Reader, cts.Token));
        }
    }

    private async Task DrainAsync(ChannelReader<CopyTradeSignal> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var signal in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var captured = signal;
                Dispatcher.UIThread.Post(() => SignalDetected?.Invoke(captured));
            }
        }
        catch (OperationCanceledException)
        {
            // normal on stop
        }
        catch
        {
            // swallow — best-effort monitoring
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }
}
