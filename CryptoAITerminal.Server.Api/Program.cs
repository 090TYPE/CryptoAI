using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;

var builder = WebApplication.CreateBuilder(args);

var conn = builder.Configuration["DB_CONN"]
           ?? builder.Configuration.GetConnectionString("Db")
           ?? "Host=localhost;Port=5432;Database=cryptoai;Username=postgres;Password=postgres";

builder.Services.AddSingleton(new Db(conn));
builder.Services.AddSingleton<UsersRepository>();
builder.Services.AddSingleton<FavoritesRepository>();
builder.Services.AddSingleton<CandleRepository>();
builder.Services.AddSingleton<ApiReadRepository>();
builder.Services.AddSingleton<ProviderKeyStore>();
builder.Services.AddSingleton(sp => new AiProxy(
    new HttpClient { Timeout = TimeSpan.FromSeconds(120) },
    sp.GetRequiredService<ProviderKeyStore>(),
    builder.Configuration["ANTHROPIC_API_KEY"],
    builder.Configuration["OPENAI_API_KEY"]));
builder.Services.AddSingleton<SecretsRepository>();
builder.Services.AddSingleton<WithdrawalsRepository>();
builder.Services.AddSingleton<BotConfigRepository>();
builder.Services.AddSingleton<PriceAlertsRepository>();
builder.Services.AddSingleton<NotificationRepository>();
builder.Services.AddSingleton<TrackedTokenRepository>();
builder.Services.AddSingleton<AuditRepository>();

// Custodial envelope encryption. Registered only when a master key is provided
// (base64 32-byte) — secrets endpoints return 503 otherwise. In production this is a
// Vault-backed cipher on the isolated executor; here it's the local AES implementation.
var kekB64 = builder.Configuration["CRYPTOAI_KEK_B64"];
if (!string.IsNullOrWhiteSpace(kekB64))
    builder.Services.AddSingleton<IEnvelopeCipher>(LocalAesEnvelopeCipher.FromBase64(kekB64));

// License verification: same RSA-signed tokens the app issues. Override the key in prod
// via LICENSE_PUBLIC_KEY_PEM; falls back to the app's embedded public key.
var licensePubKey = builder.Configuration["LICENSE_PUBLIC_KEY_PEM"] ?? LicenseTokenValidator.DefaultPublicKeyPem;
builder.Services.AddSingleton(new LicenseTokenValidator(licensePubKey));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Rate limiting: a fixed window per license token (or per client IP when unauthenticated),
// so a leaked token or a brute-force sweep against the public domain can't hammer the API.
var ratePerMin = int.TryParse(builder.Configuration["RATE_LIMIT_PER_MIN"], out var rpm) ? rpm : 120;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var license = ctx.Request.Headers["X-License"].ToString();
        var key = string.IsNullOrEmpty(license)
            ? (ctx.Connection.RemoteIpAddress?.ToString() ?? "anon")
            : license;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ratePerMin,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

var app = builder.Build();
app.UseCors();
app.UseRateLimiter();

// ── Auth ─────────────────────────────────────────────────────────────────────
// /health is open. /api/keys is admin-gated (X-Admin == ADMIN_TOKEN when set).
// Everything else needs a valid X-License: the token's RSA signature is verified (shared
// LicenseTokenValidator), then the license Name resolves to a user id in ctx.Items["uid"].
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;

    if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    if (path.StartsWith("/api/keys", StringComparison.OrdinalIgnoreCase))
    {
        var admin = Environment.GetEnvironmentVariable("ADMIN_TOKEN");
        if (!string.IsNullOrEmpty(admin) &&
            !string.Equals(ctx.Request.Headers["X-Admin"], admin, StringComparison.Ordinal))
        {
            await Deny(ctx, "admin token required");
            return;
        }
        await next();
        return;
    }

    var token = ctx.Request.Headers["X-License"].ToString();
    var validator = ctx.RequestServices.GetRequiredService<LicenseTokenValidator>();
    var check = validator.Validate(token);
    if (!check.IsValid)
    {
        // 402 tells the app "renew"; 401 is a bad/forged/missing token.
        ctx.Response.StatusCode = check.Result == LicenseCheck.Expired
            ? StatusCodes.Status402PaymentRequired
            : StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = check.Result == LicenseCheck.Expired ? "license_expired" : "license_invalid" });
        return;
    }

    // Identity is the verified license Name (stable across token renewals).
    var users = ctx.RequestServices.GetRequiredService<UsersRepository>();
    ctx.Items["uid"] = await users.GetOrCreateByLicenseAsync(check.Payload!.Name, ctx.RequestAborted);
    await next();
});

