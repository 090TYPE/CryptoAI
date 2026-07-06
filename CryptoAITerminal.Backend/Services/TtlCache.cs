using System.Collections.Concurrent;

namespace CryptoAITerminal.Backend.Services;

/// <summary>
/// Tiny thread-safe TTL cache. Fronts upstream data calls (e.g. portfolio balances) so 100+
/// clients asking for the same wallet don't each hit the paid API. Time is injectable for tests.
/// </summary>
public sealed class TtlCache
{
    private sealed record Entry(string Value, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _store = new();
    private readonly TimeSpan _ttl;

    public TtlCache(TimeSpan ttl) => _ttl = ttl;

    public bool TryGet(string key, DateTime now, out string value)
    {
        if (_store.TryGetValue(key, out var e) && e.ExpiresAt > now)
        {
            value = e.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public void Set(string key, string value, DateTime now)
        => _store[key] = new Entry(value, now + _ttl);

    public bool TryGet(string key, out string value) => TryGet(key, DateTime.UtcNow, out value);
    public void Set(string key, string value) => Set(key, value, DateTime.UtcNow);
}
