using CryptoAITerminal.WebApi.Models;
using CryptoAITerminal.WebApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SharedStateService>(_ => new SharedStateService());
builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();
app.UseCors();

// ────────────────────────────────────────────────────────────────────────────
//  Optional bearer-token auth. Включается через env var CRYPTOAI_WEBAPI_TOKEN.
//  Если переменная не задана — API открыт (режим локального мониторинга).
//  Это middleware пропускает /api/health без проверки, чтобы внешние ping-и
//  работали без раскрытия токена.
// ────────────────────────────────────────────────────────────────────────────

var authToken = Environment.GetEnvironmentVariable("CRYPTOAI_WEBAPI_TOKEN");

app.Use(async (ctx, next) =>
{
    if (string.IsNullOrWhiteSpace(authToken))
    {
        await next();
        return;
    }

    var path = ctx.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    if (!ctx.Request.Headers.TryGetValue("Authorization", out var header))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "missing Authorization header" });
        return;
    }

    var raw = header.ToString();
    const string prefix = "Bearer ";
    if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(raw[prefix.Length..].Trim(), authToken, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "invalid bearer token" });
        return;
    }

    await next();
});

// ────────────────────────────────────────────────────────────────────────────
//  Health (без авторизации)
// ────────────────────────────────────────────────────────────────────────────

app.MapGet("/api/health", (SharedStateService state) =>
{
    var snap = state.ReadSnapshot();
    var ageSeconds = snap.UpdatedUtc == DateTime.MinValue
        ? -1
        : (DateTime.UtcNow - snap.UpdatedUtc).TotalSeconds;
    return Results.Ok(new
    {
        status      = "ok",
        updatedUtc  = snap.UpdatedUtc,
        ageSeconds,
        snapshotPath = state.SnapshotPath,
        queueDir     = state.QueueDir,
        authRequired = !string.IsNullOrWhiteSpace(authToken),
    });
});

// ────────────────────────────────────────────────────────────────────────────
//  Read-only views
// ────────────────────────────────────────────────────────────────────────────

app.MapGet("/api/positions", (SharedStateService state) =>
    Results.Ok(state.ReadSnapshot().Positions));

app.MapGet("/api/sniper/candidates", (SharedStateService state) =>
    Results.Ok(state.ReadSnapshot().Candidates));

app.MapGet("/api/pnl", (SharedStateService state) =>
    Results.Ok(state.ReadSnapshot().Pnl));

app.MapGet("/api/snapshot", (SharedStateService state) =>
    Results.Ok(state.ReadSnapshot()));

app.MapGet("/api/balances", (SharedStateService state) =>
    Results.Ok(state.ReadSnapshot().Balances));

// ────────────────────────────────────────────────────────────────────────────
//  Copy Trading — leader publishes executed trades; followers poll this.
// ────────────────────────────────────────────────────────────────────────────

app.MapGet("/api/copy-trades", () =>
{
    var path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CryptoAITerminal", "webapi", "copy-trades.json");
    if (!File.Exists(path))
        return Results.Ok(Array.Empty<object>());
    var json = File.ReadAllText(path);
    return Results.Content(json, "application/json");
});

// ────────────────────────────────────────────────────────────────────────────
//  Order intake: WebApi сохраняет ордер в queue/, TerminalUI его подберёт.
// ────────────────────────────────────────────────────────────────────────────

app.MapPost("/api/orders/market", (MarketOrderRequest req, SharedStateService state) =>
{
    if (string.IsNullOrWhiteSpace(req.Symbol))
        return Results.BadRequest(new { error = "Symbol is required." });
    if (req.Quantity <= 0)
        return Results.BadRequest(new { error = "Quantity must be > 0." });

    var queued = state.EnqueueOrder(req);
    return Results.Accepted($"/api/orders/queue/{queued.Id}", new
    {
        id          = queued.Id,
        enqueuedUtc = queued.EnqueuedUtc,
        action      = queued.Action,
        status      = "queued",
    });
});

app.MapPost("/api/orders/cancel", (CancelOrderRequest req, SharedStateService state) =>
{
    if (string.IsNullOrWhiteSpace(req.OrderId))
        return Results.BadRequest(new { error = "OrderId is required." });
    if (string.IsNullOrWhiteSpace(req.Exchange))
        return Results.BadRequest(new { error = "Exchange is required." });

    var queued = state.EnqueueCancel(req);
    return Results.Accepted($"/api/orders/queue/{queued.Id}", new
    {
        id          = queued.Id,
        enqueuedUtc = queued.EnqueuedUtc,
        action      = queued.Action,
        status      = "queued",
    });
});

// ────────────────────────────────────────────────────────────────────────────
//  TradingView Webhook
//  POST /api/webhook/tradingview
//
//  Этот endpoint намеренно НЕ защищён bearer-токеном (TradingView не умеет
//  передавать Authorization header). Защита через поле "secret" в теле запроса.
//  Задать: env CRYPTOAI_TV_SECRET=your_secret
//
//  Пример Pine Script Alert Message:
//  { "action": "{{strategy.order.action}}", "symbol": "{{ticker}}",
//    "qty": 0.01, "exchange": "Binance", "secret": "your_secret" }
// ────────────────────────────────────────────────────────────────────────────

var tvSecret = Environment.GetEnvironmentVariable("CRYPTOAI_TV_SECRET");

app.MapPost("/api/webhook/tradingview", (TradingViewAlertDto alert, SharedStateService state) =>
{
    // Secret validation
    if (!string.IsNullOrWhiteSpace(tvSecret) &&
        !string.Equals(alert.Secret, tvSecret, StringComparison.Ordinal))
    {
        return Results.Json(
            new TradingViewWebhookResult { Accepted = false, Message = "Invalid secret." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (string.IsNullOrWhiteSpace(alert.Symbol))
        return Results.Json(
            new TradingViewWebhookResult { Accepted = false, Message = "Symbol is required." },
            statusCode: StatusCodes.Status400BadRequest);

    if (alert.Qty <= 0)
        return Results.Json(
            new TradingViewWebhookResult { Accepted = false, Message = "Qty must be > 0." },
            statusCode: StatusCodes.Status400BadRequest);

    var validActions = new[] { "buy", "sell", "close", "long", "short" };
    if (!validActions.Contains(alert.Action.Trim().ToLowerInvariant()))
        return Results.Json(
            new TradingViewWebhookResult
            {
                Accepted = false,
                Message  = $"Unknown action '{alert.Action}'. Use: buy | sell | close"
            },
            statusCode: StatusCodes.Status400BadRequest);

    var queued = state.EnqueueTradingViewAlert(alert);

    return Results.Ok(new TradingViewWebhookResult
    {
        Accepted    = true,
        OrderId     = queued.Id,
        Message     = $"Alert accepted: {alert.Action.ToUpperInvariant()} {alert.Symbol} qty={alert.Qty} on {alert.Exchange} {alert.Market}",
        ReceivedUtc = queued.EnqueuedUtc,
    });
});

// Webhook log — показывает последние N записей для диагностики
app.MapGet("/api/webhook/tradingview/log", () =>
{
    var logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CryptoAITerminal", "webapi", "webhook-log");

    var today = Path.Combine(logDir, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
    if (!File.Exists(today))
        return Results.Ok(new { entries = Array.Empty<string>(), date = DateTime.UtcNow.Date });

    var lines = File.ReadAllLines(today).TakeLast(50).ToArray();
    return Results.Ok(new { entries = lines, date = DateTime.UtcNow.Date, count = lines.Length });
});

app.Run();
