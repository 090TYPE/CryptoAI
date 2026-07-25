using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// The shared cache is what makes ~100 terminals cost one database read per TTL instead of
/// one per request. The two properties worth pinning down are single-flight (expiry must not
/// become a stampede) and cancellation isolation (the first caller walking away must not kill
/// the fetch everyone else is waiting on).
/// </summary>
public class SharedResponseCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private sealed class Clock
    {
        public DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public async Task Second_call_within_ttl_does_not_hit_the_factory()
    {
        var clock = new Clock();
        var cache = new SharedResponseCache(clock.Get);
        var calls = 0;

        var first = await cache.GetOrCreateAsync("news:50", Ttl, _ => { calls++; return Task.FromResult("[1]"); });
        clock.Advance(TimeSpan.FromSeconds(29));
        var second = await cache.GetOrCreateAsync("news:50", Ttl, _ => { calls++; return Task.FromResult("[2]"); });

        Assert.Equal(1, calls);
        Assert.Equal(first.Json, second.Json);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public async Task Entry_is_refetched_once_the_ttl_expires()
    {
        var clock = new Clock();
        var cache = new SharedResponseCache(clock.Get);

        await cache.GetOrCreateAsync("gas", Ttl, _ => Task.FromResult("v1"));
        clock.Advance(TimeSpan.FromSeconds(31));
        var again = await cache.GetOrCreateAsync("gas", Ttl, _ => Task.FromResult("v2"));

        Assert.Equal("v2", again.Json);
        Assert.Equal(2, cache.Misses);
    }

    [Fact]
    public async Task Hundred_concurrent_callers_cause_exactly_one_factory_run()
    {
        var cache = new SharedResponseCache();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return "payload";
        }

        // All hundred start before the factory is allowed to finish, so they must pile onto
        // the same in-flight fetch — this is the stampede case at expiry.
        var callers = Enumerable.Range(0, 100)
            .Select(_ => cache.GetOrCreateAsync("whales:50", Ttl, Factory))
            .ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal("payload", r.Json));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(99, cache.SingleFlightJoins);
    }

    [Fact]
    public async Task One_caller_cancelling_does_not_cancel_the_shared_fetch()
    {
        var cache = new SharedResponseCache();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new CancellationTokenSource().Token;

        async Task<string> Factory(CancellationToken ct)
        {
            observed = ct;
            await gate.Task;
            return "survived";
        }

        using var cts = new CancellationTokenSource();
        var impatient = cache.GetOrCreateAsync("digests:*:20", Ttl, Factory, cts.Token);
        var patient = cache.GetOrCreateAsync("digests:*:20", Ttl, Factory);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);

        gate.SetResult();
        var result = await patient;

        Assert.Equal("survived", result.Json);
        Assert.False(observed.CanBeCanceled); // the factory never receives a caller's token
    }

    [Fact]
    public async Task Failed_factory_is_not_cached_and_the_next_caller_retries()
    {
        var cache = new SharedResponseCache();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync("sentiment", Ttl, _ =>
            {
                attempts++;
                throw new InvalidOperationException("db down");
            }));

        var recovered = await cache.GetOrCreateAsync("sentiment", Ttl, _ =>
        {
            attempts++;
            return Task.FromResult("ok");
        });

        Assert.Equal(2, attempts);
        Assert.Equal("ok", recovered.Json);
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        var cache = new SharedResponseCache();

        var a = await cache.GetOrCreateAsync("news:50", Ttl, _ => Task.FromResult("fifty"));
        var b = await cache.GetOrCreateAsync("news:200", Ttl, _ => Task.FromResult("two hundred"));

        Assert.Equal("fifty", a.Json);
        Assert.Equal("two hundred", b.Json);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ETag_is_stable_for_identical_payloads_and_differs_otherwise()
    {
        Assert.Equal(SharedResponseCache.MakeETag("{\"a\":1}"), SharedResponseCache.MakeETag("{\"a\":1}"));
        Assert.NotEqual(SharedResponseCache.MakeETag("{\"a\":1}"), SharedResponseCache.MakeETag("{\"a\":2}"));
        Assert.StartsWith("W/\"", SharedResponseCache.MakeETag("x"));
    }

    [Fact]
    public void Bucket_floors_to_the_window_so_near_simultaneous_requests_share_a_key()
    {
        var window = TimeSpan.FromSeconds(20);
        var early = new DateTime(2026, 7, 25, 12, 0, 5, DateTimeKind.Utc);
        var late = new DateTime(2026, 7, 25, 12, 0, 19, DateTimeKind.Utc);
        var next = new DateTime(2026, 7, 25, 12, 0, 21, DateTimeKind.Utc);

        Assert.Equal(SharedResponseCache.Bucket(early, window), SharedResponseCache.Bucket(late, window));
        Assert.NotEqual(SharedResponseCache.Bucket(late, window), SharedResponseCache.Bucket(next, window));
        Assert.Equal(DateTimeKind.Utc, SharedResponseCache.Bucket(early, window).Kind);
    }
}
