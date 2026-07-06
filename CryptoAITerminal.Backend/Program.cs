using System.Text;
using CryptoAITerminal.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration (env vars override appsettings) ─────────────────────────────
// LICENSE_PUBLIC_KEY_PEM  — RSA public key that verifies license tokens (same key the app embeds)
// ANTHROPIC_API_KEY       — server-held Claude key (proxied, never shipped to clients)
// COVALENT_API_KEY        — server-held Covalent key (portfolio)
// RATE_LIMIT_PER_MIN      — per-license request budget (default 60)
var cfg = builder.Configuration;
var publicKeyPem = cfg["LICENSE_PUBLIC_KEY_PEM"] ?? cfg["License:PublicKeyPem"] ?? string.Empty;
var anthropicKey = cfg["ANTHROPIC_API_KEY"] ?? cfg["Upstream:AnthropicKey"] ?? string.Empty;
var covalentKey = cfg["COVALENT_API_KEY"] ?? cfg["Upstream:CovalentKey"] ?? string.Empty;
var perMinute = int.TryParse(cfg["RATE_LIMIT_PER_MIN"], out var rpm) ? rpm : 60;

builder.Services.AddSingleton(new LicenseTokenValidator(publicKeyPem));
builder.Services.AddSingleton(new SlidingRateLimiter(perMinute, TimeSpan.FromMinutes(1)));
builder.Services.AddSingleton(new TtlCache(TimeSpan.FromSeconds(30)));
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    http.Timeout = TimeSpan.FromSeconds(60);
    return new UpstreamProxy(http, anthropicKey, covalentKey);
});

var app = builder.Build();

// Verifies the X-License header. Returns the license name to key the rate limiter, or null.
static string? Authorize(HttpContext ctx, LicenseTokenValidator validator, out IResult? error)
{
    error = null;
    var token = ctx.Request.Headers["X-License"].ToString();
    var result = validator.Validate(token);
    if (result.IsValid)
    {
        return result.Payload!.Name;
    }

    error = result.Result switch
    {
        LicenseCheck.Expired => Results.Json(new { error = "license_expired" }, statusCode: 402),
        _ => Results.Json(new { error = "license_invalid" }, statusCode: 401),
    };
    return null;
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTime.UtcNow }));

// Online license check (optional — the app can also validate offline).
app.MapPost("/api/license/verify", async (HttpContext ctx, LicenseTokenValidator validator) =>
{
    var token = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var r = validator.Validate(token.Trim().Trim('"'));
    return Results.Json(new { valid = r.IsValid, state = r.Result.ToString(), name = r.Payload?.Name, edition = r.Payload?.Edition, expires = r.Payload?.Expires });
});

// Proxy: Claude Messages API with the server-held key.
app.MapPost("/api/ai/message", async (HttpContext ctx, LicenseTokenValidator validator, SlidingRateLimiter limiter, UpstreamProxy proxy, CancellationToken ct) =>
{
    var name = Authorize(ctx, validator, out var error);
    if (name is null) return error!;
    if (!limiter.TryAcquire(name)) return Results.Json(new { error = "rate_limited" }, statusCode: 429);
    if (!proxy.AiConfigured) return Results.Json(new { error = "ai_key_not_configured" }, statusCode: 503);

    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ct);
    var (status, upstream) = await proxy.ForwardAnthropicAsync(body, ct);
    return Results.Content(upstream, "application/json", Encoding.UTF8, status);
});

// Proxy: cross-chain portfolio balances via Covalent (cached).
app.MapGet("/api/portfolio/{chainId:int}/{address}", async (int chainId, string address, HttpContext ctx, LicenseTokenValidator validator, SlidingRateLimiter limiter, TtlCache cache, UpstreamProxy proxy, CancellationToken ct) =>
{
    var name = Authorize(ctx, validator, out var error);
    if (name is null) return error!;
    if (!limiter.TryAcquire(name)) return Results.Json(new { error = "rate_limited" }, statusCode: 429);
    if (!proxy.PortfolioConfigured) return Results.Json(new { error = "portfolio_key_not_configured" }, statusCode: 503);

    var cacheKey = $"{chainId}:{address.ToLowerInvariant()}";
    if (cache.TryGet(cacheKey, out var cached))
    {
        return Results.Content(cached, "application/json");
    }

    var (status, upstream) = await proxy.FetchCovalentBalancesAsync(chainId, address, ct);
    if (status == 200)
    {
        cache.Set(cacheKey, upstream);
    }
    return Results.Content(upstream, "application/json", Encoding.UTF8, status);
});

app.Run();

// Exposed so the test project can reference the app assembly.
public partial class Program;
