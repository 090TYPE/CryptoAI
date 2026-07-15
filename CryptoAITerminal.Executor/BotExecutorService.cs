using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.Executor;

/// <summary>
/// Runs enabled bots on the server so strategies keep working with the user's PC off. Each tick
/// it loads enabled bot_configs, and for those whose interval has elapsed evaluates the strategy
/// and records the action via <see cref="IBotOrderExecutor"/>. Currently implements DCA
/// (periodic fixed-USD buy); grid/trailing are recognized but left for their own strategy code.
/// </summary>
public sealed class BotExecutorService : BackgroundService
{
    private readonly BotConfigRepository _bots;
    private readonly BotOrdersRepository _orders;
    private readonly AuditRepository _audit;
    private readonly IBotOrderExecutor _executor;
    private readonly ILogger<BotExecutorService> _log;
    private readonly TimeSpan _tick;

    public BotExecutorService(BotConfigRepository bots, BotOrdersRepository orders, AuditRepository audit,
        IBotOrderExecutor executor, IConfiguration cfg, ILogger<BotExecutorService> log)
    {
        _bots = bots; _orders = orders; _audit = audit; _executor = executor; _log = log;
        _tick = TimeSpan.FromSeconds(int.TryParse(cfg["BOT_TICK_SECONDS"], out var t) ? t : 15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("BotExecutor started (tick={Tick}s, executor={Executor})", _tick.TotalSeconds, _executor.GetType().Name);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "bot tick failed"); }
            try { await Task.Delay(_tick, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var bot in await _bots.GetEnabledAsync(ct))
        {
            try
            {
                if (!string.Equals(bot.Strategy, "dca", StringComparison.OrdinalIgnoreCase))
                    continue; // other strategies land as their own evaluators

                var p = ParseParams(bot.ParamsJson);
                if (p is null) continue;

                var (asset, amountUsd, intervalMin) = p.Value;
                if (bot.LastRunUtc is { } last && (DateTime.UtcNow - last).TotalMinutes < intervalMin)
                    continue; // not due yet

                var (status, extRef) = await _executor.PlaceAsync(bot.UserId, "buy", asset, amountUsd, ct);
                await _orders.InsertAsync(bot.Id, bot.UserId, "buy", asset, amountUsd, null, status, extRef, ct);
                await _bots.MarkRunAsync(bot.Id, ct);
                await _audit.WriteAsync(bot.UserId, "bot", "bot_order",
                    JsonSerializer.Serialize(new { bot.Id, strategy = "dca", side = "buy", asset, amountUsd, status, extRef }), null, ct);
                _log.LogInformation("bot {Id} DCA: buy {Amount} {Asset} ({Status} {Ref})", bot.Id, amountUsd, asset, status, extRef);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "bot {Id} failed", bot.Id); }
        }
    }

    /// <summary>DCA params: { "asset": "BTC", "amountUsd": 100, "intervalMinutes": 60 }.</summary>
    private static (string Asset, decimal AmountUsd, double IntervalMin)? ParseParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var asset = root.TryGetProperty("asset", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(asset)) return null;
            var amount = root.TryGetProperty("amountUsd", out var am) && am.TryGetDecimal(out var d) ? d : 0m;
            if (amount <= 0) return null;
            var interval = root.TryGetProperty("intervalMinutes", out var iv) && iv.TryGetDouble(out var i) ? i : 0d;
            return (asset!, amount, interval);
        }
        catch { return null; }
    }
}
