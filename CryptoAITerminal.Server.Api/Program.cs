using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Executor;
using CryptoAITerminal.Server.Api;
using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;
using Microsoft.AspNetCore.HttpOverrides;

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
builder.Services.AddSingleton<TwoFactorRepository>();
builder.Services.AddSingleton<PriceAlertsRepository>();
builder.Services.AddSingleton<NotificationRepository>();
builder.Services.AddSingleton<InboxRepository>();
builder.Services.AddSingleton<AiDigestRepository>();
builder.Services.AddSingleton<PersonalAiRepository>();
builder.Services.AddSingleton<TrackedTokenRepository>();
builder.Services.AddSingleton<AuditRepository>();

// Shared-response cache for the endpoints that are identical for every user (see
// SharedEndpoints). Cost scales with TTL, not with the number of connected terminals.
builder.Services.AddSingleton<SharedResponseCache>();

// AI spend controls. The proxy forwards with the SERVER's key, so the client must not get to
// choose the model, the output length or the context size — see AiRequestPolicy / AiBudget.
var aiPolicy = AiPolicyOptions.FromConfig(k => builder.Configuration[k]);
builder.Services.AddSingleton(aiPolicy);
builder.Services.AddSingleton(new AiBudget(aiPolicy.DailyTokenCapPerLicense));

// Deployment role. "edge" is the internet-facing node behind Caddy; "core" is the isolated node
// with no public IP. Only core may hold CRYPTOAI_KEK_B64: TradingService decrypts every user's
// exchange keys in the memory of whatever process registers the cipher, so an edge node that
// registers it puts the whole custodial key set one RCE away from the internet. On edge the
// cipher is not registered at all — /api/secrets and /api/2fa answer 503 (they already check),
// and /api/trade/* is not mapped.
var isEdgeRole = string.Equals(builder.Configuration["CRYPTOAI_ROLE"], "edge", StringComparison.OrdinalIgnoreCase);

// Custodial envelope encryption. Registered only when a master key is provided
// (base64 32-byte) — secrets endpoints return 503 otherwise. In production this is a
// Vault-backed cipher on the isolated executor; here it's the local AES implementation.
var kekB64 = builder.Configuration["CRYPTOAI_KEK_B64"];
if (!isEdgeRole && !string.IsNullOrWhiteSpace(kekB64))
    builder.Services.AddSingleton<IEnvelopeCipher>(LocalAesEnvelopeCipher.FromBase64(kekB64));

// At-rest protection for the server's OWN operational secrets (Telegram bot tokens, provider API
// keys). Deliberately a different key from the custodial KEK: the edge node must be able to store
// and read these, and must never be able to decrypt a customer's exchange key. Unset = today's
// behaviour, values stored verbatim.
builder.Services.AddSingleton(ColumnCipher.FromBase64Key(builder.Configuration[ColumnCipher.KeyEnvVar]));

// ── Manual trading (server-side execution) ───────────────────────────────────
// Collaborators live in the executor project; the API owns only the HTTP surface.
if (!isEdgeRole)
{
    builder.Services.AddSingleton<IGatewayFactory, GatewayFactory>();
    builder.Services.AddSingleton<ICexKeyProvider, SecretsCexKeyProvider>();
    builder.Services.AddSingleton<IPriceSource>(new HttpPriceSource(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
    builder.Services.AddSingleton<IManualRiskGate>(_ => new PerOrderCapManualRiskGate(
        decimal.TryParse(Environment.GetEnvironmentVariable("MANUAL_MAX_NOTIONAL_USD"), out var cap) ? cap : 5000m));
    builder.Services.AddSingleton<IOrderJournal, OrderJournalRepository>();
    builder.Services.AddSingleton<ITradingService, TradingService>();
}

// Live order-status / notification stream to the desktop terminal.
builder.Services.AddSignalR();
builder.Services.AddSingleton<ITradeNotifier, TradeNotifier>();

// License verification: same RSA-signed tokens the app issues. Override the key in prod
// via LICENSE_PUBLIC_KEY_PEM; falls back to the app's embedded public key.
var licensePubKey = builder.Configuration["LICENSE_PUBLIC_KEY_PEM"] ?? LicenseTokenValidator.DefaultPublicKeyPem;
builder.Services.AddSingleton(new LicenseTokenValidator(licensePubKey));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Behind Caddy every request otherwise arrives from the proxy's container address: audit_log.ip
// recorded that one address for every withdrawal and secret write, and the whole anonymous
// internet shared a single rate-limit partition. Forwarded headers are honoured ONLY from the
// proxies listed here — trusting them from anywhere lets a caller pick its own IP.
// TRUSTED_PROXY_CIDRS overrides; the default is loopback plus the Docker bridge range.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownNetworks.Clear();
    var cidrs = builder.Configuration["TRUSTED_PROXY_CIDRS"] ?? "127.0.0.1/32,::1/128,172.16.0.0/12";
    foreach (var entry in cidrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        // Fully qualified: System.Net also has an IPNetwork, and KnownNetworks wants this one.
        if (Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(entry, out var network))
            o.KnownNetworks.Add(network);
        else if (IPAddress.TryParse(entry, out var single))
            o.KnownProxies.Add(single);
    }
});

