using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Concurrent;
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

    // Contains+Add из обычного HashSet неатомарны, а опрос идёт из фонового цикла:
    // TryAdd даёт «первый выигравший исполняет» одной операцией.
    private readonly ConcurrentDictionary<string, byte> _executedIds = new();

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    // Водяной знак старта: всё, что лидер исполнил раньше, зеркалить нельзя.
    private DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>Сделка старше этого возраста уже не отражает цену входа лидера — пропускаем.</summary>
    private static readonly TimeSpan MaxTradeAge = TimeSpan.FromSeconds(60);

    // Шага лота биржа через IExchangeGateway пока не отдаёт: отсекаем хвост количества вниз
    // (округление вверх дало бы объём больше, чем у лидера) и не шлём заведомо мелкие ордера.
    private const decimal QuantityStep   = 0.000001m;
    private const decimal MinNotionalUsd = 5m;

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>Base URL of the leader's WebApi, e.g. "http://192.168.1.10:5180".</summary>
    public string  LeaderUrl        { get; set; } = "";
    /// <summary>Fraction of leader's quantity to execute (0.5 = half size).</summary>
    public decimal ScaleRatio       { get; set; } = 1.0m;
    /// <summary>Minimum performance fee % taken from profitable trades (local tracking only).</summary>
    public decimal PerformanceFeePct { get; set; } = 20m;

    /// <summary>Gateway used to execute copied orders.</summary>
    public IExchangeGateway? Gateway { get; set; }

    /// <summary>
    /// Риск-гейт для каждой зеркалируемой сделки: размер выбирает лидер, а платит follower,
    /// поэтому лимиты по номиналу и дневному убытку считаются здесь, а не на стороне лидера.
    /// </summary>
    public RiskManager.RiskManager Risk { get; } = new();

    /// <summary>
    /// Спрашивает у воркспейса разрешение на живое исполнение; возвращает причину отказа
    /// или null, если можно. По умолчанию блокирует: сервис шлёт настоящие рыночные ордера
    /// и не имеет бумажного режима, поэтому непривязанный экземпляр торговать не должен.
    /// </summary>
    public Func<string, string?> LiveExecutionGuard { get; set; } =
        _ => "Live execution guard is not wired up — refusing to mirror real orders.";

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<CopyExecution>? ExecutionCompleted;
    public event Action<string>?        LogMessage;

    // ── Accumulated fee estimate ───────────────────────────────────────────────

    public decimal EstimatedFeesEarned { get; private set; }
    public int     TotalCopied         { get; private set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_cts is not null) return;
            var cts = new CancellationTokenSource();
            _cts = cts;
            _startedUtc = DateTime.UtcNow;
            _executedIds.Clear();
            // Новый цикл стартует только после выхода предыдущего: Stop() возвращается,
            // не дожидаясь PollLoopAsync, и пара Stop→Start (смена режима в UI)
            // поднимала второй поллер поверх ещё живого первого.
            _loopTask = RunLoopAsync(_loopTask, cts);
        }
    }

    private async Task RunLoopAsync(Task? previous, CancellationTokenSource cts)
    {
        if (previous is not null)
        {
            try { await previous.ConfigureAwait(false); } catch { /* предыдущий цикл уже отлогировал */ }
        }

        try { await PollLoopAsync(cts.Token).ConfigureAwait(false); }
        finally { cts.Dispose(); }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            // Cancel может прийти после того, как цикл уже освободил свой CTS.
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts = null;
        }
    }

    /// <summary>Останавливает опрос и дожидается фактического выхода из цикла.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_lifecycleLock)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts = null;
            loop = _loopTask;
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* цикл уже отлогировал */ }
        }
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

            // Лидер отдаёт всю свою историю (до 100 сделок), поэтому первый опрос без
            // отсечки уходил в биржу сотней рыночных ордеров по текущей цене.
            if (trade.ExecutedUtc < _startedUtc || DateTime.UtcNow - trade.ExecutedUtc > MaxTradeAge)
            {
                _executedIds.TryAdd(trade.Id, 0);
                continue;
            }

            if (!_executedIds.TryAdd(trade.Id, 0)) continue;

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

        if (LiveExecutionGuard($"Copy trading · {trade.Symbol}") is { } blocked)
        {
            Log($"Skip {trade.Symbol} {trade.Side} — {blocked}");
            return;
        }

        var scaledQty = FloorToStep(trade.Quantity * ScaleRatio, QuantityStep);
        if (scaledQty <= 0)
        {
            Log($"Skip {trade.Symbol} — scaled qty too small");
            return;
        }

        var notionalUsd = scaledQty * trade.Price;
        if (trade.Price > 0m && IsUsdQuoted(trade.Symbol) && notionalUsd < MinNotionalUsd)
        {
            Log($"Skip {trade.Symbol} — {notionalUsd:N2} USDT is below the {MinNotionalUsd:N0} USDT exchange minimum");
            return;
        }

        var side = trade.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? OrderSide.Buy : OrderSide.Sell;

        var order = new Order
        {
            Symbol   = trade.Symbol,
            Side     = side,
            Type     = OrderType.Market,
            Quantity = scaledQty,
        };

        // Риск-гейт: без него follower повторял любой размер лидера, включая тот,
        // на который у него нет баланса.
        decimal balanceUsd = 0m;
        try { balanceUsd = await Gateway.GetBalanceAsync("USDT"); }
        catch (Exception ex) { Log($"Balance check failed: {ex.Message}"); }

        var risk = Risk.Evaluate(order, trade.Price, balanceUsd);
        if (!risk.Allowed)
        {
            Log($"Skip {trade.Symbol} — risk gate: {risk.Reason}");
            return;
        }

        bool   success = false;
        string error   = "";

        try
        {
            await Gateway.PlaceOrderAsync(order);

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

    private static decimal FloorToStep(decimal value, decimal step)
        => step <= 0m ? value : Math.Floor(value / step) * step;

    /// <summary>Порог minNotional в долларах применим только к парам с долларовым котируемым активом.</summary>
    private static bool IsUsdQuoted(string symbol) =>
        symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
        || symbol.EndsWith("USDC", StringComparison.OrdinalIgnoreCase)
        || symbol.EndsWith("BUSD", StringComparison.OrdinalIgnoreCase)
        || symbol.EndsWith("FDUSD", StringComparison.OrdinalIgnoreCase);

    private void Log(string msg) => LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public void Dispose() => Stop();
}