static async Task Deny(HttpContext ctx, string msg)
{
    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsJsonAsync(new { error = msg });
}

static Guid Uid(HttpContext ctx) => (Guid)ctx.Items["uid"]!;
static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTime.UtcNow }));

// ── Favorites (per-user; writes drive 24/7 tracking via the DB trigger) ───────
app.MapGet("/api/favorites", async (HttpContext ctx, FavoritesRepository favs) =>
    Results.Ok(await favs.ListForUserAsync(Uid(ctx), ctx.RequestAborted)));

app.MapPut("/api/favorites", async (HttpContext ctx, FavoriteInput[] body, FavoritesRepository favs) =>
{
    await favs.ReplaceAllAsync(Uid(ctx), body, ctx.RequestAborted);
    return Results.Ok(new { synced = body.Length });
});

app.MapPost("/api/favorites/{chain}/{token}", async (HttpContext ctx, string chain, string token, string? symbol, FavoritesRepository favs) =>
{
    await favs.AddAsync(Uid(ctx), chain, token, symbol, ctx.RequestAborted);
    return Results.Ok(new { added = token });
});

app.MapDelete("/api/favorites/{chain}/{token}", async (HttpContext ctx, string chain, string token, FavoritesRepository favs) =>
{
    await favs.RemoveAsync(Uid(ctx), chain, token, ctx.RequestAborted);
    return Results.Ok(new { removed = token });
});

// ── AI proxy (server-held key; returns the model's answer) ────────────────────
app.MapPost("/api/ai/message", async (HttpContext ctx, AiProxy ai) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);
    var result = await ai.ForwardAnthropicAsync(body, ctx.RequestAborted);
    return result is null
        ? Results.Json(new { error = "ai_key_not_configured" }, statusCode: 503)
        : Results.Content(result.Value.Body, "application/json", Encoding.UTF8, result.Value.Status);
});

app.MapPost("/api/ai/openai", async (HttpContext ctx, AiProxy ai) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);
    var result = await ai.ForwardOpenAiAsync(body, ctx.RequestAborted);
    return result is null
        ? Results.Json(new { error = "ai_key_not_configured" }, statusCode: 503)
        : Results.Content(result.Value.Body, "application/json", Encoding.UTF8, result.Value.Status);
});

// ── DEX data (read) ───────────────────────────────────────────────────────────
app.MapGet("/api/dex/candles", async (string chain, string token, string? tf, DateTime? from, DateTime? to, CandleRepository candles, CancellationToken ct) =>
{
    tf ??= "1m";
    if (!CandleRepository.IsValidTimeframe(tf))
        return Results.BadRequest(new { error = "tf must be one of 1m,5m,15m,1h,4h,1d" });
    var toUtc = (to ?? DateTime.UtcNow).ToUniversalTime();
    var fromUtc = (from ?? toUtc.AddDays(-1)).ToUniversalTime();
    return Results.Ok(await candles.GetCandlesAsync(tf, chain, token, fromUtc, toUtc, ct));
});

app.MapGet("/api/dex/token/{chain}/{token}", async (string chain, string token, ApiReadRepository read, CancellationToken ct) =>
{
    var detail = await read.GetTokenDetailAsync(chain, token, ct);
    return detail is null ? Results.NotFound(new { error = "not tracked" }) : Results.Ok(detail);
});