// Rate limiting: a fixed window per licence SUBJECT (or per client IP when unauthenticated),
// so a leaked token or a brute-force sweep against the public domain can't hammer the API.
var ratePerMin = int.TryParse(builder.Configuration["RATE_LIMIT_PER_MIN"], out var rpm) ? rpm : 120;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        // The subject comes from the VERIFIED payload, put in Items by the licence middleware that
        // deliberately runs before this limiter. Partitioning on the raw header instead meant one
        // valid token had unlimited spellings — padding, base64url vs base64, leading whitespace —
        // and therefore an unlimited supply of fresh windows.
        var key = ctx.Items.TryGetValue(LicenseCtx.Subject, out var subject) && subject is string s && s.Length > 0
            ? s
            : (ctx.Connection.RemoteIpAddress?.ToString() ?? "anon");
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ratePerMin,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

var app = builder.Build();

// Minimal-API handlers throw; without this a handler fault answered an empty 500 with nothing the
// client could act on and nothing but a stack trace in the container log.
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { error = "internal_error" });
}));

app.UseForwardedHeaders();
app.UseCors();

// ── Auth, part 1: licence verification ───────────────────────────────────────
// Runs BEFORE the limiter so the limiter can partition on a canonical subject instead of on the
// raw header. It only records the verdict and never answers: a rejected request must still pass
// through the limiter, or an invalid token would be an unlimited supply of free 401s.
// /health, /api/keys and /api/admin carry no licence — they are gated in part 2.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;
    var licensed =
        !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/api/keys", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase);

    if (licensed)
    {
        var validator = ctx.RequestServices.GetRequiredService<LicenseTokenValidator>();
        var check = validator.Validate(ctx.Request.Headers["X-License"].ToString());
        ctx.Items[LicenseCtx.Check] = check;
        if (check.IsValid && check.Payload is not null)
            ctx.Items[LicenseCtx.Subject] = LicenseIdentity.Subject(check.Payload);
    }

    await next();
});

app.UseRateLimiter();

// ── Auth, part 2: enforcement ────────────────────────────────────────────────
// /api/keys is admin-gated: requires X-Admin == ADMIN_TOKEN, and is denied outright when
// ADMIN_TOKEN is unset (fail closed). Everything else turns the verdict from part 1 into a
// response, and resolves the licence to a user id in ctx.Items["uid"]. That database round trip
// sits after the limiter on purpose.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;

    if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    if (path.StartsWith("/api/keys", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
    {
        // Fail CLOSED: this endpoint can read/overwrite server-held provider keys, so
        // an unset ADMIN_TOKEN must deny access (not fall through unauthenticated).
        var admin = Environment.GetEnvironmentVariable("ADMIN_TOKEN");
        if (string.IsNullOrEmpty(admin) ||
            !string.Equals(ctx.Request.Headers["X-Admin"], admin, StringComparison.Ordinal))
        {
            await Deny(ctx, "admin token required");
            return;
        }
        await next();
        return;
    }

    var check = ctx.Items[LicenseCtx.Check] as LicenseCheckResult;
    if (check is null || !check.IsValid || check.Payload is null)
    {
        // 402 tells the app "renew"; 401 is a bad/forged/missing token.
        var expired = check?.Result == LicenseCheck.Expired;
        ctx.Response.StatusCode = expired
            ? StatusCodes.Status402PaymentRequired
            : StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = expired ? "license_expired" : "license_invalid" });
        return;
    }

    // Identity is the licence SUBJECT, not the display Name: Name comes from the buyer's Telegram
    // profile, is not unique and is editable in seconds, so two customers could land on one user
    // row and a rename could take over another customer's secrets, withdrawals and bots.
    var payload = check.Payload;
    var users = ctx.RequestServices.GetRequiredService<UsersRepository>();
    ctx.Items["uid"] = await users.GetOrCreateByLicenseAsync(
        LicenseIdentity.Subject(payload), payload.Name, ctx.RequestAborted);
    await next();
});

