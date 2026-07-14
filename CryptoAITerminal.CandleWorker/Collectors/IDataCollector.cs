namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// A pluggable data source the server collects on a fixed cadence. Add a new source by
/// implementing this and registering it in Program.cs — the runner schedules it automatically.
/// </summary>
public interface IDataCollector
{
    /// <summary>Stable identifier, also the key in collector_runs.</summary>
    string Name { get; }

    /// <summary>How often to run.</summary>
    TimeSpan Interval { get; }

    /// <summary>Do one collection pass; return the number of items processed.</summary>
    Task<int> CollectAsync(CancellationToken ct);
}
