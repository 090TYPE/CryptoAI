using System.Text.Json;
using CryptoAITerminal.Core.Models;
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
    private readonly IGridOrderStore _gridStore;
    private readonly IGridGatewayProvider _gridGateways;
    private readonly ITslPositionStore _tslStore;
    private readonly TslPositionsRepository _tslRepo;
    private readonly IPriceSource _price;
    private readonly IQuoteSource _quotes;
    private readonly UserFlagsRepository _flags;
    private readonly ILogger<BotExecutorService> _log;
    private readonly TimeSpan _tick;

    public BotExecutorService(BotConfigRepository bots, BotOrdersRepository orders, AuditRepository audit,
        IBotOrderExecutor executor, IPreTradeReviewer reviewer, IRiskGate risk, InboxRepository inbox,
        IGridOrderStore gridStore, IGridGatewayProvider gridGateways,
        ITslPositionStore tslStore, TslPositionsRepository tslRepo, IPriceSource price, IQuoteSource quotes,
        UserFlagsRepository flags, IConfiguration cfg, ILogger<BotExecutorService> log)
    {
        _bots = bots; _orders = orders; _audit = audit; _executor = executor;
        _reviewer = reviewer; _risk = risk; _inbox = inbox;
        _gridStore = gridStore; _gridGateways = gridGateways;
        _tslStore = tslStore; _tslRepo = tslRepo; _price = price; _quotes = quotes; _flags = flags; _log = log;
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
                if (string.Equals(bot.Strategy, "grid", StringComparison.OrdinalIgnoreCase))
                {
                    await RunGridAsync(bot, ct);
                    continue;
                }
                if (string.Equals(bot.Strategy, "trailing", StringComparison.OrdinalIgnoreCase))
                {
                    await RunTrailingAsync(bot, ct);
                    continue;
                }
                if (!string.Equals(bot.Strategy, "dca", StringComparison.OrdinalIgnoreCase))
                    continue; // other strategies land as their own evaluators

                var p = ParseParams(bot.ParamsJson);
                if (p is null) continue;

                var (asset, quote, amountUsd, intervalMin, exchange, market, mode, route, exchanges) = p.Value;
                if (bot.LastRunUtc is { } last && (DateTime.UtcNow - last).TotalMinutes < intervalMin)
                    continue; // not due yet

                // Best-execution routing (Phase 4.4): pick the cheapest venue among the user's exchanges.
                if (route && exchanges.Length >= 2)
                    exchange = await RouteBuyAsync(bot, asset, quote, exchange, exchanges, ct);

                // Idempotency: same logical minute → same client order id (see Track 4 §4.5).
                var clientOrderId = $"dca-{bot.Id:N}-{DateTime.UtcNow:yyyyMMddHHmm}";
                var intent = new BotIntent(bot.UserId, bot.Id, exchange, market, mode, "buy", asset, quote, amountUsd, null, clientOrderId);

                // Kill-switch: a halted user (or global halt) blocks live orders before anything else.
                var (killed, killReason) = LiveGate.Check(mode, await _flags.IsLiveHaltedAsync(bot.UserId, ct));
                if (killed)
                {
                    await _orders.InsertAsync(bot.Id, bot.UserId, "buy", asset, amountUsd, null, "blocked", null, ct);
                    await _bots.MarkRunAsync(bot.Id, ct);
                    await _audit.WriteAsync(bot.UserId, "bot", "kill_switch",
                        JsonSerializer.Serialize(new { bot.Id, strategy = "dca", asset, reason = killReason }), null, ct);
                    _log.LogWarning("bot {Id} live BLOCKED by kill-switch", bot.Id);
                    continue;
                }

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

                // Idempotency: if this logical tick's order was already recorded (e.g. node restarted
                // between placing and stamping last_run), don't place it again — just re-stamp.
                if (await _orders.ExistsClientOrderIdAsync(clientOrderId, ct))
                {
                    await _bots.MarkRunAsync(bot.Id, ct);
                    _log.LogInformation("bot {Id} DCA: order {Cid} already placed — skipping (idempotent)", bot.Id, clientOrderId);
                    continue;
                }

                var fill = await _executor.PlaceAsync(intent, ct);
                await _orders.InsertAsync(bot.Id, bot.UserId, "buy", asset, amountUsd, fill.AvgPrice, fill.Status, fill.ExtRef, ct, clientOrderId);
                await _bots.MarkRunAsync(bot.Id, ct);
                await _audit.WriteAsync(bot.UserId, "bot", "bot_order",
                    JsonSerializer.Serialize(new { bot.Id, strategy = "dca", side = "buy", asset, amountUsd, fill.Status, fill.ExtRef }), null, ct);
                _log.LogInformation("bot {Id} DCA: buy {Amount} {Asset} ({Status} {Ref})", bot.Id, amountUsd, asset, fill.Status, fill.ExtRef);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "bot {Id} failed", bot.Id); }
        }
    }

    /// <summary>
    /// Grid tick: on the bot's first run place the initial ladder (StartAsync); afterwards poll for
    /// fills and place the opposite order (PollAsync). The gateway is paper (simulated by price cross)
    /// or the user's live trade-only key, per params. Order state lives in grid_orders so it survives
    /// restarts. A per-order risk cap is applied to each grid cell's notional.
    /// </summary>
    private async Task RunGridAsync(EnabledBot bot, CancellationToken ct)
    {
        var g = ParseGridParams(bot.ParamsJson);
        if (g is null) return;
        var (symbol, lower, upper, levels, qty, exchange, market, mode, pollMin) = g.Value;

        var first = bot.LastRunUtc is null;
        if (!first && bot.LastRunUtc is { } last && (DateTime.UtcNow - last).TotalMinutes < pollMin)
            return; // not due yet

        // Kill-switch: halt live grid placement (paper unaffected).
        var (killed, killReason) = LiveGate.Check(mode, await _flags.IsLiveHaltedAsync(bot.UserId, ct));
        if (killed)
        {
            await _bots.MarkRunAsync(bot.Id, ct);
            await _audit.WriteAsync(bot.UserId, "bot", "kill_switch",
                JsonSerializer.Serialize(new { bot.Id, strategy = "grid", symbol, reason = killReason }), null, ct);
            _log.LogWarning("grid bot {Id} live BLOCKED by kill-switch", bot.Id);
            return;
        }

        var price = await _price.GetPriceAsync(exchange, symbol, ct);
        if (price <= 0) { _log.LogWarning("grid bot {Id}: no price for {Symbol}", bot.Id, symbol); return; }

        // Guard each grid cell's notional with the same per-order cap as DCA.
        var cellIntent = new BotIntent(bot.UserId, bot.Id, exchange, market, mode, "buy",
            symbol, "", qty * price, qty, null);
        var (riskOk, riskReason) = _risk.Check(cellIntent);
        if (!riskOk)
        {
            await _bots.MarkRunAsync(bot.Id, ct);
            await _audit.WriteAsync(bot.UserId, "bot", "bot_trade_blocked",
                JsonSerializer.Serialize(new { bot.Id, strategy = "grid", symbol, cellNotional = qty * price, reason = riskReason }), null, ct);
            await _inbox.InsertAsync(bot.UserId, "bot", $"Risk blocked a grid bot: {symbol}", riskReason ?? "per-order cap exceeded", ct);
            _log.LogInformation("grid bot {Id} BLOCKED by risk gate: {Reason}", bot.Id, riskReason);
            return;
        }

        var open = await _gridStore.GetOpenAsync(bot.Id, ct);
        var gateway = await _gridGateways.CreateAsync(bot.UserId, exchange, market, mode, price, open, ct);
        var runner = new GridBotRunner(gateway, _gridStore);
        var cfg = new GridConfig(bot.Id, bot.UserId, symbol, lower, upper, levels, qty, string.Equals(market, "futures", StringComparison.OrdinalIgnoreCase));

        if (first)
        {
            await runner.StartAsync(cfg, price, ct);
            await _audit.WriteAsync(bot.UserId, "bot", "bot_order",
                JsonSerializer.Serialize(new { bot.Id, strategy = "grid", action = "start", symbol, lower, upper, levels, price, mode }), null, ct);
            _log.LogInformation("grid bot {Id} started: {Symbol} [{Lower}-{Upper}]/{Levels} @ {Price} ({Mode})", bot.Id, symbol, lower, upper, levels, price, mode);
        }
        else
        {
            await runner.PollAsync(cfg, ct);
            _log.LogInformation("grid bot {Id} polled: {Symbol} @ {Price}", bot.Id, symbol, price);
        }
        await _bots.MarkRunAsync(bot.Id, ct);
    }

    /// <summary>Grid params: { "symbol":"BTCUSDT" | "asset":"BTC","quote":"USDT", "lower":100, "upper":200,
    /// "gridLevels":10, "qtyPerGrid":0.01, "exchange":"binance", "market":"spot", "mode":"paper",
    /// "pollMinutes":1 }. Missing fields fall back to safe defaults; returns null if unusable.</summary>
    private static (string Symbol, decimal Lower, decimal Upper, int Levels, decimal Qty,
        string Exchange, string Market, string Mode, double PollMin)? ParseGridParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;

            string? symbol = r.TryGetProperty("symbol", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(symbol))
            {
                var asset = r.TryGetProperty("asset", out var a) ? a.GetString() : null;
                var quote = r.TryGetProperty("quote", out var q) ? q.GetString() : "USDT";
                if (string.IsNullOrWhiteSpace(asset)) return null;
                symbol = asset + (string.IsNullOrWhiteSpace(quote) ? "USDT" : quote);
            }

            var lower = r.TryGetProperty("lower", out var lo) && lo.TryGetDecimal(out var l) ? l : 0m;
            var upper = r.TryGetProperty("upper", out var up) && up.TryGetDecimal(out var u) ? u : 0m;
            var levels = r.TryGetProperty("gridLevels", out var gl) && gl.TryGetInt32(out var n) ? n : 0;
            var qty = r.TryGetProperty("qtyPerGrid", out var qp) && qp.TryGetDecimal(out var qd) ? qd : 0m;
            if (upper <= lower || levels <= 0 || qty <= 0) return null;

            var exchange = r.TryGetProperty("exchange", out var ex) ? ex.GetString() : null;
            var market = r.TryGetProperty("market", out var mk) ? mk.GetString() : null;
            var mode = r.TryGetProperty("mode", out var mo) ? mo.GetString() : null;
            var pollMin = r.TryGetProperty("pollMinutes", out var pm) && pm.TryGetDouble(out var pmd) ? pmd : 0d;

            return (symbol!, lower, upper, levels, qty,
                    string.IsNullOrWhiteSpace(exchange) ? "binance" : exchange!,
                    string.IsNullOrWhiteSpace(market) ? "spot" : market!,
                    string.IsNullOrWhiteSpace(mode) ? "paper" : mode!, pollMin);
        }
        catch { return null; }
    }

    /// <summary>
    /// Trailing tick: on the bot's first run it seeds the managed position from params (the user
    /// already holds it — the bot only manages the exit); afterwards it polls the price and lets
    /// <see cref="TrailingBotRunner"/> close (full/partial) or ratchet the stop. State lives in
    /// tsl_positions, so a restart resumes exactly where it left off.
    /// </summary>
    private async Task RunTrailingAsync(EnabledBot bot, CancellationToken ct)
    {
        var t = ParseTrailingParams(bot.ParamsJson);
        if (t is null) return;
        var (symbol, isLong, entry, qty, futures, exchange, market, mode, pollMin, tsl) = t.Value;

        if (bot.LastRunUtc is null)
        {
            // Seed the position with real user_id (SaveAsync later only updates the row).
            var s = ServerTrailingStop.Init(isLong, entry, tsl) with { RemainingQty = qty };
            await _tslRepo.UpsertAsync(new TslPositionRow(
                bot.Id, bot.UserId, symbol, isLong, entry, futures,
                s.Peak, s.Sl, s.RemainingQty, s.TpPercent, s.PartialDone, Closed: false), ct);
            await _bots.MarkRunAsync(bot.Id, ct);
            await _audit.WriteAsync(bot.UserId, "bot", "bot_order",
                JsonSerializer.Serialize(new { bot.Id, strategy = "trailing", action = "attach", symbol, side = isLong ? "long" : "short", entry, qty, mode }), null, ct);
            _log.LogInformation("trailing bot {Id} attached: {Side} {Qty} {Symbol} @ {Entry} ({Mode})", bot.Id, isLong ? "long" : "short", qty, symbol, entry, mode);
            return;
        }

        if (bot.LastRunUtc is { } last && (DateTime.UtcNow - last).TotalMinutes < pollMin)
            return; // not due yet

        var pos = await _tslStore.LoadAsync(bot.Id, ct);
        if (pos is null || pos.Closed) { await _bots.MarkRunAsync(bot.Id, ct); return; }

        var price = await _price.GetPriceAsync(exchange, symbol, ct);
        if (price <= 0) { _log.LogWarning("trailing bot {Id}: no price for {Symbol}", bot.Id, symbol); return; }

        var gateway = await _gridGateways.CreateAsync(bot.UserId, exchange, market, mode, price, Array.Empty<TrackedGridOrder>(), ct);
        var runner = new TrailingBotRunner(gateway, _tslStore);
        await runner.PollAsync(bot.Id, tsl, price, ct);
        await _bots.MarkRunAsync(bot.Id, ct);

        var after = await _tslStore.LoadAsync(bot.Id, ct);
        if (after is { Closed: true })
        {
            await _audit.WriteAsync(bot.UserId, "bot", "bot_order",
                JsonSerializer.Serialize(new { bot.Id, strategy = "trailing", action = "closed", symbol, price }), null, ct);
            await _inbox.InsertAsync(bot.UserId, "bot", $"Trailing bot closed {symbol}", $"exit around {price}", ct);
            _log.LogInformation("trailing bot {Id} closed {Symbol} @ {Price}", bot.Id, symbol, price);
        }
    }

    /// <summary>Trailing params: { "symbol":"BTCUSDT" (or "asset"+"quote"), "side":"long"|"short",
    /// "entry":100, "qty":0.5, "futures":false, "exchange":"binance", "market":"spot", "mode":"paper",
    /// "pollMinutes":1, plus TpSlConfig fields: tpEnabled, tpPercent, slEnabled, slPercent, trailing,
    /// partialTp, partialTpClosePercent, partialTp2Percent }. Returns null if unusable.</summary>
    private static (string Symbol, bool IsLong, decimal Entry, decimal Qty, bool Futures,
        string Exchange, string Market, string Mode, double PollMin, TpSlConfig Tsl)? ParseTrailingParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;

            string? symbol = r.TryGetProperty("symbol", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(symbol))
            {
                var asset = r.TryGetProperty("asset", out var a) ? a.GetString() : null;
                var quote = r.TryGetProperty("quote", out var q) ? q.GetString() : "USDT";
                if (string.IsNullOrWhiteSpace(asset)) return null;
                symbol = asset + (string.IsNullOrWhiteSpace(quote) ? "USDT" : quote);
            }

            var entry = r.TryGetProperty("entry", out var e) && e.TryGetDecimal(out var ed) ? ed : 0m;
            var qty = r.TryGetProperty("qty", out var qp) && qp.TryGetDecimal(out var qd) ? qd : 0m;
            if (entry <= 0 || qty <= 0) return null;

            var side = r.TryGetProperty("side", out var sd) ? sd.GetString() : "long";
            var isLong = !string.Equals(side, "short", StringComparison.OrdinalIgnoreCase);
            var futures = r.TryGetProperty("futures", out var fu) && fu.ValueKind == JsonValueKind.True;
            var market = r.TryGetProperty("market", out var mk) ? mk.GetString() : null;
            if (string.IsNullOrWhiteSpace(market)) market = futures ? "futures" : "spot";
            var exchange = r.TryGetProperty("exchange", out var ex) ? ex.GetString() : null;
            var mode = r.TryGetProperty("mode", out var mo) ? mo.GetString() : null;
            var pollMin = r.TryGetProperty("pollMinutes", out var pm) && pm.TryGetDouble(out var pmd) ? pmd : 0d;

            var tsl = new TpSlConfig
            {
                TpEnabled = Bool(r, "tpEnabled"),
                TpPercent = Dec(r, "tpPercent", 2m),
                SlEnabled = Bool(r, "slEnabled"),
                SlPercent = Dec(r, "slPercent", 1m),
                TrailingStop = Bool(r, "trailing"),
                PartialTp = Bool(r, "partialTp"),
                PartialTpClosePercent = Dec(r, "partialTpClosePercent", 50m),
                PartialTp2Percent = Dec(r, "partialTp2Percent", 4m),
            };

            return (symbol!, isLong, entry, qty, futures,
                    string.IsNullOrWhiteSpace(exchange) ? "binance" : exchange!, market!,
                    string.IsNullOrWhiteSpace(mode) ? "paper" : mode!, pollMin, tsl);
        }
        catch { return null; }
    }

    private static bool Bool(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static decimal Dec(JsonElement r, string name, decimal fallback) => r.TryGetProperty(name, out var v) && v.TryGetDecimal(out var d) ? d : fallback;

    /// <summary>
    /// Compare the user's exchanges on effective (fee-adjusted) price and return the cheapest to buy.
    /// Public quotes only; venues that don't answer are skipped. Falls back to the default exchange
    /// when fewer than two venues quote. The choice (and saving vs the worst venue) is audited.
    /// </summary>
    private async Task<string> RouteBuyAsync(EnabledBot bot, string asset, string quote, string fallback, string[] exchanges, CancellationToken ct)
    {
        var quotes = new List<ServerBestExecution.ExchangeQuote>(exchanges.Length);
        foreach (var ex in exchanges)
        {
            var px = await _quotes.GetQuoteAsync(ex, asset, quote, ct);
            if (px is > 0m) quotes.Add(new ServerBestExecution.ExchangeQuote(ex, px.Value, 0.1m));
        }

        var choice = ServerBestExecution.PickBest(Core.Enums.OrderSide.Buy, quotes);
        if (choice is null) return fallback;

        await _audit.WriteAsync(bot.UserId, "bot", "bot_route",
            JsonSerializer.Serialize(new
            {
                bot.Id, asset, quote, chosen = choice.Value.Best.Exchange,
                savingsPerUnit = choice.Value.SavingsPerUnit,
                venues = quotes.Select(q => new { q.Exchange, q.RawPrice }),
            }), null, ct);
        _log.LogInformation("bot {Id} routed {Asset}{Quote} → {Exchange} (save {Save}/unit)",
            bot.Id, asset, quote, choice.Value.Best.Exchange, choice.Value.SavingsPerUnit);
        return choice.Value.Best.Exchange;
    }

    /// <summary>DCA params: { "asset":"BTC", "quote":"USDT", "amountUsd":100, "intervalMinutes":60,
    /// "exchange":"binance", "market":"spot", "mode":"paper", "route":true, "exchanges":["binance","bybit"] }.
    /// Missing fields fall back to safe defaults; route/exchanges enable best-execution (Phase 4.4).</summary>
    private static (string Asset, string Quote, decimal AmountUsd, double IntervalMin, string Exchange,
        string Market, string Mode, bool Route, string[] Exchanges)? ParseParams(string? json)
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

            var route = root.TryGetProperty("route", out var rt) && rt.ValueKind == JsonValueKind.True;
            var exchanges = root.TryGetProperty("exchanges", out var exs) && exs.ValueKind == JsonValueKind.Array
                ? exs.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray()
                : Array.Empty<string>();

            return (asset!, string.IsNullOrWhiteSpace(quote) ? "USDT" : quote!, amount, interval,
                    string.IsNullOrWhiteSpace(exchange) ? "binance" : exchange!,
                    string.IsNullOrWhiteSpace(market) ? "spot" : market!,
                    string.IsNullOrWhiteSpace(mode) ? "paper" : mode!, route, exchanges);
        }
        catch { return null; }
    }
}
