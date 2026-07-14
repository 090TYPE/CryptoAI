using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Runs every registered <see cref="IDataCollector"/> on its own independent cadence and
/// records the outcome in <c>collector_runs</c>. One slow/failing collector never blocks another.
/// </summary>
public sealed class CollectorRunner : BackgroundService
{
    private readonly IEnumerable<IDataCollector> _collectors;
    private readonly CollectorRunsRepository _runs;
    private readonly ILogger<CollectorRunner> _log;

    public CollectorRunner(IEnumerable<IDataCollector> collectors, CollectorRunsRepository runs, ILogger<CollectorRunner> log)
    {
        _collectors = collectors;
        _runs = runs;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var collectors = _collectors.ToList();
        _log.LogInformation("CollectorRunner starting {Count} collectors: {Names}",
            collectors.Count, string.Join(", ", collectors.Select(c => c.Name)));
        return Task.WhenAll(collectors.Select(c => RunLoopAsync(c, stoppingToken)));
    }

    private async Task RunLoopAsync(IDataCollector c, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = await c.CollectAsync(ct);
                await _runs.RecordAsync(c.Name, ok: true, error: null, items: n, ct);
                _log.LogInformation("collector {Name}: {N} items", c.Name, n);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "collector {Name} failed", c.Name);
                try { await _runs.RecordAsync(c.Name, ok: false, error: ex.Message, items: 0, ct); }
                catch { /* recording failure must not kill the loop */ }
            }

            try { await Task.Delay(c.Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
