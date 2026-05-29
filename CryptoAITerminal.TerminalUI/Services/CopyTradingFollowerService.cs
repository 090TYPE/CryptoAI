using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Follower side of copy trading: polls the leader's /api/copy-trades endpoint
/// every 2 seconds, mirrors new trades scaled by <see cref="ScaleRatio"/>,
/// and raises events so the ViewModel can log results.
/// </summary>
public sealed class CopyTradingFollowerService : IDisposable
{
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HashSet<string> _executedIds = [];
    private CancellationTokenSource? _cts;

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>Base URL of the leader's WebApi, e.g. "http://192.168.1.10:5180".</summary>
    public string  LeaderUrl        { get; set; } = "";
    /// <summary>Fraction of leader's quantity to execute (0.5 = half size).</summary>
    public decimal ScaleRatio       { get; set; } = 1.0m;
    /// <summary>Minimum performance fee % taken from profitable trades (local tracking only).</summary>
    public decimal PerformanceFeePct { get; set; } = 20m;

    /// <summary>Gateway used to execute copied orders.</summary>
    public IExchangeGateway? Gateway { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<CopyExecution>? ExecutionCompleted;
    public event Action<string>?        LogMessage;

    // ── Accumulated fee estimate ───────────────────────────────────────────────

    public decimal EstimatedFeesEarned { get; private set; }
    public int     TotalCopied         { get; private set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        Log("Follower started — polling leader...");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"Poll error: {ex.Message}"); }

            try { await Task.Delay(2_000, ct); } catch { break; }
        }
        Log("Follower stopped.");
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(LeaderUrl)) return;

        var url  = LeaderUrl.TrimEnd('/') + "/api/copy-trades";
        var json = await _http.GetStringAsync(url, ct);
        var trades = JsonSerializer.Deserialize<List<CopyTrade>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (trades is null || trades.Count == 0) return;

        // Process newest-first, skip already executed
        trades.Sort((a, b) => b.ExecutedUtc.CompareTo(a.ExecutedUtc));
        foreach (var trade in trades)
        {
            if (ct.IsCancellationRequested) break;
            if (_executedIds.Contains(trade.Id)) continue;
            _executedIds.Add(trade.Id);

            await MirrorTradeAsync(trade, ct);
        }
    }

    private async Task MirrorTradeAsync(CopyTrade trade, CancellationToken ct)
    {
        if (Gateway is null)
        {
            Log($"Skip {trade.Symbol} {trade.Side} — no gateway configured");
            return;
        }

        var scaledQty = Math.Round(trade.Quantity * ScaleRatio, 6);
        if (scaledQty <= 0)
        {
            Log($"Skip {trade.Symbol} — scaled qty too small");
            return;
        }

        bool   success = false;
        string error   = "";

        try
        {
            var side = trade.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Buy : OrderSide.Sell;

            await Gateway.PlaceOrderAsync(new Order
            {
                Symbol   = trade.Symbol,
                Side     = side,
                Type     = OrderType.Market,
                Quantity = scaledQty,
            });

            success = true;
            TotalCopied++;

            if (side == OrderSide.Sell)
                EstimatedFeesEarned += scaledQty * trade.Price * (PerformanceFeePct / 100m) * 0.01m;

            Log($"Copied {trade.Side} {scaledQty} {trade.Symbol} @ ~{trade.Price:N4} from {trade.Source}");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log($"Failed to copy {trade.Symbol}: {ex.Message}");
        }

        ExecutionCompleted?.Invoke(new CopyExecution
        {
            LeaderTradeId = trade.Id,
            Symbol        = trade.Symbol,
            Side          = trade.Side,
            Quantity      = scaledQty,
            Price         = trade.Price,
            Success       = success,
            Error         = error,
        });
    }

    private void Log(string msg) => LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public void Dispose() => Stop();
}
