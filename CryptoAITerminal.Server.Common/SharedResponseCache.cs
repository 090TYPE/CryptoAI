using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CryptoAITerminal.Server.Common;

/// <summary>One cached shared payload: the serialized JSON, its ETag, and when it goes stale.</summary>
public sealed record CachedPayload(string Json, string ETag, DateTime ExpiresUtc);

/// <summary>
/// Cache for responses that are IDENTICAL for every licensed user — news, sentiment, gas,
/// on-chain, whales, liquidations, AI digests, token detail, candles. A hundred terminals
/// polling the same endpoint must cost one database read per TTL, not one per request.
///
/// Two properties matter here and neither comes free with a plain TTL dictionary:
///
///  * <b>single-flight</b> — when an entry expires, concurrent callers share ONE factory run
///    instead of stampeding Postgres. Without this, expiry is exactly when load spikes.
///  * <b>ETag</b> — a repeat poller whose copy is still current gets 304 with an empty body,
///    so the common case costs no serialization, no bandwidth and no client-side parse.
///
/// The factory deliberately runs on <see cref="CancellationToken.None"/>: whoever asked first
/// must not be able to cancel the fetch that everyone else is waiting on. Each caller cancels
/// only its own wait.
/// </summary>
public sealed class SharedResponseCache
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public CachedPayload? Payload;
        public Task<CachedPayload>? Refresh;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _evictAfter;

    private int _requests;
    private long _hits;
    private long _misses;
    private long _joins;

    /// <param name="clock">UTC time source; injectable so tests don't sleep.</param>
    /// <param name="evictAfter">How far past expiry an untouched entry is dropped, so per-token
    /// keys can't grow without bound.</param>
    public SharedResponseCache(Func<DateTime>? clock = null, TimeSpan? evictAfter = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _evictAfter = evictAfter ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>Served from a fresh cached entry.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Times the factory actually ran (i.e. real database reads).</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Requests that arrived mid-refresh and reused the in-flight fetch instead of adding load.</summary>
    public long SingleFlightJoins => Interlocked.Read(ref _joins);

    public int Count => _entries.Count;

    /// <summary>
    /// Return the cached payload for <paramref name="key"/>, invoking <paramref name="factory"/>
    /// at most once per <paramref name="ttl"/> however many callers arrive at once.
    /// A factory that throws is not cached — the next caller retries.
    /// </summary>
    public async Task<CachedPayload> GetOrCreateAsync(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken ct = default)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        Task<CachedPayload> refresh;
        TaskCompletionSource<CachedPayload>? starter = null;

        lock (entry.Gate)
        {
            var cached = entry.Payload;
            if (cached is not null && cached.ExpiresUtc > _clock())
            {
                Interlocked.Increment(ref _hits);
                return cached;
            }

            if (entry.Refresh is null)
            {
                Interlocked.Increment(ref _misses);
                starter = new TaskCompletionSource<CachedPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Refresh = starter.Task;
                // Take the local from the TCS, never by re-reading entry.Refresh: a factory that
                // completes synchronously clears that field before we would get to read it back.
                refresh = starter.Task;
            }
            else
            {
                Interlocked.Increment(ref _joins);
                refresh = entry.Refresh;
            }
        }

        // Started outside the lock so a slow factory can't stall callers of the same key
        // before its first await.
        if (starter is not null)
            _ = RunRefreshAsync(entry, ttl, factory, starter);

        MaybeEvict();
        return await refresh.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Drop a key so the next request re-reads it (e.g. after a collector writes).</summary>
    public void Invalidate(string key) => _entries.TryRemove(key, out _);

    /// <summary>
    /// Look at a key without triggering a fetch. Used to split a composite request (a watchlist
    /// of N tokens) into "already shared" and "needs loading", so the misses can go to the
    /// database as ONE batched query instead of N single-row reads.
    /// </summary>
    public bool TryGetFresh(string key, out CachedPayload payload)
    {
        // Counts towards the eviction cadence like GetOrCreateAsync does: the composite endpoints
        // reach the cache only through TryGetFresh/Seed, so without this a per-token key seeded
        // there would never be swept.
        MaybeEvict();

        if (_entries.TryGetValue(key, out var entry))
        {
            lock (entry.Gate)
            {
                var cached = entry.Payload;
                if (cached is not null && cached.ExpiresUtc > _clock())
                {
                    Interlocked.Increment(ref _hits);
                    payload = cached;
                    return true;
                }
            }
        }

        Interlocked.Increment(ref _misses);
        payload = null!;
        return false;
    }

    /// <summary>
    /// Publish a value fetched outside the cache (the batched-miss path). Any refresh already
    /// in flight for this key is left alone — it carries equally fresh data and will simply
    /// overwrite this entry when it lands.
    /// </summary>
    public CachedPayload Seed(string key, TimeSpan ttl, string json)
    {
        var payload = new CachedPayload(json, MakeETag(json), _clock() + ttl);
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry.Gate) entry.Payload = payload;
        MaybeEvict();
        return payload;
    }

    /// <summary>Weak ETag over the payload. Weak because we only promise semantic equality.</summary>
    public static string MakeETag(string json)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"W/\"{Convert.ToHexString(hash, 0, 8)}\"";
    }

    /// <summary>
    /// Floor a timestamp to a <paramref name="size"/> boundary. Used to collapse "the last 24h"
    /// asked at a hundred slightly different instants onto a single cache key.
    /// </summary>
    public static DateTime Bucket(DateTime utc, TimeSpan size)
    {
        if (size <= TimeSpan.Zero) return utc;
        return new DateTime(utc.Ticks - (utc.Ticks % size.Ticks), DateTimeKind.Utc);
    }

    private async Task RunRefreshAsync(
        Entry entry, TimeSpan ttl, Func<CancellationToken, Task<string>> factory,
        TaskCompletionSource<CachedPayload> tcs)
    {
        try
        {
            var json = await factory(CancellationToken.None).ConfigureAwait(false);
            var payload = new CachedPayload(json, MakeETag(json), _clock() + ttl);
            lock (entry.Gate)
            {
                entry.Payload = payload;
                entry.Refresh = null;
            }
            tcs.TrySetResult(payload);
        }
        catch (Exception ex)
        {
            // Failures are never cached: clear the slot so the next caller retries immediately.
            lock (entry.Gate) entry.Refresh = null;
            tcs.TrySetException(ex);
        }
    }

    /// <summary>Opportunistic sweep of long-dead entries. Cheap: runs once every 256 requests.</summary>
    private void MaybeEvict()
    {
        if (Interlocked.Increment(ref _requests) % 256 != 0) return;

        var cutoff = _clock() - _evictAfter;
        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            lock (entry.Gate)
            {
                if (entry.Refresh is null && entry.Payload is not null && entry.Payload.ExpiresUtc < cutoff)
                    _entries.TryRemove(kv.Key, out _);
            }
        }
    }
}
