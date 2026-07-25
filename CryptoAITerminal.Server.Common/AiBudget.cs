using System.Collections.Concurrent;

namespace CryptoAITerminal.Server.Common;

/// <summary>
/// Per-licence daily token budget for the server-held AI key. The request rate limiter caps how
/// MANY calls a licence makes; this caps how much they can cost — 120 requests a minute at an
/// unbounded output length is not a budget.
///
/// Charged from the vendor's reported usage after each call, so it tracks real spend.
///
/// Deliberately in memory: the counter resets on restart, which is the wrong trade for billing
/// but the right one for a control that must never add a database round trip to the hot path.
/// Moving the daily total to Postgres (one upsert per call, keyed by licence + UTC day) is the
/// follow-up if a restart-proof cap is needed.
/// </summary>
public sealed class AiBudget
{
    private sealed class Counter
    {
        public DateOnly Day;
        public long Tokens;
    }

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly long _dailyCap;
    private readonly Func<DateTime> _clock;

    public AiBudget(long dailyCap, Func<DateTime>? clock = null)
    {
        _dailyCap = dailyCap;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public long DailyCap => _dailyCap;

    /// <summary>False when this licence has already spent its day. Checked before the upstream call.</summary>
    public bool HasHeadroom(string license)
    {
        if (string.IsNullOrEmpty(license)) return false;
        return Used(license) < _dailyCap;
    }

    /// <summary>Tokens spent by this licence in the current UTC day.</summary>
    public long Used(string license)
    {
        if (!_counters.TryGetValue(license, out var counter)) return 0;
        var today = DateOnly.FromDateTime(_clock());
        lock (counter) return counter.Day == today ? counter.Tokens : 0;
    }

    public long Remaining(string license) => Math.Max(0, _dailyCap - Used(license));

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
