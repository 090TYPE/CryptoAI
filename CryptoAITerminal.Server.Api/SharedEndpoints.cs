using System.Text;
using System.Text.Json;
using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.Server.Api;

/// <summary>
/// The GET endpoints whose response is byte-for-byte the same for every licensed user.
/// They are served through <see cref="SharedResponseCache"/>, so with ~100 terminals the
/// server does one database read per TTL per endpoint instead of one per request, and a
/// client whose copy is still current gets 304 with an empty body.
///
/// TTLs are matched to how often the collector behind each endpoint actually writes
/// (see <c>CandleWorker</c> collector intervals) — caching longer than the write cadence
/// would serve stale data, caching shorter would just burn reads. Every TTL is overridable
/// with <c>CACHE_TTL_&lt;NAME&gt;</c> in seconds.
///
/// Per-user endpoints stay deliberately OUT of this file — favorites, inbox, alerts, bots,
/// secrets, withdrawals, 2FA, notifications, <c>/api/ai/*</c> and <c>/api/trade/*</c> are keyed
/// by uid and must never share a cache entry.
/// </summary>
public static class SharedEndpoints
{
    /// <summary>
    /// The one place the per-token cache key is spelled. <see cref="CompositeEndpoints"/> composes
    /// watchlist pages out of these exact entries, so the two must never drift apart — a mismatch
    /// would silently double the cache and halve the reuse.
    /// </summary>
    internal static string TokenDetailKey(string chain, string token) => $"token:{chain}:{token}";

    /// <summary>TTL for a token's analytics block, shared with the composite endpoints.</summary>
    internal static TimeSpan TokenDetailTtl(IConfiguration cfg) => Ttl(cfg, "TOKEN", 20);

    public static void MapSharedEndpoints(this IEndpointRouteBuilder app)
    {
        var cfg = app.ServiceProvider.GetRequiredService<IConfiguration>();

        // Collector cadence → TTL. Left column is the collector interval, for reference.
        var tCandles = Ttl(cfg, "CANDLES", 20);     // CandlePollingService: 60s per token
        var tToken = TokenDetailTtl(cfg);           // same poll + metadata/security collectors
        var tNews = Ttl(cfg, "NEWS", 60);           // NewsCollector 5m, CryptoPanic 10m
        var tSentiment = Ttl(cfg, "SENTIMENT", 120); // SentimentCollector 15m
        var tGas = Ttl(cfg, "GAS", 30);             // GasCollector 2m
        var tOnChain = Ttl(cfg, "ONCHAIN", 300);    // OnChainCollector 1h
        var tWhales = Ttl(cfg, "WHALES", 60);       // WhalesCollector 10m
        var tLiquidations = Ttl(cfg, "LIQUIDATIONS", 60); // LiquidationsCollector 10m
        var tDigests = Ttl(cfg, "DIGESTS", 120);    // digest jobs, 15m tick

        // ── Candles ───────────────────────────────────────────────────────────────
        app.MapGet("/api/dex/candles", async (HttpContext ctx, string chain, string token, string? tf,
            DateTime? from, DateTime? to, CandleRepository candles, TrackedTokenRepository tracked) =>
        {
            tf ??= "1m";
            if (!CandleRepository.IsValidTimeframe(tf))
                return Results.BadRequest(new { error = "tf must be one of 1m,5m,15m,1h,4h,1d" });

            // Floor BOTH ends to the TTL: a hundred terminals asking for "the last day" at a
            // hundred slightly different instants then collapse onto one key and one read.
            // The query uses the floored values, so the response really is what the key says.
            var toUtc = SharedResponseCache.Bucket((to ?? DateTime.UtcNow).ToUniversalTime(), tCandles);
            var fromUtc = SharedResponseCache.Bucket((from ?? toUtc.AddDays(-1)).ToUniversalTime(), tCandles);
            var key = $"candles:{chain}:{token}:{tf}:{fromUtc.Ticks}:{toUtc.Ticks}";

            return await ServeAsync(ctx, key, tCandles, async ct =>
            {
                // Inside the factory on purpose: this runs only on a cache miss, so the demand
                // signal costs at most one UPDATE per TTL per token instead of one per request.
                await tracked.TouchReadAsync(chain, token, ct);
                return await candles.GetCandlesAsync(tf, chain, token, fromUtc, toUtc, ct);
            });
        });

        // ── Token detail ──────────────────────────────────────────────────────────
        app.MapGet("/api/dex/token/{chain}/{token}", async (HttpContext ctx, string chain, string token,
            ApiReadRepository read, TrackedTokenRepository tracked) =>
            await ServeAsync(ctx, TokenDetailKey(chain, token), tToken, async ct =>
            {
                await tracked.TouchReadAsync(chain, token, ct);
                return await read.GetTokenDetailAsync(chain, token, ct);
            }));

        // ── Market-wide feeds ─────────────────────────────────────────────────────
        app.MapGet("/api/news", async (HttpContext ctx, int? limit, ApiReadRepository read) =>
        {
            var n = Math.Clamp(limit ?? 50, 1, 200);
            return await ServeAsync(ctx, $"news:{n}", tNews, ct => Box(read.GetNewsAsync(n, ct)));
        });

        app.MapGet("/api/sentiment", async (HttpContext ctx, ApiReadRepository read) =>
            await ServeAsync(ctx, "sentiment", tSentiment, ct => Box(read.GetLatestSentimentAsync(ct))));

        app.MapGet("/api/gas", async (HttpContext ctx, ApiReadRepository read) =>
            await ServeAsync(ctx, "gas", tGas, ct => Box(read.GetGasAsync(ct))));

        app.MapGet("/api/onchain", async (HttpContext ctx, string? asset, ApiReadRepository read) =>
        {
            var a = (asset ?? "btc").ToLowerInvariant();
            return await ServeAsync(ctx, $"onchain:{a}", tOnChain, ct => Box(read.GetOnChainAsync(a, ct)));
        });

        app.MapGet("/api/whales", async (HttpContext ctx, int? limit, ApiReadRepository read) =>
        {
            var n = Math.Clamp(limit ?? 50, 1, 200);
            return await ServeAsync(ctx, $"whales:{n}", tWhales, ct => Box(read.GetWhalesAsync(n, ct)));
        });

        app.MapGet("/api/liquidations", async (HttpContext ctx, int? limit, ApiReadRepository read) =>
        {
            var n = Math.Clamp(limit ?? 50, 1, 200);
            return await ServeAsync(ctx, $"liquidations:{n}", tLiquidations, ct => Box(read.GetLiquidationsAsync(n, ct)));
        });

        // ── Shared AI digests: one model call on the server, read by every user ────
        app.MapGet("/api/digests", async (HttpContext ctx, string? kind, int? limit, AiDigestRepository digests) =>
        {
            var n = Math.Clamp(limit ?? 20, 1, 100);
            return await ServeAsync(ctx, $"digests:{kind ?? "*"}:{n}", tDigests,
                ct => Box(digests.ListRecentAsync(kind, n, ct)));
        });

        // ── Cache observability: proves the fan-in is working, and cheap to scrape ─
        app.MapGet("/api/cache/stats", (SharedResponseCache cache) => Results.Ok(new
        {
            cache.Hits,
            cache.Misses,
            cache.SingleFlightJoins,
            entries = cache.Count,
            // Share of shared-endpoint requests that did NOT touch Postgres.
            savedRatio = cache.Hits + cache.Misses + cache.SingleFlightJoins == 0
                ? 0d
                : Math.Round((double)(cache.Hits + cache.SingleFlightJoins)
                             / (cache.Hits + cache.Misses + cache.SingleFlightJoins), 4)
        }));
    }

