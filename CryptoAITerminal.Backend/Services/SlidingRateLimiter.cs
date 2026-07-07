using System.Collections.Concurrent;

namespace CryptoAITerminal.Backend.Services;

/// <summary>
/// Per-key sliding-window rate limiter. Each key (a license name / token) gets at most
/// <c>limit</c> requests per <c>window</c>. Thread-safe, in-memory — enough for a single-node
/// backend fronting 100+ desktop clients. Pure timekeeping is injectable for tests.
/// </summary>
public sealed class SlidingRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();

    public SlidingRateLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _window = window;
    }

    /// <summary>Returns true if the request is allowed and records it; false if over the limit.</summary>
    public bool TryAcquire(string key, DateTime now)
    {
        if (string.IsNullOrEmpty(key)) key = "anon";
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            var cutoff = now - _window;
            while (q.Count > 0 && q.Peek() < cutoff)
            {
                q.Dequeue();
            }

            if (q.Count >= _limit)
            {
                return false;
            }

            q.Enqueue(now);
            return true;
        }
    }

    public bool TryAcquire(string key) => TryAcquire(key, DateTime.UtcNow);
}