static async Task Deny(HttpContext ctx, string msg)
{
    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsJsonAsync(new { error = msg });
}

static Guid Uid(HttpContext ctx) => (Guid)ctx.Items["uid"]!;
static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

// Canonical per-customer key for everything metered: the rate-limit partition and the AI budget.
// Derived from the verified payload, so re-encoding the header mints no new bucket.
static string LicenseKey(HttpContext ctx) =>
    ctx.Items.TryGetValue(LicenseCtx.Subject, out var v) && v is string s ? s : string.Empty;

// Always checks the code against the stored secret (used for enable + verify).
static async Task<bool> VerifyCodeAsync(HttpContext ctx, string? code)
{
    var repo = ctx.RequestServices.GetRequiredService<TwoFactorRepository>();
    var row = await repo.GetAsync(Uid(ctx), ctx.RequestAborted);
    var cipher = ctx.RequestServices.GetService<IEnvelopeCipher>();
    if (row is null || cipher is null || string.IsNullOrWhiteSpace(code)) return false;
    var secret = await cipher.DecryptAsync(row.Ciphertext, row.WrappedDek, ctx.RequestAborted);
    return Totp.Verify(secret, code!);
}

// Gate for custodial actions: passes when 2FA isn't enabled, else requires a valid code.
static async Task<bool> Verify2faAsync(HttpContext ctx, string? code)
{
    var repo = ctx.RequestServices.GetRequiredService<TwoFactorRepository>();
    var row = await repo.GetAsync(Uid(ctx), ctx.RequestAborted);
    if (row is null || !row.Enabled) return true; // 2FA not set up → nothing to check
    return await VerifyCodeAsync(ctx, code);
}