    /// <summary>Adapt a typed repository call to the cache factory's <c>object?</c> contract.</summary>
    private static async Task<object?> Box<T>(Task<T> task) => await task.ConfigureAwait(false);

    private static TimeSpan Ttl(IConfiguration cfg, string name, int defaultSeconds)
        => TimeSpan.FromSeconds(
            int.TryParse(cfg[$"CACHE_TTL_{name}"], out var s) && s > 0 ? s : defaultSeconds);

    /// <summary>
    /// Serve <paramref name="key"/> from the shared cache, honouring <c>If-None-Match</c>.
    ///
    /// A loader returning null serializes to <c>"null"</c> and is answered 404 — and stays
    /// cached for the TTL, which is wanted: a sweep for tokens we don't track can't turn into
    /// a database read per request.
    /// </summary>
    private static async Task<IResult> ServeAsync(
        HttpContext ctx, string key, TimeSpan ttl, Func<CancellationToken, Task<object?>> load)
    {
        var cache = ctx.RequestServices.GetRequiredService<SharedResponseCache>();

        var payload = await cache.GetOrCreateAsync(
            key, ttl,
            async ct => JsonSerializer.Serialize(await load(ct).ConfigureAwait(false)),
            ctx.RequestAborted);

        ctx.Response.Headers.ETag = payload.ETag;
        ctx.Response.Headers.CacheControl = $"private, max-age={(int)Math.Max(1, ttl.TotalSeconds)}";

        if (payload.Json is "null")
            return Results.NotFound(new { error = "not tracked" });

        var ifNoneMatch = ctx.Request.Headers.IfNoneMatch;
        foreach (var header in ifNoneMatch)
        {
            if (header is null) continue;
            foreach (var tag in header.Split(','))
            {
                if (string.Equals(tag.Trim(), payload.ETag, StringComparison.Ordinal))
                    return Results.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        return Results.Content(payload.Json, "application/json", Encoding.UTF8);
    }
}