app.MapGet("/api/news", async (int? limit, ApiReadRepository read, CancellationToken ct) =>
    Results.Ok(await read.GetNewsAsync(Math.Clamp(limit ?? 50, 1, 200), ct)));

app.MapGet("/api/sentiment", async (ApiReadRepository read, CancellationToken ct) =>
    Results.Ok(await read.GetLatestSentimentAsync(ct)));

app.MapGet("/api/gas", async (ApiReadRepository read, CancellationToken ct) =>
    Results.Ok(await read.GetGasAsync(ct)));

app.MapGet("/api/onchain", async (string? asset, ApiReadRepository read, CancellationToken ct) =>
    Results.Ok(await read.GetOnChainAsync((asset ?? "btc").ToLowerInvariant(), ct)));

app.MapGet("/api/whales", async (int? limit, ApiReadRepository read, CancellationToken ct) =>
    Results.Ok(await read.GetWhalesAsync(Math.Clamp(limit ?? 50, 1, 200), ct)));

app.MapGet("/api/liquidations", async (int? limit, ApiReadRepository read, CancellationToken ct) =>
    Results.Ok(await read.GetLiquidationsAsync(Math.Clamp(limit ?? 50, 1, 200), ct)));

// ── Custodial secrets (envelope-encrypted; API never decrypts) ────────────────
app.MapPost("/api/secrets", async (HttpContext ctx, SecretInput body, SecretsRepository secrets, AuditRepository audit) =>
{
    var cipher = ctx.RequestServices.GetService<IEnvelopeCipher>();
    if (cipher is null) return Results.Json(new { error = "encryption_not_configured" }, statusCode: 503);
    if (string.IsNullOrWhiteSpace(body.Secret) || string.IsNullOrWhiteSpace(body.ExchangeOrChain))
        return Results.BadRequest(new { error = "secret and exchangeOrChain are required" });

    var (ciphertext, wrappedDek) = await cipher.EncryptAsync(body.Secret, ctx.RequestAborted);
    var uid = Uid(ctx);
    var id = await secrets.StoreAsync(uid, string.IsNullOrWhiteSpace(body.Kind) ? "cex_api" : body.Kind,
        body.Label, body.ExchangeOrChain, ciphertext, wrappedDek, body.Permissions, ctx.RequestAborted);

    var detail = JsonSerializer.Serialize(new { id, kind = body.Kind, target = body.ExchangeOrChain, permissions = body.Permissions });
    await audit.WriteAsync(uid, "user", "secret_stored", detail, ClientIp(ctx), ctx.RequestAborted);
    return Results.Ok(new { id });
});

app.MapGet("/api/secrets", async (HttpContext ctx, SecretsRepository secrets) =>
    Results.Ok(await secrets.ListForUserAsync(Uid(ctx), ctx.RequestAborted)));

app.MapDelete("/api/secrets/{id:guid}", async (HttpContext ctx, Guid id, SecretsRepository secrets, AuditRepository audit) =>
{
    var removed = await secrets.DeleteAsync(Uid(ctx), id, ctx.RequestAborted);
    if (removed > 0)
        await audit.WriteAsync(Uid(ctx), "user", "secret_deleted",
            JsonSerializer.Serialize(new { id }), ClientIp(ctx), ctx.RequestAborted);
    return Results.Ok(new { removed });
});

// ── Withdrawals (delay + cancel window — the custodial compensating control) ──
app.MapPost("/api/withdrawals", async (HttpContext ctx, WithdrawalRequest body, WithdrawalsRepository wd, AuditRepository audit, IConfiguration cfg) =>
{
    if (body.Amount <= 0 || string.IsNullOrWhiteSpace(body.Asset) || string.IsNullOrWhiteSpace(body.ToAddress))
        return Results.BadRequest(new { error = "asset, amount>0 and toAddress are required" });

    var delayMin = int.TryParse(cfg["WITHDRAWAL_DELAY_MINUTES"], out var d) ? d : 30;
    var executeAfter = DateTime.UtcNow.AddMinutes(delayMin);
    var uid = Uid(ctx);
    var id = await wd.CreateAsync(uid, body.Asset, body.Amount, body.ToAddress, executeAfter, ClientIp(ctx), ctx.RequestAborted);

    var detail = JsonSerializer.Serialize(new { id, body.Asset, body.Amount, body.ToAddress, executeAfter });
    await audit.WriteAsync(uid, "user", "withdrawal_requested", detail, ClientIp(ctx), ctx.RequestAborted);
    return Results.Ok(new { id, status = "pending", executeAfterUtc = executeAfter, cancellableUntilUtc = executeAfter });
});

