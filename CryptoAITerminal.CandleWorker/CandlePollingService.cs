using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker;

/// <summary>
/// The 24/7 loop: claim due tokens from <c>tracked_tokens</c>, fetch 1m OHLCV from
/// GeckoTerminal, upsert into <c>dex_candles</c>, refresh <c>token_snapshot</c>, and
/// retire tokens nobody favorites anymore.
/// </summary>
public sealed class CandlePollingService : BackgroundService
{
    private readonly TrackedTokenRepository _tracked;
    private readonly CandleRepository _candles;
    private readonly GeckoTerminalClient _gecko;
    private readonly ILogger<CandlePollingService> _log;
    private readonly int _batch;
    private readonly TimeSpan _tick;
    private readonly PollDemand _demand;

    public CandlePollingService(
        TrackedTokenRepository tracked,
        CandleRepository candles,
        GeckoTerminalClient gecko,
        IConfiguration cfg,
        ILogger<CandlePollingService> log)
    {
        _tracked = tracked;
        _candles = candles;
        _gecko = gecko;
        _log = log;
        _batch = int.TryParse(cfg["POLL_BATCH"], out var b) ? b : 50;
        _tick = TimeSpan.FromSeconds(int.TryParse(cfg["POLL_TICK_SECONDS"], out var t) ? t : 5);

        // Demand tiers: a chart somebody has open keeps full cadence, one nobody has looked
        // at in an hour drops to a slow heartbeat. Tokens with an active price alert are
        // exempt (handled in the claim query).
        _demand = new PollDemand(
            HotWindowS: Cfg(cfg, "POLL_HOT_WINDOW_S", PollDemand.Default.HotWindowS),
            WarmWindowS: Cfg(cfg, "POLL_WARM_WINDOW_S", PollDemand.Default.WarmWindowS),
            WarmIntervalS: Cfg(cfg, "POLL_WARM_INTERVAL_S", PollDemand.Default.WarmIntervalS),
            ColdIntervalS: Cfg(cfg, "POLL_COLD_INTERVAL_S", PollDemand.Default.ColdIntervalS));
    }

    private static int Cfg(IConfiguration cfg, string key, int fallback)
        => int.TryParse(cfg[key], out var v) && v > 0 ? v : fallback;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "CandleWorker started (batch={Batch}, tick={Tick}s, demand hot<{Hot}s warm<{Warm}s → {WarmI}s, cold → {ColdI}s)",
            _batch, _tick.TotalSeconds, _demand.HotWindowS, _demand.WarmWindowS,
            _demand.WarmIntervalS, _demand.ColdIntervalS);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "poll tick failed");
            }

            try { await Task.Delay(_tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass over the currently-due tokens. Public so it can be driven directly in tests.</summary>
    public async Task TickOnceAsync(CancellationToken ct)
    {
        var due = await _tracked.ClaimDueAsync(_batch, _demand, ct);
        foreach (var t in due)
        {
            if (string.IsNullOrEmpty(t.PoolAddress))
            {
                // Pool auto-resolution (DexScreener/GeckoTerminal search) lands in the next increment.
                _log.LogWarning("no pool for {Chain}/{Token} — skipping", t.Chain, t.TokenAddress);
                continue;
            }

            var network = ChainMap.ToGeckoNetwork(t.Chain);
            if (network is null)
            {
                _log.LogWarning("unknown chain '{Chain}' — skipping", t.Chain);
                continue;
            }

            var points = await _gecko.GetPoolOhlcvAsync(network, t.PoolAddress, "minute", aggregate: 1, limit: 60, ct);
            if (points.Count == 0)
            {
                await _tracked.MarkPolledAsync(t.Chain, t.TokenAddress, null, ct);
                continue;
            }

            var rows = points
                .Select(p => new CandleRow(
                    DateTime.SpecifyKind(p.Timestamp.ToUniversalTime(), DateTimeKind.Utc),
                    p.Open, p.High, p.Low, p.Close, p.Volume))
                .ToList();

            var n = await _candles.UpsertCandlesAsync(t.Chain, t.TokenAddress, rows, ct);
            var last = rows[^1];
            await _candles.UpdateSnapshotAsync(t.Chain, t.TokenAddress, last.C, null, null, null, null, null, ct);
            await _tracked.MarkPolledAsync(t.Chain, t.TokenAddress, last.Ts, ct);

            _log.LogInformation("{Chain}/{Token}: {N} candles (last close={Close})",
                t.Chain, t.TokenAddress, n, last.C);
        }

        var orphans = await _tracked.DeactivateOrphansAsync(ct);
        if (orphans > 0) _log.LogInformation("retired {N} orphan tokens", orphans);
    }
}
