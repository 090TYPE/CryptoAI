using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, string> _lastError = new(StringComparer.OrdinalIgnoreCase);
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
            // Assets stay sequential per exchange (one rate-limit bucket), but the exchanges run
            // side by side: a pass costs the time of the slowest venue instead of the sum of all.
            // Gateways without keys are skipped outright — they used to throw once per asset and
            // have every one of those exceptions swallowed.
            var perTarget = new List<Task<List<BalanceSnapshot>>>(_targets.Length);
            foreach (var target in _targets)
            {
                if (!target.Gateway.HasPrivateApiCredentials) continue;
                perTarget.Add(LoadTargetAsync(target));
            }

            var results = await Task.WhenAll(perTarget);

            var next = new List<BalanceSnapshot>(_targets.Length * _assets.Length);
            foreach (var result in results)
                next.AddRange(result);

            lock (_cacheLock)
                _cache = next;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task<List<BalanceSnapshot>> LoadTargetAsync(
        (string Exchange, string Market, IExchangeGateway Gateway) target)
    {
        var found = new List<BalanceSnapshot>(_assets.Length);
        string? firstError = null;

        foreach (var asset in _assets)
        {
            decimal amount;
            try { amount = await target.Gateway.GetBalanceAsync(asset); }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                continue;
            }

            if (amount <= 0m) continue;
            found.Add(new BalanceSnapshot(target.Exchange, target.Market, asset, amount));
        }

        // One line per venue at most, and only when the reason changes — enough to tell
        // "no balance" from "the venue rejected us" after the fact, without a log every minute.
        _lastError.TryGetValue(target.Exchange, out var previous);
        if (firstError is null)
        {
            _lastError.TryRemove(target.Exchange, out _);
        }
        else if (firstError != previous)
        {
            _lastError[target.Exchange] = firstError;
            CrashLog.Write("WARN", $"BalanceRefresher: {target.Exchange} {target.Market} — {firstError}");
        }

        return found;
    }

    public void Dispose() => _timer.Dispose();

    public sealed record BalanceSnapshot(string Exchange, string Market, string Asset, decimal Amount);
}
