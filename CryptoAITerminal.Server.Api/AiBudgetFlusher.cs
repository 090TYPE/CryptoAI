using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.Server.Api;

/// <summary>
/// Makes the daily AI budget survive a restart.
///
/// Loads today's totals once at startup, then writes the live counters back on an interval and
/// once more on shutdown. The hot path is untouched: charging a call still only increments memory.
///
/// The interval is the exposure window. A crash loses at most that much accounting, which is the
/// right trade against an upsert per AI call — and the failure is bounded and known rather than
/// "everything since the last deploy", which is what the memory-only counter gave.
/// </summary>
public sealed class AiBudgetFlusher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly AiBudget _budget;
    private readonly AiBudgetRepository _repo;
    private readonly ILogger<AiBudgetFlusher> _log;

    public AiBudgetFlusher(AiBudget budget, AiBudgetRepository repo, ILogger<AiBudgetFlusher> log)
    {
        _budget = budget; _repo = repo; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await LoadAsync(ct);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await FlushAsync(ct);
        }
        catch (OperationCanceledException) { /* shutting down */ }

        // Shutdown flush. CancellationToken.None on purpose: ct is already cancelled by the time we
        // get here, and passing it would abandon exactly the write this exists for.
        await FlushAsync(CancellationToken.None);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var rows = await _repo.LoadAsync(today, ct);
            foreach (var r in rows) _budget.Seed(r.License, r.Day, r.Tokens);
            if (rows.Count > 0) _log.LogInformation("ai budget: restored {Count} licence counters for {Day}", rows.Count, today);
        }
        catch (Exception ex)
        {
            // Starting with empty counters is wrong but survivable — it hands back one day of
            // allowance. Refusing to start because a reporting table is unreadable is worse.
            _log.LogWarning(ex, "ai budget: could not restore counters, starting from zero");
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            await _repo.FlushAsync(_budget.Snapshot(), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ai budget: flush failed, counters stay in memory");
        }
    }
}