app.MapGet("/api/withdrawals", async (HttpContext ctx, WithdrawalsRepository wd) =>
    Results.Ok(await wd.ListForUserAsync(Uid(ctx), ctx.RequestAborted)));

app.MapPost("/api/withdrawals/{id:guid}/cancel", async (HttpContext ctx, Guid id, WithdrawalsRepository wd, AuditRepository audit) =>
{
    var cancelled = await wd.CancelAsync(Uid(ctx), id, ctx.RequestAborted);
    if (cancelled > 0)
        await audit.WriteAsync(Uid(ctx), "user", "withdrawal_cancelled",
            JsonSerializer.Serialize(new { id }), ClientIp(ctx), ctx.RequestAborted);
    // 409 when it's too late (already executing/done) or unknown.
    return cancelled > 0 ? Results.Ok(new { cancelled = id }) : Results.Conflict(new { error = "not cancellable" });
});

// ── Autonomous strategy configs ───────────────────────────────────────────────
app.MapPost("/api/bots", async (HttpContext ctx, BotInput body, BotConfigRepository bots, AuditRepository audit) =>
{
    if (string.IsNullOrWhiteSpace(body.Strategy))
        return Results.BadRequest(new { error = "strategy is required" });

    var paramsJson = body.Params is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null }
        ? body.Params.Value.GetRawText() : null;
    var uid = Uid(ctx);
    var id = await bots.CreateAsync(uid, body.Strategy, paramsJson, body.Enabled, ctx.RequestAborted);
    await audit.WriteAsync(uid, "user", "bot_created",
        JsonSerializer.Serialize(new { id, body.Strategy, body.Enabled }), ClientIp(ctx), ctx.RequestAborted);
    return Results.Ok(new { id });
});

app.MapGet("/api/bots", async (HttpContext ctx, BotConfigRepository bots) =>
    Results.Ok(await bots.ListForUserAsync(Uid(ctx), ctx.RequestAborted)));

app.MapPost("/api/bots/{id:guid}/enable", async (HttpContext ctx, Guid id, bool on, BotConfigRepository bots, AuditRepository audit) =>
{
    var n = await bots.SetEnabledAsync(Uid(ctx), id, on, ctx.RequestAborted);
    if (n > 0)
        await audit.WriteAsync(Uid(ctx), "user", on ? "bot_enabled" : "bot_disabled",
            JsonSerializer.Serialize(new { id }), ClientIp(ctx), ctx.RequestAborted);
    return n > 0 ? Results.Ok(new { id, enabled = on }) : Results.NotFound(new { error = "not found" });
});

app.MapDelete("/api/bots/{id:guid}", async (HttpContext ctx, Guid id, BotConfigRepository bots, AuditRepository audit) =>
{
    var removed = await bots.DeleteAsync(Uid(ctx), id, ctx.RequestAborted);
    if (removed > 0)
        await audit.WriteAsync(Uid(ctx), "user", "bot_deleted",
            JsonSerializer.Serialize(new { id }), ClientIp(ctx), ctx.RequestAborted);
    return Results.Ok(new { removed });
});

