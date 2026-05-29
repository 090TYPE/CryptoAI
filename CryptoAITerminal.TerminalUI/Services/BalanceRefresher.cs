using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Interfaces;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Периодически опрашивает <see cref="IExchangeGateway.GetBalanceAsync"/> на
/// каждом подключённом гейтвее по списку ключевых активов и хранит результат
/// в thread-safe кэше. Используется <c>WebApiSnapshotWriter</c> — снимок
/// читает кэш мгновенно, не блокируя UI на сетевые вызовы.
///
/// Интервал по умолчанию — 60 секунд, чтобы не выжигать rate limits бирж.
/// </summary>
public sealed class BalanceRefresher : IDisposable
{
    private static readonly string[] DefaultAssets = ["USDT", "USDC", "BTC", "ETH", "BNB", "SOL"];

    private readonly (string Exchange, string Market, IExchangeGateway Gateway)[] _targets;
    private readonly string[] _assets;
    private readonly Timer _timer;
    private readonly object _cacheLock = new();
    private List<BalanceSnapshot> _cache = new();
    private int _refreshing;

    public BalanceRefresher(
        IEnumerable<(string Exchange, string Market, IExchangeGateway Gateway)> targets,
        IEnumerable<string>? assets = null,
        TimeSpan? interval = null)
    {
        var list = new List<(string, string, IExchangeGateway)>();
        foreach (var t in targets)
            if (t.Gateway is not null) list.Add(t);
        _targets = list.ToArray();

        var assetList = new List<string>();
        foreach (var a in assets ?? DefaultAssets)
            if (!string.IsNullOrWhiteSpace(a))
                assetList.Add(a.ToUpperInvariant());
        _assets = assetList.ToArray();

        var dueTime = TimeSpan.FromSeconds(5);
        var period  = interval ?? TimeSpan.FromSeconds(60);
        _timer = new Timer(_ => _ = RefreshAsync(), null, dueTime, period);
    }

    public IReadOnlyList<BalanceSnapshot> CurrentBalances
    {
        get
        {
            lock (_cacheLock)
                return _cache;
        }
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var next = new List<BalanceSnapshot>(_targets.Length * _assets.Length);
            foreach (var (exchange, market, gw) in _targets)
            {
                foreach (var asset in _assets)
                {
                    decimal amount;
                    try { amount = await gw.GetBalanceAsync(asset); }
                    catch { continue; }

                    if (amount <= 0m) continue;
                    next.Add(new BalanceSnapshot(exchange, market, asset, amount));
                }
            }

            lock (_cacheLock)
                _cache = next;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public void Dispose() => _timer.Dispose();

    public sealed record BalanceSnapshot(string Exchange, string Market, string Asset, decimal Amount);
}
