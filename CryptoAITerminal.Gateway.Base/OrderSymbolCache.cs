using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.Gateway.Base;

/// <summary>
/// Remembers which symbol an order id belongs to, for exchanges whose cancel endpoint needs both.
/// Seven gateways each carried a bare <c>ConcurrentDictionary&lt;string, string&gt;</c> that only
/// ever dropped an entry on cancel, so filled orders accumulated for the whole session. This one
/// is bounded: entries older than the TTL go first, then the oldest remaining ones, once the map
/// grows past its capacity.
/// </summary>
public sealed class OrderSymbolCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public OrderSymbolCache(int capacity = 512, TimeSpan? ttl = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ttl = ttl ?? TimeSpan.FromHours(6);
    }

    public int Count => _entries.Count;

    public void Remember(string orderId, string symbol)
    {
        if (string.IsNullOrEmpty(orderId)) return;
        _entries[orderId] = new Entry(symbol, DateTime.UtcNow);
        if (_entries.Count > _capacity) Prune();
    }

    /// <summary>Symbol for the id, or <c>false</c> and an empty string when it is not cached.</summary>
    public bool TryGet(string orderId, out string symbol)
    {
        if (!string.IsNullOrEmpty(orderId) && _entries.TryGetValue(orderId, out var entry))
        {
            symbol = entry.Symbol;
            return true;
        }
        symbol = string.Empty;
        return false;
    }

    /// <summary>Drops the entry once the order is terminal (cancelled, filled or rejected).</summary>
    public void Forget(string orderId)
    {
        if (string.IsNullOrEmpty(orderId)) return;
        _entries.TryRemove(orderId, out _);
    }

    public void Clear() => _entries.Clear();

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - _ttl;
        foreach (var pair in _entries)
        {
            if (pair.Value.AddedUtc < cutoff) _entries.TryRemove(pair.Key, out _);
        }

        var excess = _entries.Count - _capacity;
        if (excess <= 0) return;

        // No LRU in ConcurrentDictionary — ToArray() gives a snapshot that is safe to remove from.
        foreach (var pair in _entries.ToArray().OrderBy(p => p.Value.AddedUtc).Take(excess))
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    private readonly record struct Entry(string Symbol, DateTime AddedUtc);
}
