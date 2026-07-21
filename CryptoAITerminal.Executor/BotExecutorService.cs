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
    private readonly IPreTradeReviewer _reviewer;
    private readonly IRiskGate _risk;
    private readonly InboxRepository _inbox;
    private readonly ILogger<BotExecutorService> _log;
    private readonly TimeSpan _tick;

    public BotExecutorService(BotConfigRepository bots, BotOrdersRepository orders, AuditRepository audit,
        IBotOrderExecutor executor, IPreTradeReviewer reviewer, IRiskGate risk, InboxRepository inbox,
        IConfiguration cfg, ILogger<BotExecutorService> log)
    {
        _bots = bots; _orders = orders; _audit = audit; _executor = executor;
        _reviewer = reviewer; _risk = risk; _inbox = inbox; _log = log;
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

                var (asset, quote, amountUsd, intervalMin, exchange, market, mode) = p.Value;
                if (bot.LastRunUtc is { } last && (DateTime.UtcNow - last).TotalMinutes < intervalMin)
                    continue; // not due yet

                // Idempotency: same logical minute → same client order id (see Track 4 §4.5).
                var clientOrderId = $"dca-{bot.Id:N}-{DateTime.UtcNow:yyyyMMddHHmm}";
                var intent = new BotIntent(bot.UserId, bot.Id, exchange, market, mode, "buy", asset, quote, amountUsd, null, clientOrderId);

                // Hard risk gate (per-order cap) before spending on AI review or touching an exchange.
                var (riskOk, riskReason) = _risk.Check(intent);
                if (!riskOk)
                {
                    await _orders.InsertAsync(bot.Id, bot.UserId, "buy", asset, amountUsd, null, "blocked", null, ct);
                    await _bots.MarkRunAsync(bot.Id, ct);
                    await _audit.WriteAsync(bot.UserId, "bot", "bot_trade_blocked",
                        JsonSerializer.Serialize(new { bot.Id, side = "buy", asset, amountUsd, reason = riskReason }), null, ct);
                    await _inbox.InsertAsync(bot.UserId, "bot",
                        $"Risk blocked a bot trade: {asset}", riskReason ?? "per-order cap exceeded", ct);
                    _log.LogInformation("bot {Id} trade BLOCKED by risk gate: {Reason}", bot.Id, riskReason);
                    continue;
                }

                // AI risk check before anything is placed. An explicit reject stops the trade;
                // see AiPreTradeReviewer for what happens when the model is unavailable.
                var (approved, reason) = await _reviewer.ReviewAsync("buy", asset, amountUsd, ct);
                if (!approved)
                {
                    await _orders.InsertAsync(bot.Id, bot.UserId, "buy", asset, amountUsd, null, "blocked", null, ct);
                    await _bots.MarkRunAsync(bot.Id, ct); // respect the interval; don't hammer the model
                    await _audit.WriteAsync(bot.UserId, "bot", "bot_trade_blocked",
                        JsonSerializer.Serialize(new { bot.Id, side = "buy", asset, amountUsd, reason }), null, ct);
                    await _inbox.InsertAsync(bot.UserId, "bot",
                        $"AI blocked a bot trade: {asset}", reason ?? "No reason given", ct);
                    _log.LogInformation("bot {Id} trade BLOCKED by AI review: {Reason}", bot.Id, reason);
                    continue;
                }

                var fill = await _executor.PlaceAsync(intent, ct);
                await _orders.InsertAsync(bot.Id, bot.UserId, "buy", asset, amountUsd, fill.AvgPrice, fill.Status, fill.ExtRef, ct);
                await _bots.MarkRunAsync(bot.Id, ct);
                await _audit.WriteAsync(bot.UserId, "bot", "bot_order",
                    JsonSerializer.Serialize(new { bot.Id, strategy = "dca", side = "buy", asset, amountUsd, fill.Status, fill.ExtRef }), null, ct);
                _log.LogInformation("bot {Id} DCA: buy {Amount} {Asset} ({Status} {Ref})", bot.Id, amountUsd, asset, fill.Status, fill.ExtRef);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "bot {Id} failed", bot.Id); }
        }
    }

    /// <summary>DCA params: { "asset":"BTC", "quote":"USDT", "amountUsd":100, "intervalMinutes":60,
    /// "exchange":"binance", "market":"spot", "mode":"paper" }. Missing fields fall back to safe defaults.</summary>
    private static (string Asset, string Quote, decimal AmountUsd, double IntervalMin, string Exchange, string Market, string Mode)? ParseParams(string? json)
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
            var quote = root.TryGetProperty("quote", out var q) ? q.GetString() : null;
            var exchange = root.TryGetProperty("exchange", out var ex) ? ex.GetString() : null;
            var market = root.TryGetProperty("market", out var mk) ? mk.GetString() : null;
            var mode = root.TryGetProperty("mode", out var mo) ? mo.GetString() : null;
            return (asset!, string.IsNullOrWhiteSpace(quote) ? "USDT" : quote!, amount, interval,
                    string.IsNullOrWhiteSpace(exchange) ? "binance" : exchange!,
                    string.IsNullOrWhiteSpace(market) ? "spot" : market!,
                    string.IsNullOrWhiteSpace(mode) ? "paper" : mode!);
        }
        catch { return null; }
    }
}