// ── Price alerts (server watches 24/7, even with the user's PC off) ───────────
app.MapPost("/api/alerts", async (HttpContext ctx, AlertInput body, PriceAlertsRepository alerts, TrackedTokenRepository tracked) =>
{
    var cond = (body.Condition ?? "").ToLowerInvariant();
    if (cond is not ("above" or "below") || body.Threshold <= 0 ||
        string.IsNullOrWhiteSpace(body.Chain) || string.IsNullOrWhiteSpace(body.TokenAddress))
        return Results.BadRequest(new { error = "chain, tokenAddress, condition(above|below), threshold>0 required" });

    // Make sure the token is tracked so the collector fills its price.
    await tracked.EnsureTrackedAsync(body.Chain, body.TokenAddress, body.Symbol, ctx.RequestAborted);
    var id = await alerts.CreateAsync(Uid(ctx), body.Chain, body.TokenAddress, body.Symbol, cond, body.Threshold, ctx.RequestAborted);
    return Results.Ok(new { id, status = "active" });
});

app.MapGet("/api/alerts", async (HttpContext ctx, PriceAlertsRepository alerts) =>
    Results.Ok(await alerts.ListForUserAsync(Uid(ctx), ctx.RequestAborted)));

app.MapDelete("/api/alerts/{id:guid}", async (HttpContext ctx, Guid id, PriceAlertsRepository alerts) =>
    Results.Ok(new { removed = await alerts.DeleteAsync(Uid(ctx), id, ctx.RequestAborted) }));

// ── Notification channel (ntfy topic or Telegram) for alert/bot pushes ────────
app.MapGet("/api/notifications", async (HttpContext ctx, NotificationRepository notif) =>
{
    var ch = await notif.GetForUserAsync(Uid(ctx), ctx.RequestAborted);
    return ch is null
        ? Results.Ok(new { configured = false })
        // never echo the telegram bot token back
        : Results.Ok(new { configured = true, ch.Kind, ch.Target, ch.Enabled });
});

app.MapPut("/api/notifications", async (HttpContext ctx, NotificationInput body, NotificationRepository notif) =>
{
    var kind = (body.Kind ?? "").ToLowerInvariant();
    if (kind is not ("ntfy" or "telegram") || string.IsNullOrWhiteSpace(body.Target))
        return Results.BadRequest(new { error = "kind (ntfy|telegram) and target required" });
    if (kind == "telegram" && string.IsNullOrWhiteSpace(body.Token))
        return Results.BadRequest(new { error = "telegram needs a bot token" });

    await notif.UpsertAsync(Uid(ctx), kind, body.Target, body.Token, body.Enabled ?? true, ctx.RequestAborted);
    return Results.Ok(new { ok = true, kind });
});

// ── Admin: editable provider keys ─────────────────────────────────────────────
app.MapGet("/api/keys", async (ProviderKeyStore keys, CancellationToken ct) =>
{
    var all = await keys.ListAsync(ct);
    // Never return raw keys — mask to last 4.
    return Results.Ok(all.Select(k => new
    {
        k.Provider,
        k.Enabled,
        k.Note,
        k.UpdatedUtc,
        hasKey = k.ApiKey.Length > 0,
        masked = k.ApiKey.Length > 4 ? $"••••{k.ApiKey[^4..]}" : (k.ApiKey.Length > 0 ? "••••" : "")
    }));
});

app.MapPut("/api/keys/{provider}", async (string provider, KeyUpdate body, ProviderKeyStore keys, CancellationToken ct) =>
{
    await keys.SetAsync(provider, body.ApiKey, body.Enabled, body.Note, ct);
    return Results.Ok(new { provider, body.Enabled });
});

app.Run();

record KeyUpdate(string ApiKey, bool Enabled, string? Note);
record SecretInput(string? Kind, string? Label, string ExchangeOrChain, string Secret, string? Permissions);
record WithdrawalRequest(string Asset, decimal Amount, string ToAddress);
record BotInput(string Strategy, JsonElement? Params, bool Enabled);
record AlertInput(string Chain, string TokenAddress, string? Symbol, string Condition, decimal Threshold);
record NotificationInput(string Kind, string Target, string? Token, bool? Enabled);

public partial class Program; // for WebApplicationFactory in tests
