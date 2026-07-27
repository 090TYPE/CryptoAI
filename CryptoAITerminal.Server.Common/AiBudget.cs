using System.Collections.Concurrent;

namespace CryptoAITerminal.Server.Common;

/// <summary>
/// Per-licence daily token budget for the server-held AI key. The request rate limiter caps how
/// MANY calls a licence makes; this caps how much they can cost — 120 requests a minute at an
/// unbounded output length is not a budget.
///
/// Charged from the vendor's reported usage after each call, so it tracks real spend.
///
/// The live counter is in memory so the hot path never waits on a database. Durability comes from
/// outside: a loader seeds today's totals at startup and a flusher writes them back periodically.
/// Until that existed the cap was not a cap but a cap between restarts, and on a box that gets
/// redeployed during the working day the ceiling effectively did not exist.
///
/// The cap itself is passed in per call rather than fixed at construction, because it depends on
/// the licence's tier — a Lite licence and a Max licence share this object and must not share a
/// ceiling.
/// </summary>
public sealed class AiBudget
{
    private sealed class Counter
    {
        public DateOnly Day;
        public long Tokens;
    }

    /// <summary>One licence's spend for one UTC day, for loading and flushing.</summary>
    public readonly record struct Entry(string License, DateOnly Day, long Tokens);

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly long _dailyCap;
    private readonly Func<DateTime> _clock;

    public AiBudget(long dailyCap, Func<DateTime>? clock = null)
    {
        _dailyCap = dailyCap;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public long DailyCap => _dailyCap;

    /// <summary>False when this licence has already spent its day, against the default cap.</summary>
    public bool HasHeadroom(string license) => HasHeadroom(license, _dailyCap);

    /// <summary>
    /// False when this licence has already spent its day against the cap its tier allows.
    /// A non-positive cap is treated as the default rather than as "no allowance" — a
    /// misconfigured tier should degrade to the old behaviour, not lock a paying customer out.
    /// </summary>
    public bool HasHeadroom(string license, long cap)
    {
        if (string.IsNullOrEmpty(license)) return false;
        return Used(license) < (cap > 0 ? cap : _dailyCap);
    }

    /// <summary>Tokens spent by this licence in the current UTC day.</summary>
    public long Used(string license)
    {
        if (!_counters.TryGetValue(license, out var counter)) return 0;
        var today = DateOnly.FromDateTime(_clock());
        lock (counter) return counter.Day == today ? counter.Tokens : 0;
    }

    public long Remaining(string license) => Math.Max(0, _dailyCap - Used(license));

    public long Remaining(string license, long cap) => Math.Max(0, (cap > 0 ? cap : _dailyCap) - Used(license));

    /// <summary>
    /// Restore a total read from storage. Takes the LARGER of the stored and in-memory values:
    /// the loader runs at startup but requests can already be arriving, and a load that overwrote
    /// a fresh charge would hand back allowance that was just spent.
    /// </summary>
    public void Seed(string license, DateOnly day, long tokens)
    {
        if (string.IsNullOrEmpty(license) || tokens <= 0) return;
        var counter = _counters.GetOrAdd(license, _ => new Counter { Day = day });
        lock (counter)
        {
            if (counter.Day != day) { counter.Day = day; counter.Tokens = tokens; }
            else counter.Tokens = Math.Max(counter.Tokens, tokens);
        }
    }

    /// <summary>Every live counter, for the flusher. Cheap: one entry per licence seen today.</summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        var list = new List<Entry>(_counters.Count);
        foreach (var (license, counter) in _counters)
            lock (counter) list.Add(new Entry(license, counter.Day, counter.Tokens));
        return list;
    }

    /// <summary>Charge real usage. A day boundary resets the counter rather than accumulating forever.</summary>
    public void Charge(string license, long tokens)
    {
        if (string.IsNullOrEmpty(license) || tokens <= 0) return;

        var today = DateOnly.FromDateTime(_clock());
        var counter = _counters.GetOrAdd(license, _ => new Counter { Day = today });
        lock (counter)
        {
            if (counter.Day != today)
            {
                counter.Day = today;
                counter.Tokens = 0;
            }
            counter.Tokens += tokens;
        }
    }
}