// Answers 503 when the database is unreachable. It used to return 200 unconditionally, which meant
// an uptime monitor pointed at it reported green while the whole contour was down. Result cached
// briefly so this endpoint cannot itself become a way to hammer Postgres.
app.MapGet("/health", async (ApiReadRepository read, SharedResponseCache cache) =>
{
    var payload = await cache.GetOrCreateAsync("health:db", TimeSpan.FromSeconds(5),
        async ct => (await read.IsDatabaseReachableAsync(ct)).ToString());

    var dbUp = string.Equals(payload.Json, bool.TrueString, StringComparison.OrdinalIgnoreCase);
    return Results.Json(
        new { status = dbUp ? "ok" : "degraded", database = dbUp ? "up" : "down", ts = DateTime.UtcNow },
        statusCode: dbUp ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

// Admin-gated (X-Admin, fails closed without ADMIN_TOKEN). collector_runs was written on every pass
// and read by nothing, so a collector that quietly stopped stayed stopped until someone noticed the
// data had gone stale. Poll this from whatever watches the box.
app.MapGet("/api/admin/collectors", async (ApiReadRepository read, IConfiguration cfg, CancellationToken ct) =>
{
    var staleAfter = int.TryParse(cfg["COLLECTOR_STALE_MINUTES"], out var m) && m > 0 ? m : 120;
    var rows = await read.GetCollectorHealthAsync(ct);

    var failing = rows.Where(r => r.LastOk == false).Select(r => r.Name).ToList();
    var stale = rows.Where(r => r.MinutesAgo is null || r.MinutesAgo > staleAfter).Select(r => r.Name).ToList();

    return Results.Json(new
    {
        degraded = failing.Count > 0 || stale.Count > 0,
        staleAfterMinutes = staleAfter,
        failing,
        stale,
        collectors = rows,
    }, statusCode: failing.Count > 0 || stale.Count > 0
        ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status200OK);
});

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

// ── AI proxy (server-held key) ────────────────────────────────────────────────
// The key never ships to clients, but that alone is not enough: the body used to be forwarded
// verbatim, so a client picked the model, the output length and the context size while the
// server paid for all three. Every call now goes through AiRequestPolicy (allowlisted model,
// clamped max_tokens, capped body and message count, streaming forced off) and AiBudget
// (per-licence daily token cap, charged from the vendor's own usage block).

// Bounded read: refuse an oversized body instead of buffering it into memory first.
static async Task<string?> ReadBoundedBodyAsync(HttpContext ctx, int maxChars)
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var buffer = new char[8192];
    var sb = new StringBuilder();
    int read;
    while ((read = await reader.ReadAsync(buffer, ctx.RequestAborted)) > 0)
    {
        sb.Append(buffer, 0, read);
        if (sb.Length > maxChars) return null;
    }
    return sb.ToString();
}

static async Task<IResult> ForwardAiAsync(HttpContext ctx, AiVendor vendor, AiProxy ai,
    AiPolicyOptions policy, AiBudget budget)
{
    var license = LicenseKey(ctx);
    if (!budget.HasHeadroom(license))
        return Results.Json(new { error = "ai_daily_budget_exhausted", cap = budget.DailyCap }, statusCode: 429);

    var raw = await ReadBoundedBodyAsync(ctx, policy.MaxRequestBytes);
    if (raw is null)
        return Results.Json(new { error = "request_too_large", maxBytes = policy.MaxRequestBytes }, statusCode: 413);

    var gated = AiRequestPolicy.Apply(raw, policy, vendor);
    if (!gated.Ok)
        return Results.BadRequest(new { error = gated.Error });

    var result = vendor == AiVendor.Anthropic
        ? await ai.ForwardAnthropicAsync(gated.Body!, ctx.RequestAborted)
        : await ai.ForwardOpenAiAsync(gated.Body!, ctx.RequestAborted);

    if (result is null) return Results.Json(new { error = "ai_key_not_configured" }, statusCode: 503);

    budget.Charge(license, AiRequestPolicy.CountUsage(result.Value.Body, vendor));
    return Results.Content(result.Value.Body, "application/json", Encoding.UTF8, result.Value.Status);
}

app.MapPost("/api/ai/message", (HttpContext ctx, AiProxy ai, AiPolicyOptions policy, AiBudget budget) =>
    ForwardAiAsync(ctx, AiVendor.Anthropic, ai, policy, budget));

app.MapPost("/api/ai/openai", (HttpContext ctx, AiProxy ai, AiPolicyOptions policy, AiBudget budget) =>
    ForwardAiAsync(ctx, AiVendor.OpenAi, ai, policy, budget));

// So the terminal can show remaining allowance instead of discovering the cap via a 429.
app.MapGet("/api/ai/budget", (HttpContext ctx, AiBudget budget) =>
{
    var license = LicenseKey(ctx);
    return Results.Ok(new
    {
        used = budget.Used(license),
        remaining = budget.Remaining(license),
        cap = budget.DailyCap,
    });
});

// ── RAG: answer questions grounded in OUR collected data, not the model's memory ──
app.MapPost("/api/ai/ask", async (HttpContext ctx, AskInput body, AiProxy ai,
    PersonalAiRepository personal, AiDigestRepository digests, AiBudget budget, IConfiguration askCfg) =>
{
    if (string.IsNullOrWhiteSpace(body.Question))
        return Results.BadRequest(new { error = "question required" });

    // The server composes this request itself, so the model and length are already ours — but it
    // still spends the server key, so it draws on the same per-licence daily budget.
    var askLicense = LicenseKey(ctx);
    if (!budget.HasHeadroom(askLicense))
        return Results.Json(new { error = "ai_daily_budget_exhausted", cap = budget.DailyCap }, statusCode: 429);

    // Context = what the server actually knows: this user's watchlist, the latest AI
    // digests and recent headlines. The model must answer from this or admit it can't.
    var context = JsonSerializer.Serialize(new
    {
        yourWatchlist = await personal.GetUserPortfolioFactsAsync(Uid(ctx), ctx.RequestAborted),
        latestDigests = await digests.ListRecentAsync(null, 8, ctx.RequestAborted),
        headlines = await digests.GetNewsHeadlinesAsync(15, ctx.RequestAborted)
    });

    var request = JsonSerializer.Serialize(new
    {
        // Override with AI_ASK_MODEL. Default is the current Haiku — cheaper and better at the
        // "answer strictly from this context, as JSON-ish prose" job than 3.5 Haiku was.
        model = askCfg["AI_ASK_MODEL"] ?? "claude-haiku-4-5-20251001",
        max_tokens = 700,
        system = "Answer the trader's question using ONLY the provided context (their watchlist, our AI digests, " +
                 "recent headlines). Quote the numbers from it. If the context doesn't contain the answer, say so " +
                 "plainly instead of guessing or drawing on outside knowledge. Not financial advice.",
        messages = new[] { new { role = "user", content = $"Context:\n{context}\n\nQuestion: {body.Question}" } }
    });

    var res = await ai.ForwardAnthropicAsync(request, ctx.RequestAborted);
    if (res is null) return Results.Json(new { error = "ai_key_not_configured" }, statusCode: 503);
    budget.Charge(askLicense, AiRequestPolicy.CountUsage(res.Value.Body, AiVendor.Anthropic));
    if (res.Value.Status != 200) return Results.Json(new { error = "upstream", status = res.Value.Status }, statusCode: 502);

    // Unwrap the model envelope so the terminal just gets the answer text.
    try
    {
        using var doc = JsonDocument.Parse(res.Value.Body);
        var text = "";
        if (doc.RootElement.TryGetProperty("content", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var b in arr.EnumerateArray())
                if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tv))
                { text = tv.GetString() ?? ""; break; }
        return Results.Ok(new { answer = text });
    }
    catch { return Results.Json(new { error = "unparsable_upstream" }, statusCode: 502); }
});

// ── Shared reads: one server-side computation, served to every user ───────────
// /api/dex/candles, /api/dex/token, /api/news, /api/sentiment, /api/gas, /api/onchain,
// /api/whales, /api/liquidations, /api/digests + /api/cache/stats. All go through
// SharedResponseCache with per-endpoint TTLs matched to the collector cadence, plus
// ETag/304, so ~100 terminals cost one DB read per TTL rather than one per request.
// Reading a token also stamps demand (last_read_utc), which is what lets the poller
// back off charts nobody has open. See SharedEndpoints.cs.
app.MapSharedEndpoints();

// Per-user COMPOSITION over those same shared parts: /api/watchlist (the AI-signals tab in one
// request) and /api/dex/tokens (an explicit set). The list of tokens is per-user; every
// analytics block inside it is a shared cache entry, so users watching the same coin share it.
app.MapCompositeEndpoints();

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

// ── 2FA (TOTP) ────────────────────────────────────────────────────────────────
// Re-provisioning is authenticated with the CURRENT factor. UpsertSecretAsync overwrites the secret
// AND resets enabled=false, and Verify2faAsync treats "not enabled" as "nothing to check" — so
// without this gate anyone holding a licence token could POST here once and walk straight through
// the 2FA check on /api/withdrawals. First-time setup (no row, or a row that was never enabled)
// still needs no code.
app.MapPost("/api/2fa/setup", async (HttpContext ctx, TwoFaCode? body, TwoFactorRepository tfa, AuditRepository audit) =>
{
    var cipher = ctx.RequestServices.GetService<IEnvelopeCipher>();
    if (cipher is null) return Results.Json(new { error = "encryption_not_configured" }, statusCode: 503);

    var existing = await tfa.GetAsync(Uid(ctx), ctx.RequestAborted);
    if (existing is { Enabled: true } && !await VerifyCodeAsync(ctx, body?.Code))
        return Results.Json(new { error = "current 2fa code required to re-provision" }, statusCode: 401);

    var secret = Totp.GenerateSecret();
    var (cipherB, wrapped) = await cipher.EncryptAsync(secret, ctx.RequestAborted);
    await tfa.UpsertSecretAsync(Uid(ctx), cipherB, wrapped, ctx.RequestAborted);

    if (existing is { Enabled: true })
        await audit.WriteAsync(Uid(ctx), "user", "2fa_reset", null, ClientIp(ctx), ctx.RequestAborted);

    // secret returned once so the user adds it to their authenticator app
    return Results.Ok(new { secret, otpauth = Totp.ProvisioningUri(secret, Uid(ctx).ToString()) });
});

app.MapPost("/api/2fa/enable", async (HttpContext ctx, TwoFaCode body, TwoFactorRepository tfa, AuditRepository audit) =>
{
    if (!await VerifyCodeAsync(ctx, body.Code)) return Results.BadRequest(new { error = "invalid code" });
    await tfa.SetEnabledAsync(Uid(ctx), true, ctx.RequestAborted);
    await audit.WriteAsync(Uid(ctx), "user", "2fa_enabled", null, ClientIp(ctx), ctx.RequestAborted);
    return Results.Ok(new { enabled = true });
});

app.MapPost("/api/2fa/verify", async (HttpContext ctx, TwoFaCode body) =>
    await VerifyCodeAsync(ctx, body.Code) ? Results.Ok(new { ok = true }) : Results.Unauthorized());

// ── Withdrawals (delay + cancel window — the custodial compensating control) ──
app.MapPost("/api/withdrawals", async (HttpContext ctx, WithdrawalRequest body, WithdrawalsRepository wd, AuditRepository audit, IConfiguration cfg) =>
{
    if (body.Amount <= 0 || string.IsNullOrWhiteSpace(body.Asset) || string.IsNullOrWhiteSpace(body.ToAddress))
        return Results.BadRequest(new { error = "asset, amount>0 and toAddress are required" });
    // Custodial action → require a valid 2FA code when the user has 2FA enabled.
    if (!await Verify2faAsync(ctx, body.Code))
        return Results.Json(new { error = "2fa_required" }, statusCode: 401);

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

// ── Inbox: how the desktop terminal receives server events (every user, no setup) ─
// Per-user, so deliberately NOT in SharedEndpoints — never share an inbox cache entry.
app.MapGet("/api/inbox", async (HttpContext ctx, bool? unread, int? limit, InboxRepository inbox) =>
    Results.Ok(await inbox.ListForUserAsync(Uid(ctx), unread ?? false, Math.Clamp(limit ?? 50, 1, 200), ctx.RequestAborted)));

app.MapGet("/api/inbox/unread-count", async (HttpContext ctx, InboxRepository inbox) =>
    Results.Ok(new { unread = await inbox.UnreadCountAsync(Uid(ctx), ctx.RequestAborted) }));

app.MapPost("/api/inbox/{id:guid}/read", async (HttpContext ctx, Guid id, InboxRepository inbox) =>
    Results.Ok(new { read = await inbox.MarkReadAsync(Uid(ctx), id, ctx.RequestAborted) }));

app.MapPost("/api/inbox/read-all", async (HttpContext ctx, InboxRepository inbox) =>
    Results.Ok(new { read = await inbox.MarkAllReadAsync(Uid(ctx), ctx.RequestAborted) }));

// ── Notification channel (ntfy topic or Telegram) for OPTIONAL phone pushes ──
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

// ── Manual trading (place / cancel / positions) ───────────────────────────────
// Not mapped on an edge node: server-side execution needs the custodial master key, which an
// internet-facing process must not hold. See CRYPTOAI_ROLE above.
if (!isEdgeRole) app.MapTradeEndpoints();
app.MapHub<TradeHub>("/hubs/trade");

app.Run();

/// <summary>ctx.Items keys for the licence verdict, written once by the licence middleware.</summary>
static class LicenseCtx
{
    public const string Check = "license.check";
    public const string Subject = "license.subject";
}

record KeyUpdate(string ApiKey, bool Enabled, string? Note);
record SecretInput(string? Kind, string? Label, string ExchangeOrChain, string Secret, string? Permissions);
record WithdrawalRequest(string Asset, decimal Amount, string ToAddress, string? Code);
record TwoFaCode(string Code);
record BotInput(string Strategy, JsonElement? Params, bool Enabled);
record AlertInput(string Chain, string TokenAddress, string? Symbol, string Condition, decimal Threshold);
record NotificationInput(string Kind, string Target, string? Token, bool? Enabled);
record AskInput(string Question);

public partial class Program; // for WebApplicationFactory in tests
